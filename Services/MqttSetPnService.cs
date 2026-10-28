using System.Collections.Concurrent;
using System.Diagnostics;
using LightGatewayScanner.Models;
using Microsoft.Maui.Storage;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace LightGatewayScanner.Services;

/// <summary>
/// MQTT 常驻长连接 + 设置 PN 请求/回执配对。
/// 协议：
///   发布 server/{网关ID}/cmd/set_pn   {"Seq":1782373476,"Data":{"PN":"PN123"}}
///   订阅 device/+/cmd/reply/pn        {"Seq":1,"Data":{"CmdSeq":1782373476,"Code":1,"Msg":null,"CmdData":{"GetPN":"PN123"}}}
/// 说明：MQTTnet 5.x 已移除 ManagedMqttClient，重连逻辑在这里自行实现。
/// </summary>
public sealed class MqttSetPnService : IMqttSetPnService, IAsyncDisposable
{
    private const string ClientIdKey = "mqtt_client_id";
    private const int MaxReconnectAttempts = 10;

    private readonly IAppConfigService _configService;
    private readonly IAppLogService _logService;
    private readonly MqttClientFactory _factory = new();

    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly ConcurrentDictionary<long, PendingRequest> _pending = new();

    private IMqttClient? _client;
    private string _signatureInUse = string.Empty;
    private string? _lastError;
    private long _lastSeq;
    private int _reconnectRunning;
    private bool _disposing;

    public MqttSetPnService(IAppConfigService configService, IAppLogService logService)
    {
        _configService = configService;
        _logService = logService;
        ClientId = LoadOrCreateClientId();

        _configService.ConfigChanged += (_, _) => OnConfigChanged();
    }

    public bool IsConnected => _client?.IsConnected == true;

    public string Endpoint => $"{_configService.Current.MqttHost}:{_configService.Current.MqttPort}";

    public event EventHandler? StateChanged;

    private string ClientId { get; }

    // ---------- 对外 ----------

    public void Start() => _ = EnsureConnectedAsync(TimeSpan.FromSeconds(10), _lifetimeCts.Token);

    public async Task<SetPnResult> SetPnAsync(string gatewayId, string pn, string? deviceMac = null,
        CancellationToken cancellationToken = default)
    {
        var config = _configService.Current;
        gatewayId = (gatewayId ?? string.Empty).Trim();
        pn = (pn ?? string.Empty).Trim();
        var mac = MqttReplyParser.NormalizeMac(deviceMac);

        if (gatewayId.Length == 0 || pn.Length == 0)
            return SetPnResult.Fail("网关 ID 或储能 PN 为空，未发送消息");

        if (!MqttReplyParser.IsValidGatewayIdForTopic(gatewayId))
            return SetPnResult.Fail($"网关 ID 含非法字符（/ + #），无法拼主题：{gatewayId}");

        var stopwatch = Stopwatch.StartNew();

        // 发布前必须确认"已连接且已订阅"，否则回执无人接收
        if (!await EnsureConnectedAsync(TimeSpan.FromSeconds(5), cancellationToken))
        {
            stopwatch.Stop();
            return SetPnResult.Fail(
                $"MQTT 未连接（{Endpoint}）：{_lastError ?? "无法连接 Broker"}",
                elapsedMs: stopwatch.ElapsedMilliseconds,
                deviceMac: mac);
        }

        var seq = NextSeq();
        var pending = new PendingRequest(gatewayId, pn);

        // 先登记再发布：否则网关回执可能早于登记到达而被丢弃
        _pending[seq] = pending;

        try
        {
            var topic = MqttReplyParser.BuildCommandTopic(gatewayId);
            var payload = MqttReplyParser.BuildCommandPayload(seq, pn, mac);

            await _logService.WriteAsync($"SetPn(MQTT) 发布 topic={topic} seq={seq} pn={pn} mac={mac ?? "-"}");

            var published = await PublishAsync(topic, payload, cancellationToken);
            if (!published.Success)
            {
                stopwatch.Stop();
                var failed = SetPnResult.Fail($"消息发送失败：{published.Error}", seq,
                    elapsedMs: stopwatch.ElapsedMilliseconds, deviceMac: mac);
                await _logService.WriteAsync($"SetPn(MQTT) 发送失败 gateway={gatewayId} pn={pn} seq={seq} 原因={published.Error}");
                return failed;
            }

            var timeout = TimeSpan.FromSeconds(Math.Clamp(config.MqttReplyTimeoutSeconds, 1, 60));
            var reply = await pending.Task.WaitAsync(timeout, cancellationToken);
            stopwatch.Stop();

            var result = reply.Success
                ? SetPnResult.Ok(reply.Message, reply.Seq, reply.Code, reply.GatewayMessage, reply.RawResponse,
                    stopwatch.ElapsedMilliseconds, mac)
                : SetPnResult.Fail(reply.Message, reply.Seq, reply.Code, reply.GatewayMessage, reply.RawResponse,
                    stopwatch.ElapsedMilliseconds, mac);

            await _logService.WriteAsync(
                $"SetPn(MQTT) {(result.Success ? "成功" : "失败")} gateway={gatewayId} pn={pn} seq={seq} mac={mac ?? "-"} 耗时={result.ElapsedMs}ms 回执={reply.RawResponse}");

            return result;
        }
        catch (TimeoutException)
        {
            stopwatch.Stop();
            var seconds = Math.Clamp(config.MqttReplyTimeoutSeconds, 1, 60);
            await _logService.WriteAsync($"SetPn(MQTT) 回执超时 gateway={gatewayId} pn={pn} seq={seq} 等待={seconds}s");
            return SetPnResult.Fail(
                $"等待网关回执超时（>{seconds}s），未收到 {MqttReplyParser.BuildReplyTopic(gatewayId)}",
                seq, elapsedMs: stopwatch.ElapsedMilliseconds, deviceMac: mac);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return SetPnResult.Fail("请求已取消", seq, elapsedMs: stopwatch.ElapsedMilliseconds, deviceMac: mac);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            await _logService.WriteAsync($"SetPn(MQTT) 异常 gateway={gatewayId} pn={pn} seq={seq}", ex);
            return SetPnResult.Fail($"请求异常：{ex.Message}", seq, elapsedMs: stopwatch.ElapsedMilliseconds, deviceMac: mac);
        }
        finally
        {
            // 超时后迟到的回执自然被丢弃，只写日志
            _pending.TryRemove(seq, out _);
        }
    }

    public async Task<string> ProbeAsync(string host, int port, string? username, string? password, CancellationToken cancellationToken = default)
    {
        host = (host ?? string.Empty).Trim();
        if (host.Length == 0) return "MQTT 服务器地址不能为空";
        if (port < 1 || port > 65535) return "MQTT 端口需在 1~65535 之间";

        var client = _factory.CreateMqttClient();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(8));

            var options = BuildOptions(host, port, username, password, $"lgw-probe-{Guid.NewGuid():N}"[..24]);
            var connect = await client.ConnectAsync(options, cts.Token);
            if (connect.ResultCode != MqttClientConnectResultCode.Success)
                return $"连接被拒绝：{connect.ResultCode}{DescribeConnectCode(connect.ResultCode)}";

            var subscribe = await SubscribeAsync(client, cts.Token);
            if (subscribe is not null)
                return $"已连接 {host}:{port}，但订阅 {MqttReplyParser.ReplyTopicFilter} 失败：{subscribe}（请检查 Broker 的 ACL 权限）";

            await client.DisconnectAsync();
            return $"已连接 {host}:{port}，订阅 {MqttReplyParser.ReplyTopicFilter} 成功（仅连通性测试，未发送写入）";
        }
        catch (OperationCanceledException)
        {
            return $"连接 {host}:{port} 超时，请确认手机已连内网、Broker 已启动";
        }
        catch (Exception ex)
        {
            return DescribeException(ex, host, port);
        }
        finally
        {
            try { client.Dispose(); } catch { /* 忽略 */ }
        }
    }

    // ---------- 连接管理 ----------

    private async Task<bool> EnsureConnectedAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (IsConnected) return true;

        if (!await _connectGate.WaitAsync(timeout, cancellationToken).ConfigureAwait(false))
            return IsConnected;

        try
        {
            if (IsConnected) return true;

            var config = _configService.Current;
            var host = config.MqttHost?.Trim() ?? string.Empty;
            var port = config.MqttPort;
            if (host.Length == 0 || port < 1 || port > 65535)
            {
                _lastError = "MQTT 配置不完整（服务器地址 / 端口）";
                return false;
            }

            var client = GetOrCreateClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCts.Token);
            cts.CancelAfter(timeout);

            var options = BuildOptions(host, port, config.MqttUsername, config.MqttPassword, ClientId);
            var result = await client.ConnectAsync(options, cts.Token).ConfigureAwait(false);

            if (result.ResultCode != MqttClientConnectResultCode.Success)
            {
                _lastError = $"Broker 拒绝连接：{result.ResultCode}{DescribeConnectCode(result.ResultCode)}";
                await _logService.WriteAsync($"MQTT 连接失败 {host}:{port}：{_lastError}");
                return false;
            }

            var subscribeError = await SubscribeAsync(client, cts.Token).ConfigureAwait(false);
            if (subscribeError is not null)
            {
                _lastError = subscribeError;
                await _logService.WriteAsync($"MQTT 订阅失败：{subscribeError}");
                return false;
            }

            _signatureInUse = BuildSignature(config.MqttHost, config.MqttPort, config.MqttUsername, config.MqttPassword);
            _lastError = null;
            await _logService.WriteAsync($"MQTT 已连接 {host}:{port}，已订阅 {MqttReplyParser.ReplyTopicFilter}");
            NotifyState();
            return true;
        }
        catch (OperationCanceledException)
        {
            _lastError = $"连接 {Endpoint} 超时";
            return false;
        }
        catch (Exception ex)
        {
            _lastError = DescribeException(ex, _configService.Current.MqttHost, _configService.Current.MqttPort);
            await _logService.WriteAsync($"MQTT 连接异常：{_lastError}", ex);
            return false;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private IMqttClient GetOrCreateClient()
    {
        if (_client is not null) return _client;

        var client = _factory.CreateMqttClient();
        // 回调必须在 ConnectAsync 之前注册，否则连接瞬间的消息无人接收
        client.ApplicationMessageReceivedAsync += OnApplicationMessageReceivedAsync;
        client.DisconnectedAsync += OnDisconnectedAsync;
        _client = client;
        return client;
    }

    private MqttClientOptions BuildOptions(string host, int port, string? username, string? password, string clientId)
    {
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(host, port)
            .WithClientId(clientId)
            // 现场 Broker 多为 3.1.1；CleanSession=true 每次连上都重新订阅，行为最可预期
            .WithProtocolVersion(MqttProtocolVersion.V311)
            .WithCleanSession(true)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30));

        if (!string.IsNullOrWhiteSpace(username))
            builder.WithCredentials(username.Trim(), password ?? string.Empty);

        return builder.Build();
    }

    /// <summary>订阅回执主题；成功返回 null，失败返回原因</summary>
    private static async Task<string?> SubscribeAsync(IMqttClient client, CancellationToken cancellationToken)
    {
        var options = new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(filter => filter
                .WithTopic(MqttReplyParser.ReplyTopicFilter)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
            .Build();

        var result = await client.SubscribeAsync(options, cancellationToken).ConfigureAwait(false);

        foreach (var item in result.Items)
        {
            switch (item.ResultCode)
            {
                case MqttClientSubscribeResultCode.GrantedQoS0:
                case MqttClientSubscribeResultCode.GrantedQoS1:
                case MqttClientSubscribeResultCode.GrantedQoS2:
                    continue;
                default:
                    return $"订阅被拒绝（{item.ResultCode}）";
            }
        }

        return null;
    }

    private async Task<MqttPublishOutcome> PublishAsync(string topic, string payload, CancellationToken cancellationToken)
    {
        var client = _client;
        if (client?.IsConnected != true) return MqttPublishOutcome.Fail("MQTT 未连接");

        try
        {
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .WithRetainFlag(false)
                .Build();

            var result = await client.PublishAsync(message, cancellationToken).ConfigureAwait(false);

            // QoS1 的 PUBACK 非成功原因码不会抛异常，必须显式检查
            return result.IsSuccess
                ? MqttPublishOutcome.Ok()
                : MqttPublishOutcome.Fail($"Broker 未接收（{result.ReasonCode}）");
        }
        catch (Exception ex)
        {
            return MqttPublishOutcome.Fail($"发送异常：{ex.Message}");
        }
    }

    // ---------- 回执处理 ----------

    private Task OnApplicationMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        var payload = e.ApplicationMessage.ConvertPayloadToString();

        if (!MqttReplyParser.TryParse(topic, payload, out var reply, out var error))
        {
            _ = _logService.WriteAsync($"MQTT 回执忽略（{error}）topic={topic}");
            return Task.CompletedTask;
        }

        if (!_pending.TryGetValue(reply.CmdSeq, out var pending))
        {
            _ = _logService.WriteAsync($"MQTT 回执无匹配请求 CmdSeq={reply.CmdSeq} topic={topic}");
            return Task.CompletedTask;
        }

        if (!string.Equals(reply.TopicGatewayId, pending.GatewayId, StringComparison.Ordinal))
        {
            _ = _logService.WriteAsync(
                $"MQTT 回执网关不一致 topic={topic} 期望={pending.GatewayId} 实际={reply.TopicGatewayId}");
            return Task.CompletedTask;
        }

        pending.TryComplete(MqttReplyParser.ToResult(reply, pending.Pn));
        return Task.CompletedTask;
    }

    private Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs e)
    {
        NotifyState();

        if (_disposing || !e.ClientWasConnected) return Task.CompletedTask;

        // 断线重连放到线程池，避免占用 MQTT 内部事件管线
        _ = Task.Run(ReconnectLoopAsync);
        return Task.CompletedTask;
    }

    private async Task ReconnectLoopAsync()
    {
        if (Interlocked.CompareExchange(ref _reconnectRunning, 1, 0) == 1) return;

        try
        {
            for (var attempt = 0; attempt < MaxReconnectAttempts; attempt++)
            {
                if (_disposing || _lifetimeCts.IsCancellationRequested) return;

                var delay = TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(attempt, 5)));
                await Task.Delay(delay, _lifetimeCts.Token).ConfigureAwait(false);

                if (await EnsureConnectedAsync(TimeSpan.FromSeconds(10), _lifetimeCts.Token).ConfigureAwait(false))
                {
                    await _logService.WriteAsync($"MQTT 重连成功（{Endpoint}）");
                    return;
                }
            }

            await _logService.WriteAsync($"MQTT 重连失败（已尝试 {MaxReconnectAttempts} 次），下次提交时会再次尝试");
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
        finally
        {
            Volatile.Write(ref _reconnectRunning, 0);
        }
    }

    private void OnConfigChanged()
    {
        var config = _configService.Current;
        var signature = BuildSignature(config.MqttHost, config.MqttPort, config.MqttUsername, config.MqttPassword);
        if (signature == _signatureInUse) return;   // 只改回执超时不重连

        _signatureInUse = signature;

        _ = Task.Run(async () =>
        {
            try
            {
                if (_client?.IsConnected == true) await _client.DisconnectAsync().ConfigureAwait(false);
            }
            catch
            {
                // 断开失败不影响后续重连
            }

            await EnsureConnectedAsync(TimeSpan.FromSeconds(10), _lifetimeCts.Token).ConfigureAwait(false);
        });
    }

    // ---------- 工具 ----------

    /// <summary>
    /// Seq = Unix 秒级时间戳（如 1782373476）；同一秒内多次请求时自增 1 保证唯一。
    /// </summary>
    private long NextSeq()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        while (true)
        {
            var last = Interlocked.Read(ref _lastSeq);
            var next = now > last ? now : last + 1;
            if (Interlocked.CompareExchange(ref _lastSeq, next, last) == last) return next;
        }
    }

    private static string BuildSignature(string? host, int port, string? username, string? password)
        => $"{host?.Trim()}|{port}|{username?.Trim()}|{password}";

    private static string LoadOrCreateClientId()
    {
        try
        {
            var id = Preferences.Default.Get(ClientIdKey, string.Empty);
            if (!string.IsNullOrWhiteSpace(id)) return id;

            id = $"lgw-scanner-{Guid.NewGuid():N}"[..24];
            Preferences.Default.Set(ClientIdKey, id);
            return id;
        }
        catch
        {
            return $"lgw-scanner-{Guid.NewGuid():N}"[..24];
        }
    }

    private static string DescribeConnectCode(MqttClientConnectResultCode code) => code switch
    {
        MqttClientConnectResultCode.BadUserNameOrPassword => "（请核对用户名 / 密码）",
        MqttClientConnectResultCode.NotAuthorized => "（该账号无权连接，请检查 Broker 权限）",
        MqttClientConnectResultCode.UnsupportedProtocolVersion => "（Broker 不支持当前 MQTT 协议版本）",
        MqttClientConnectResultCode.ClientIdentifierNotValid => "（ClientId 不合法或已被占用）",
        _ => string.Empty
    };

    private static string DescribeException(Exception ex, string host, int port)
    {
        var text = ex.ToString();

        if (text.Contains("refused", StringComparison.OrdinalIgnoreCase))
            return $"连接被拒绝（{host}:{port}），Broker 可能未启动";
        if (text.Contains("No such host", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Name or service not known", StringComparison.OrdinalIgnoreCase))
            return $"无法解析主机 {host}，请确认已连接内网 WiFi";
        if (text.Contains("Network is unreachable", StringComparison.OrdinalIgnoreCase))
            return "网络不可达，请检查手机网络与内网路由";
        if (text.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            return $"连接 {host}:{port} 超时，请检查网络与 Broker";

        return ex.Message;
    }

    private void NotifyState()
    {
        try { StateChanged?.Invoke(this, EventArgs.Empty); } catch { /* 忽略订阅方异常 */ }
    }

    public async ValueTask DisposeAsync()
    {
        _disposing = true;
        _lifetimeCts.Cancel();

        try
        {
            if (_client?.IsConnected == true) await _client.DisconnectAsync().ConfigureAwait(false);
        }
        catch
        {
            // 忽略
        }

        _client?.Dispose();
        _connectGate.Dispose();
        _lifetimeCts.Dispose();
    }

    private readonly record struct MqttPublishOutcome(bool Success, string? Error)
    {
        public static MqttPublishOutcome Ok() => new(true, null);
        public static MqttPublishOutcome Fail(string error) => new(false, error);
    }

    private sealed class PendingRequest
    {
        private readonly TaskCompletionSource<SetPnResult> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PendingRequest(string gatewayId, string pn)
        {
            GatewayId = gatewayId;
            Pn = pn;
        }

        public string GatewayId { get; }

        public string Pn { get; }

        public Task<SetPnResult> Task => _tcs.Task;

        public bool TryComplete(SetPnResult result) => _tcs.TrySetResult(result);
    }
}
