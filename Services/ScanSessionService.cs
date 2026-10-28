using LightGatewayScanner.Models;

namespace LightGatewayScanner.Services;

/// <summary>
/// 扫码会话状态机 + 提交编排：
/// Empty → GatewayOnly / PnOnly → Ready（两个码都拿到才允许提交）。
/// 负责去重、顺序校验、类型冲突处理，以及"在线校验 → MQTT 写入"的串联。
/// </summary>
public sealed class ScanSessionService : IScanSessionService
{
    private readonly IAppConfigService _configService;
    private readonly IScanClassifier _classifier;
    private readonly IRedisOnlineService _redisOnlineService;
    private readonly IMqttSetPnService _mqttSetPnService;
    private readonly IBleScanner _bleScanner;
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
        IMqttSetPnService mqttSetPnService,
        IBleScanner bleScanner,
        IAppLogService logService)
    {
        _configService = configService;
        _classifier = classifier;
        _redisOnlineService = redisOnlineService;
        _mqttSetPnService = mqttSetPnService;
        _bleScanner = bleScanner;
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
        {
            // 二维码里经常塞网址或 JSON，给出明确线索，方便现场判断是不是码内容本身不对
            var tip = code.Contains("://", StringComparison.Ordinal) || code.Contains('{')
                ? "（内容像网址或 JSON，请确认码里只放编码本身）"
                : string.Empty;
            return ScanAcceptOutcome.Fail(ScanAcceptStatus.Unknown, $"无法识别的条码：{code}{tip}");
        }

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

    /// <summary>
    /// API 模式：把网关 ID 直接写入会话槽位（不走扫码分类，因为它是接口返回的 deviceId）。
    /// </summary>
    public bool SetGatewayId(string gatewayId)
    {
        var id = (gatewayId ?? string.Empty).Trim();
        if (id.Length == 0) return false;

        _session.GatewayId = id;
        _session.GatewayScannedAt = DateTime.Now;
        ArmedSlot = null;
        return true;
    }

    /// <summary>API 模式：切换老化架后旧网关 ID 失效，清掉它（不动储能 PN）</summary>
    public void ClearGatewayId()
    {
        _session.GatewayId = null;
        _session.GatewayScannedAt = null;
        ArmedSlot = null;
    }

    public async Task<SubmitOutcome> SubmitAsync(bool force = false, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
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

            // ---------- 按 PN 扫描蓝牙取 MAC（Redis 校验通过后、发布之前） ----------
            var ble = BleScanResult.Skipped("未开启蓝牙扫描");

            if (config.BleScanEnabled)
            {
                progress?.Report("正在扫描蓝牙…");
                try
                {
                    ble = await _bleScanner.FindByAdvertisedNameAsync(
                        pn,
                        Math.Clamp(config.BleScanTimeoutSeconds, 1, 15),
                        progress,
                        cancellationToken);

                    await _logService.WriteAsync(
                        $"BLE 扫描 {(ble.Found ? "命中" : "未命中")} pn={pn} mac={ble.Mac ?? "-"} " +
                        $"rssi={ble.Rssi} 缓存={ble.FromCache} 同名设备={ble.DeviceCount} " +
                        $"耗时={ble.ElapsedMs}ms 说明={ble.Reason ?? "-"}");
                }
                catch (OperationCanceledException)
                {
                    await _logService.WriteAsync($"提交在蓝牙扫描阶段被取消 gateway={gatewayId} pn={pn}");
                    return new SubmitOutcome
                    {
                        Success = false,
                        Message = "提交已取消（蓝牙扫描阶段）",
                        OnlineStatus = check.Status,
                        GatewayId = gatewayId,
                        Pn = pn
                    };
                }
                catch (Exception ex)
                {
                    await _logService.WriteAsync($"BLE 扫描异常 pn={pn}", ex);
                    ble = BleScanResult.Miss(BleScanStatus.Error, $"蓝牙扫描异常：{ex.Message}");
                }
            }

            progress?.Report(ble.Found
                ? $"已获取 MAC {ble.Mac}，正在发送…"
                : "未扫到蓝牙，正在按 PN 发送…");

            var api = await _mqttSetPnService.SetPnAsync(gatewayId, pn, ble.Mac, cancellationToken);

            if (api.Success)
            {
                _lastSubmittedKey = $"{gatewayId}|{pn}";
            }
            else if (ble.FromCache)
            {
                // 缓存的 MAC 可能已过期（设备换随机地址等），失败后作废，下次重新扫描
                _bleScanner.InvalidateCache(pn);
                await _logService.WriteAsync($"网关回执失败，作废 MAC 缓存 pn={pn} mac={ble.Mac}");
            }

            return new SubmitOutcome
            {
                Success = api.Success,
                // 服务层只给原因文本，"写入失败："前缀统一在这里加，避免出现双前缀
                Message = api.Success
                    ? $"写入成功：网关 {gatewayId} ← PN {pn}"
                    : $"写入失败：{api.Message}",
                Detail = api.RawResponse,
                ServerMessage = api.GatewayMessage,
                Seq = api.Seq,
                ElapsedMs = api.ElapsedMs,
                DeviceMac = api.DeviceMac,
                BleNote = ble.Found ? null : ble.Reason,
                BleElapsedMs = ble.ElapsedMs,
                BleRssi = ble.Found ? ble.Rssi : 0,
                BleDeviceCount = ble.DeviceCount,
                BleFromCache = ble.FromCache,
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
