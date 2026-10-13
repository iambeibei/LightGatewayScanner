using System.Text.Json;
using LightGatewayScanner.Models;
using StackExchange.Redis;

namespace LightGatewayScanner.Services;

/// <summary>
/// Redis 在线校验服务。
/// 关键领域约束：lgw:devices:online 是 List，每个元素是 JSON 字符串 {"id":"100000010","type":"1"}，
/// 必须先反序列化取 id 再与扫码得到的网关 ID 比较——直接拿元素字符串比对会永远判定为离线。
/// </summary>
public sealed class RedisOnlineService : IRedisOnlineService, IAsyncDisposable
{
    private readonly IAppConfigService _configService;
    private readonly IAppLogService _logService;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly object _cacheLock = new();

    private ConnectionMultiplexer? _connection;
    private string _connectionStringInUse = string.Empty;

    private HashSet<string>? _cache;
    private DateTime _cacheTimestamp;

    public RedisOnlineService(IAppConfigService configService, IAppLogService logService)
    {
        _configService = configService;
        _logService = logService;
        _configService.ConfigChanged += (_, _) =>
        {
            InvalidateCache();
            ResetConnection();
        };
    }

    public async Task<OnlineCheckResult> CheckAsync(string gatewayId, CancellationToken cancellationToken = default)
    {
        var config = _configService.Current;

        if (string.IsNullOrWhiteSpace(gatewayId))
            return OnlineCheckResult.BadConfig("网关 ID 为空，无法校验在线状态");
        if (string.IsNullOrWhiteSpace(config.OnlineListKey))
            return OnlineCheckResult.BadConfig("未配置 Redis 在线列表 Key");
        if (string.IsNullOrWhiteSpace(config.RedisConnectionString))
            return OnlineCheckResult.BadConfig("未配置 Redis 连接串");

        var cached = TryGetCache(config);
        if (cached is not null)
            return Judge(cached, gatewayId, fromCache: true);

        var (ids, error) = await TryLoadOnlineIdsAsync(config, cancellationToken);
        if (error is not null) return error;

        SetCache(config, ids!);
        return Judge(ids!, gatewayId, fromCache: false);
    }

    public async Task<RedisTestResult> TestAsync(CancellationToken cancellationToken = default)
    {
        var config = _configService.Current;
        var (ids, error) = await TryLoadOnlineIdsAsync(config, cancellationToken);

        if (error is not null)
            return new RedisTestResult { Success = false, Message = error.Message };

        var all = ids!;
        var samples = all.OrderBy(x => x, StringComparer.Ordinal).Take(5).ToArray();

        return new RedisTestResult
        {
            Success = true,
            Message = $"连接成功，在线设备 {all.Count} 台"
                      + (samples.Length > 0 ? $"。示例：{string.Join("、", samples)}" : string.Empty),
            OnlineCount = all.Count,
            SampleIds = samples
        };
    }

    public void InvalidateCache()
    {
        lock (_cacheLock)
        {
            _cache = null;
            _cacheTimestamp = default;
        }
    }

    public void ResetConnection()
    {
        try
        {
            _connection?.Dispose();
        }
        catch { /* 忽略释放异常 */ }

        _connection = null;
        _connectionStringInUse = string.Empty;
    }

    // ---------- 内部实现 ----------

    private async Task<(HashSet<string>? Ids, OnlineCheckResult? Error)> TryLoadOnlineIdsAsync(
        AppConfig config, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(config.RedisTimeoutMs, 500, 60000));

        try
        {
            var connection = await GetConnectionAsync(config).WaitAsync(timeout, cancellationToken);
            var database = connection.GetDatabase();

            var values = await database
                .ListRangeAsync(config.OnlineListKey, 0, -1)
                .WaitAsync(timeout, cancellationToken);

            return (ParseOnlineIds(values), null);
        }
        catch (RedisConnectionException ex)
        {
            var msg = $"无法连接 Redis（{config.RedisConnectionString}），请检查网络与配置";
            await _logService.WriteAsync(msg, ex);
            return (null, OnlineCheckResult.Unavailable(msg));
        }
        catch (RedisTimeoutException ex)
        {
            var msg = $"Redis 响应超时（>{config.RedisTimeoutMs}ms）";
            await _logService.WriteAsync(msg, ex);
            return (null, OnlineCheckResult.Unavailable(msg));
        }
        catch (RedisServerException ex)
        {
            return (null, await TranslateServerException(ex, config));
        }
        catch (RedisCommandException ex)
        {
            return (null, await TranslateServerException(ex, config));
        }
        catch (TimeoutException)
        {
            var msg = $"Redis 查询超时（>{config.RedisTimeoutMs}ms），已阻止提交";
            await _logService.WriteAsync(msg);
            return (null, OnlineCheckResult.Unavailable(msg));
        }
        catch (OperationCanceledException)
        {
            var msg = "Redis 查询已取消或超时";
            await _logService.WriteAsync(msg);
            return (null, OnlineCheckResult.Unavailable(msg));
        }
        catch (ArgumentException ex)
        {
            var msg = $"Redis 连接串格式不正确：{ex.Message}";
            await _logService.WriteAsync(msg, ex);
            return (null, OnlineCheckResult.BadConfig(msg));
        }
        catch (Exception ex)
        {
            var msg = $"Redis 查询失败：{ex.Message}（已阻止提交）";
            await _logService.WriteAsync(msg, ex);
            return (null, OnlineCheckResult.Unavailable(msg));
        }
    }

    private async Task<OnlineCheckResult> TranslateServerException(Exception ex, AppConfig config)
    {
        var text = ex.Message ?? string.Empty;

        if (text.Contains("WRONGTYPE", StringComparison.OrdinalIgnoreCase))
        {
            var msg = $"Redis Key「{config.OnlineListKey}」不是 List 类型，请检查配置";
            await _logService.WriteAsync(msg, ex);
            return OnlineCheckResult.BadConfig(msg);
        }

        if (text.Contains("NOAUTH", StringComparison.OrdinalIgnoreCase)
            || text.Contains("WRONGPASS", StringComparison.OrdinalIgnoreCase)
            || text.Contains("invalid password", StringComparison.OrdinalIgnoreCase))
        {
            var msg = "Redis 认证失败，请检查连接串中的密码";
            await _logService.WriteAsync(msg, ex);
            return OnlineCheckResult.Unavailable(msg);
        }

        var generic = $"Redis 返回错误：{text}";
        await _logService.WriteAsync(generic, ex);
        return OnlineCheckResult.Unavailable(generic);
    }

    private static HashSet<string> ParseOnlineIds(RedisValue[] values)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var value in values)
        {
            if (!value.HasValue) continue;

            var json = value.ToString();
            if (string.IsNullOrWhiteSpace(json)) continue;

            var text = json.Trim();

            // 元素是 JSON：{"id":"100000010","type":"1"} → 取 id
            if (text.StartsWith('{'))
            {
                try
                {
                    var device = JsonSerializer.Deserialize<OnlineDevice>(text);
                    if (device is not null && !string.IsNullOrWhiteSpace(device.Id))
                        ids.Add(device.Id.Trim());
                }
                catch (JsonException)
                {
                    // 非法元素跳过，不中断整体判断
                }
                continue;
            }

            // 兼容：元素本身就是纯 ID 的情况
            ids.Add(text);
        }

        return ids;
    }

    private static OnlineCheckResult Judge(HashSet<string> ids, string gatewayId, bool fromCache)
    {
        var suffix = fromCache ? "（缓存）" : string.Empty;

        if (ids.Count == 0)
            return OnlineCheckResult.NotOnline($"在线设备列表为空，网关 {gatewayId} 判定为不在线{suffix}", 0, fromCache);

        if (ids.Contains(gatewayId))
            return OnlineCheckResult.Ok($"网关 {gatewayId} 在线{suffix}", ids.Count, fromCache);

        return OnlineCheckResult.NotOnline(
            $"网关 {gatewayId} 当前不在线（在线设备 {ids.Count} 台）{suffix}", ids.Count, fromCache);
    }

    private HashSet<string>? TryGetCache(AppConfig config)
    {
        if (config.OnlineCacheSeconds <= 0) return null;

        lock (_cacheLock)
        {
            if (_cache is null) return null;
            if (DateTime.Now - _cacheTimestamp > TimeSpan.FromSeconds(config.OnlineCacheSeconds))
            {
                _cache = null;
                return null;
            }
            return _cache;
        }
    }

    private void SetCache(AppConfig config, HashSet<string> ids)
    {
        if (config.OnlineCacheSeconds <= 0) return;

        lock (_cacheLock)
        {
            _cache = ids;
            _cacheTimestamp = DateTime.Now;
        }
    }

    private async Task<ConnectionMultiplexer> GetConnectionAsync(AppConfig config)
    {
        var connectionString = config.RedisConnectionString.Trim();

        if (_connection is { IsConnected: true } && _connectionStringInUse == connectionString)
            return _connection;

        await _connectionLock.WaitAsync();
        try
        {
            if (_connection is { IsConnected: true } && _connectionStringInUse == connectionString)
                return _connection;

            ResetConnection();

            var options = BuildOptions(config, connectionString);
            _connection = await ConnectionMultiplexer.ConnectAsync(options);
            _connectionStringInUse = connectionString;
            return _connection;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private static ConfigurationOptions BuildOptions(AppConfig config, string connectionString)
    {
        var options = ConfigurationOptions.Parse(connectionString);
        options.AbortOnConnectFail = false;      // 连不上也要返回对象，由后续命令抛异常来统一处理
        options.ConnectTimeout = Math.Clamp(config.RedisTimeoutMs, 1000, 30000);
        options.SyncTimeout = Math.Clamp(config.RedisTimeoutMs, 1000, 60000);
        options.ConnectRetry = 1;
        options.ClientName = "LightGatewayScanner";
        return options;
    }

    public async ValueTask DisposeAsync()
    {
        ResetConnection();
        _connectionLock.Dispose();
        await Task.CompletedTask;
    }
}
