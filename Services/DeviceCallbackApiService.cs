using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LightGatewayScanner.Models;

namespace LightGatewayScanner.Services;

/// <summary>
/// API 模式下用到的两个上位机接口：
/// ① DeviceServiceCallback/RackPageList        —— 老化架下拉框数据
/// ② DeviceServiceCallback/AvailablePointList  —— 点位下拉框数据（联动，仅第一个下拉框选中后才请求）
/// 接口地址可在设置页修改（RackPageListUrl / AvailablePointListUrl），默认指向 192.168.1.65:11282。
/// </summary>
public sealed class DeviceCallbackApiService : IDeviceCallbackApiService, IDisposable
{
    // 按需求固定的请求体
    private const string RackPageListBody = "{\"pageSize\":111110,\"pageIndex\":1}";
    private const int PointPageSize = 1000000;
    private const int PointPageIndex = 0;

    private readonly IAppConfigService _configService;
    private readonly IAppLogService _logService;

    private HttpClient? _httpClient;
    private string _urlInUse = string.Empty;
    private int _timeoutInUse;

    public DeviceCallbackApiService(IAppConfigService configService, IAppLogService logService)
    {
        _configService = configService;
        _logService = logService;
        _configService.ConfigChanged += (_, _) => RebuildIfNeeded();
    }

    public async Task<ApiListResult<AgingRackItem>> GetRackListAsync(CancellationToken cancellationToken = default)
    {
        var config = _configService.Current;
        if (!Uri.TryCreate(config.RackPageListUrl?.Trim(), UriKind.Absolute, out var uri))
            return ApiListResult<AgingRackItem>.Fail($"老化架接口地址不合法：{config.RackPageListUrl}");

        var result = await SendAsync(uri, RackPageListBody, MapRack, null, "RackPageList", cancellationToken);
        await _logService.WriteAsync($"RackPageList success={result.Success} total={result.Total} msg={result.Message}");
        return result;
    }

    public async Task<ApiListResult<AgingPointItem>> GetAvailablePointListAsync(string agingRackId, CancellationToken cancellationToken = default)
    {
        var config = _configService.Current;

        if (string.IsNullOrWhiteSpace(agingRackId))
            return ApiListResult<AgingPointItem>.Fail("未选择老化架，无法查询点位");

        if (!Uri.TryCreate(config.AvailablePointListUrl?.Trim(), UriKind.Absolute, out var uri))
            return ApiListResult<AgingPointItem>.Fail($"点位接口地址不合法：{config.AvailablePointListUrl}");

        // 注意匿名对象属性名保持小写，序列化出来就是接口要求的 {"agingRackId":..,"pageSize":..,"pageIndex":..,"onlyAvailable":..}
        var body = JsonSerializer.Serialize(new
        {
            agingRackId = agingRackId.Trim(),
            pageSize = PointPageSize,
            pageIndex = PointPageIndex,
            onlyAvailable = false
        });

        var result = await SendAsync(uri, body, MapPoint, FilterPoints, "AvailablePointList", cancellationToken);
        await _logService.WriteAsync($"AvailablePointList rack={agingRackId} success={result.Success} total={result.Total} msg={result.Message}");
        return result;
    }

    // ---------- 通用请求 ----------

    private async Task<ApiListResult<T>> SendAsync<T>(
        Uri uri,
        string body,
        Func<JsonElement, T?> mapper,
        Func<IReadOnlyList<T>, IReadOnlyList<T>>? postFilter,
        string tag,
        CancellationToken cancellationToken)
    {
        var config = _configService.Current;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var client = GetClient(config);
            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };

            using var response = await client.SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            stopwatch.Stop();

            var status = (int)response.StatusCode;
            if (string.IsNullOrWhiteSpace(text))
                return ApiListResult<T>.Fail($"服务未返回内容（HTTP {status}）", status);

            var raw = Truncate(text, 300);

            if (!response.IsSuccessStatusCode)
                return ApiListResult<T>.Fail($"服务端返回 HTTP {status}：{raw}", status, raw);

            if (!text.TrimStart().StartsWith('{'))
                return ApiListResult<T>.Fail($"响应不是 JSON：{raw}", status, raw);

            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;

            var message = ReadMessage(root);
            var codeText = ReadCodeText(root);

            // 1) 明确失败信号：success/ok 显式 false
            if (ReadNegativeFlag(root))
                return ApiListResult<T>.Fail(message ?? $"业务返回失败（{codeText ?? "无 code"}）", status, raw);

            // 2) 数值 code 不在成功码集合内 → 失败（与 SetPn 判定保持一致）
            if (codeText is not null && int.TryParse(codeText, out var code) && !IsSuccessCode(code, config))
                return ApiListResult<T>.Fail(message ?? $"业务返回 code={codeText}", status, raw);

            // 3) 解析 data 数组
            if (!root.TryGetProperty("data", out var dataNode) || dataNode.ValueKind != JsonValueKind.Array)
                return ApiListResult<T>.Fail(message ?? "响应中缺少 data 数组", status, raw);

            var items = new List<T>();
            foreach (var element in dataNode.EnumerateArray())
            {
                var item = mapper(element);
                if (item is not null) items.Add(item);
            }

            // 4) 后置过滤（点位接口用：剔除 deviceId 为 "0" 的项）
            IReadOnlyList<T> finalItems = postFilter is null ? items : postFilter(items);

            var total = root.TryGetProperty("total", out var totalNode) && totalNode.TryGetInt32(out var t)
                ? t
                : finalItems.Count;

            return ApiListResult<T>.Ok(finalItems, total, status, raw, message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return ApiListResult<T>.Fail($"请求超时（>{config.TimeoutSeconds}s），耗时 {stopwatch.ElapsedMilliseconds}ms");
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            var msg = TranslateHttpException(ex, uri);
            await _logService.WriteAsync($"{tag} 网络失败：{msg}", ex);
            return ApiListResult<T>.Fail(msg);
        }
        catch (OperationCanceledException)
        {
            return ApiListResult<T>.Fail("请求已取消");
        }
        catch (JsonException ex)
        {
            stopwatch.Stop();
            return ApiListResult<T>.Fail($"响应解析失败：{ex.Message}");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            await _logService.WriteAsync($"{tag} 异常", ex);
            return ApiListResult<T>.Fail($"请求异常：{ex.Message}");
        }
    }

    // ---------- 响应字段映射 ----------

    private static AgingRackItem? MapRack(JsonElement element)
    {
        var id = ReadText(element, "id");
        if (string.IsNullOrWhiteSpace(id)) return null;

        return new AgingRackItem
        {
            Id = id!,
            Name = ReadText(element, "name") ?? id,
            Remark = ReadText(element, "remark"),
            MasterControlId = ReadText(element, "masterControlId")
        };
    }

    private static AgingPointItem? MapPoint(JsonElement element)
    {
        // deviceId 是网关 ID，缺失的项过滤阶段会剔除，这里给个 "0" 占位
        var point = new AgingPointItem
        {
            Id = ReadText(element, "id") ?? string.Empty,
            PointSequence = ReadText(element, "pointSequence"),
            LayerNumber = ReadText(element, "layerNumber"),
            AgingRackId = ReadText(element, "agingRackId"),
            DeviceId = ReadText(element, "deviceId") ?? "0",
            DeviceCount = element.TryGetProperty("deviceCount", out var node) && node.TryGetInt32(out var count)
                ? count
                : 0
        };
        return point;
    }

    /// <summary>只保留 deviceId != "0"（且非空）的点位</summary>
    private static IReadOnlyList<AgingPointItem> FilterPoints(IReadOnlyList<AgingPointItem> items)
        => items.Where(p => !string.IsNullOrWhiteSpace(p.DeviceId)
                            && !string.Equals(p.DeviceId.Trim(), "0", StringComparison.Ordinal))
                .ToArray();

    /// <summary>读取字符串/数字/布尔型字段，统一转成字符串；字段缺失或为 null 时返回 null</summary>
    private static string? ReadText(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var node)) return null;

        return node.ValueKind switch
        {
            JsonValueKind.String => node.GetString()?.Trim(),
            JsonValueKind.Number => node.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static string? ReadCodeText(JsonElement root)
    {
        foreach (var name in new[] { "code", "errcode", "errCode", "errorCode", "statusCode", "retCode" })
        {
            if (!root.TryGetProperty(name, out var node)) continue;

            return node.ValueKind switch
            {
                JsonValueKind.Number => node.TryGetInt32(out var number) ? number.ToString() : node.GetRawText(),
                JsonValueKind.String => node.GetString(),
                _ => null
            };
        }
        return null;
    }

    private static string? ReadMessage(JsonElement root)
    {
        foreach (var name in new[] { "message", "msg", "error", "errorMessage", "resultMsg", "info" })
        {
            if (root.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String)
            {
                var value = node.GetString();
                if (!string.IsNullOrWhiteSpace(value)) return value!.Trim();
            }
        }
        return null;
    }

    private static bool ReadNegativeFlag(JsonElement root)
    {
        foreach (var name in new[] { "success", "ok", "succeeded" })
        {
            if (!root.TryGetProperty(name, out var node)) continue;

            if (node.ValueKind == JsonValueKind.False) return true;
            if (node.ValueKind == JsonValueKind.String)
            {
                var value = node.GetString();
                if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "fail", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "failed", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "error", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    private static bool IsSuccessCode(int code, AppConfig config)
    {
        var configured = (config.ApiSuccessCodes ?? string.Empty)
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x.Length > 0)
            .ToArray();

        foreach (var item in configured)
        {
            if (int.TryParse(item, out var success) && code == success) return true;
        }

        return configured.Length == 0 && (code == 0 || code == 1 || code == 200);
    }

    private static string TranslateHttpException(HttpRequestException ex, Uri uri)
    {
        var text = $"{ex.Message} {ex.InnerException?.Message}";

        if (text.Contains("certificate", StringComparison.OrdinalIgnoreCase)
            || text.Contains("SSL", StringComparison.OrdinalIgnoreCase)
            || text.Contains("trust", StringComparison.OrdinalIgnoreCase))
            return "证书校验失败，请在设置中开启『信任自签证书』或在手机上安装服务器证书";

        if (text.Contains("NameResolution", StringComparison.OrdinalIgnoreCase)
            || text.Contains("No such host", StringComparison.OrdinalIgnoreCase))
            return $"无法解析主机 {uri.Host}，请确认已连接内网 WiFi";

        if (text.Contains("refused", StringComparison.OrdinalIgnoreCase))
            return $"连接被拒绝（{uri.Host}:{uri.Port}），服务可能未启动";

        if (text.Contains("Network is unreachable", StringComparison.OrdinalIgnoreCase))
            return "网络不可达，请检查手机网络与内网路由";

        return $"网络请求失败：{ex.Message}";
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";

    // ---------- HttpClient 生命周期 ----------

    private HttpClient GetClient(AppConfig config)
    {
        RebuildIfNeeded();
        return _httpClient!;
    }

    private void RebuildIfNeeded()
    {
        var config = _configService.Current;
        var timeout = Math.Clamp(config.TimeoutSeconds, 1, 120);
        var key = $"{config.RackPageListUrl}|{config.AvailablePointListUrl}|{config.AllowInvalidCertificate}";

        if (_httpClient is not null
            && _urlInUse == key
            && _timeoutInUse == timeout)
            return;

        _httpClient?.Dispose();

        var handler = new HttpClientHandler();
        if (config.AllowInvalidCertificate)
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;

        _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeout) };
        _urlInUse = key;
        _timeoutInUse = timeout;
    }

    public void Dispose() => _httpClient?.Dispose();
}
