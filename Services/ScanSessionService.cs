using LightGatewayScanner.Models;

namespace LightGatewayScanner.Services;

/// <summary>
/// 扫码会话状态机 + 提交编排：
/// Empty → GatewayOnly / PnOnly → Ready（两个码都拿到才允许提交）。
/// 负责去重、顺序校验、类型冲突处理，以及"在线校验 → 调接口"的串联。
/// </summary>
public sealed class ScanSessionService : IScanSessionService
{
    private readonly IAppConfigService _configService;
    private readonly IScanClassifier _classifier;
    private readonly IRedisOnlineService _redisOnlineService;
    private readonly ISetPnApiService _setPnApiService;
    private readonly IAppLogService _logService;

    private readonly ScanSession _session = new();

    private string _lastCode = string.Empty;
    private DateTime _lastCodeAt = DateTime.MinValue;
    private string _lastSubmittedKey = string.Empty;

    private int _busy;

    public ScanSessionService(
        IAppConfigService configService,
        IScanClassifier classifier,
        IRedisOnlineService redisOnlineService,
        ISetPnApiService setPnApiService,
        IAppLogService logService)
    {
        _configService = configService;
        _classifier = classifier;
        _redisOnlineService = redisOnlineService;
        _setPnApiService = setPnApiService;
        _logService = logService;
    }

    public ScanSession Current => _session;

    public bool IsBusy => Volatile.Read(ref _busy) == 1;

    public ScanCodeType? ArmedSlot { get; private set; }

    public string LastSubmittedKey => _lastSubmittedKey;

    public void Arm(ScanCodeType? type) => ArmedSlot = type is null or ScanCodeType.Unknown ? null : type;

    public ScanAcceptOutcome Accept(string rawCode)
    {
        var config = _configService.Current;
        var code = _classifier.Normalize(rawCode);

        if (string.IsNullOrEmpty(code))
            return ScanAcceptOutcome.Fail(ScanAcceptStatus.Unknown, "扫到空条码，已忽略");

        if (IsBusy)
            return ScanAcceptOutcome.Fail(ScanAcceptStatus.Busy, "正在提交上一组数据，请稍候");

        if (!config.IsScanConfigured)
            return ScanAcceptOutcome.Fail(ScanAcceptStatus.NotConfigured, "请先在设置中配置储能设备字符串前缀");

        // 去重窗口：同一条码在窗口内重复出现直接忽略（ZXing 连续多帧会重复回调）
        var now = DateTime.Now;
        if (string.Equals(code, _lastCode, StringComparison.Ordinal)
            && (now - _lastCodeAt).TotalMilliseconds < Math.Max(0, config.ScanDedupMs))
        {
            return ScanAcceptOutcome.Fail(ScanAcceptStatus.Duplicate, $"重复扫描 {code}，已忽略");
        }

        _lastCode = code;
        _lastCodeAt = now;

        var type = _classifier.Classify(code, config);
        if (type == ScanCodeType.Unknown)
            return ScanAcceptOutcome.Fail(ScanAcceptStatus.Unknown, $"无法识别的条码：{code}");

        // 手动指定槽位时，只接受匹配的类型，避免把 PN 填进网关字段
        if (ArmedSlot is { } armed && armed != type)
        {
            var want = armed == ScanCodeType.GatewayId ? "网关 ID" : "储能设备 PN";
            return ScanAcceptOutcome.Fail(ScanAcceptStatus.WrongType, $"当前指定扫描的是{want}，扫到的是{Describe(type)}：{code}");
        }

        if (type == ScanCodeType.GatewayId)
        {
            if (_session.HasGateway)
            {
                if (string.Equals(_session.GatewayId, code, StringComparison.Ordinal))
                    return ScanAcceptOutcome.Fail(ScanAcceptStatus.Duplicate, $"网关 ID {code} 重复扫描，已忽略");

                if (!config.OverwriteDuplicate)
                    return ScanAcceptOutcome.Fail(ScanAcceptStatus.WrongType,
                        $"已扫描网关 ID {_session.GatewayId}，请扫描储能设备 PN（扫到的是 {code}）");
            }

            _session.GatewayId = code;
            _session.GatewayScannedAt = now;
        }
        else
        {
            if (config.RequireScanOrder && !_session.HasGateway)
                return ScanAcceptOutcome.Fail(ScanAcceptStatus.WrongOrder, "已开启『先扫网关』，请先扫描网关 ID");

            if (_session.HasPn)
            {
                if (string.Equals(_session.Pn, code, StringComparison.Ordinal))
                    return ScanAcceptOutcome.Fail(ScanAcceptStatus.Duplicate, $"储能 PN {code} 重复扫描，已忽略");

                if (!config.OverwriteDuplicate)
                    return ScanAcceptOutcome.Fail(ScanAcceptStatus.WrongType,
                        $"已扫描储能 PN {_session.Pn}，请扫描网关 ID（扫到的是 {code}）");
            }

            _session.Pn = code;
            _session.PnScannedAt = now;
        }

        ArmedSlot = null;

        var message = type == ScanCodeType.GatewayId
            ? $"已识别网关 ID：{code}"
            : $"已识别储能设备 PN：{code}";

        var ready = _session.IsReady;
        if (ready) message += "，可提交";

        return ScanAcceptOutcome.Ok(type, code, message, ready);
    }

    public async Task<SubmitOutcome> SubmitAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        var config = _configService.Current;

        if (!_session.IsReady)
            return new SubmitOutcome { Success = false, Message = "两个条码都扫到后才能提交" };

        if (Interlocked.CompareExchange(ref _busy, 1, 0) == 1)
            return new SubmitOutcome { Success = false, Message = "正在提交上一组数据，请勿重复点击" };

        var gatewayId = _session.GatewayId!.Trim();
        var pn = _session.Pn!.Trim();

        try
        {
            var check = await _redisOnlineService.CheckAsync(gatewayId, cancellationToken);

            if (!check.IsOnline)
            {
                var canForce = config.AllowSubmitWhenRedisUnavailable
                               && check.Status == OnlineCheckStatus.RedisUnavailable;

                if (canForce && !force)
                {
                    return new SubmitOutcome
                    {
                        Success = false,
                        Message = check.Message + "。是否仍要强制提交？",
                        CanForce = true,
                        OnlineStatus = check.Status,
                        GatewayId = gatewayId,
                        Pn = pn
                    };
                }

                if (!force)
                {
                    await _logService.WriteAsync($"提交被拦截 gateway={gatewayId} pn={pn} 原因={check.Message}");
                    return new SubmitOutcome
                    {
                        Success = false,
                        Message = check.Message,
                        OnlineStatus = check.Status,
                        GatewayId = gatewayId,
                        Pn = pn
                    };
                }

                await _logService.WriteAsync($"【强制提交】Redis 不可用仍写入 gateway={gatewayId} pn={pn}");
            }

            var api = await _setPnApiService.SetPnAsync(gatewayId, pn, cancellationToken);

            if (api.Success)
                _lastSubmittedKey = $"{gatewayId}|{pn}";

            return new SubmitOutcome
            {
                Success = api.Success,
                // SetPnApiService 的失败文案已带"写入失败："前缀，这里再包一层
                // 会出现"写入失败：写入失败：…"，所以先判断是否已带前缀
                Message = api.Success
                    ? $"写入成功：网关 {gatewayId} ← PN {pn}"
                    : api.Message.StartsWith("写入失败", StringComparison.Ordinal)
                        ? api.Message
                        : $"写入失败：{api.Message}",
                Detail = api.RawResponse,
                ServerMessage = api.ServerMessage,
                StatusCode = api.StatusCode,
                ElapsedMs = api.ElapsedMs,
                OnlineStatus = check.Status,
                GatewayId = gatewayId,
                Pn = pn
            };
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    public void Reset()
    {
        _session.Clear();
        ArmedSlot = null;
        _lastCode = string.Empty;
        _lastCodeAt = DateTime.MinValue;
    }

    private static string Describe(ScanCodeType type)
        => type == ScanCodeType.GatewayId ? "网关 ID" : "储能设备 PN";
}
