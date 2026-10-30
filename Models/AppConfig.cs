using System.Text.RegularExpressions;

namespace LightGatewayScanner.Models;

/// <summary>
/// APP 全部可配置项。持久化到 Preferences（Android → SharedPreferences），重启后生效。
/// </summary>
public sealed class AppConfig
{
    public const int CurrentVersion = 5;

    // ---------- 出厂默认值（同时用于旧版配置迁移） ----------
    /// <summary>MQTT Broker 地址（设置 PN 用）</summary>
    public const string DefaultMqttHost = "192.168.1.65";
    public const int DefaultMqttPort = 1883;
    public const string DefaultMqttUsername = "dev";
    public const string DefaultMqttPassword = "123456";
    /// <summary>等待网关回执的超时（秒），超时即判失败</summary>
    public const int DefaultMqttReplyTimeoutSeconds = 5;

    /// <summary>提交前按 PN 扫描 BLE 取 MAC 的默认超时（秒）</summary>
    public const int DefaultBleScanTimeoutSeconds = 5;

    /// <summary>RSSI 门限（dBm）：0 = 不过滤</summary>
    public const int DefaultBleMinRssi = 0;

    /// <summary>同一 PN 的 MAC 缓存时长（秒）：0 = 不缓存</summary>
    public const int DefaultBleMacCacheSeconds = 120;

    public const string DefaultApiSuccessCodes = "0,1,200";

    /// <summary>v1 时代的旧默认成功码（不认 code=1，会把成功判成失败）</summary>
    public const string LegacyApiSuccessCodes = "0,200";

    /// <summary>API 模式：老化架列表接口</summary>
    public const string DefaultRackPageListUrl = "http://192.168.1.65:11282/DeviceServiceCallback/RackPageList";

    /// <summary>API 模式：点位列表接口</summary>
    public const string DefaultAvailablePointListUrl = "http://192.168.1.65:11282/DeviceServiceCallback/AvailablePointList";

    /// <summary>构建标记，显示在设置页，用于确认手机装的是哪一版</summary>
    public const string BuildTag = "2026-10-29.1";

    public int ConfigVersion { get; set; } = CurrentVersion;

    // ---------- MQTT 配置（设置 PN） ----------
    public string MqttHost { get; set; } = DefaultMqttHost;
    public int MqttPort { get; set; } = DefaultMqttPort;
    public string MqttUsername { get; set; } = DefaultMqttUsername;
    public string MqttPassword { get; set; } = DefaultMqttPassword;
    /// <summary>发布后等待 device/{网关ID}/cmd/reply/pn 回执的超时（秒）</summary>
    public int MqttReplyTimeoutSeconds { get; set; } = DefaultMqttReplyTimeoutSeconds;

    // ---------- 蓝牙（BLE）MAC 扫描 ----------
    /// <summary>提交前按 PN 扫描 BLE，命中则把 MAC 一起下发</summary>
    public bool BleScanEnabled { get; set; } = true;

    /// <summary>BLE 扫描超时（秒），超时未命中就只发 PN</summary>
    public int BleScanTimeoutSeconds { get; set; } = DefaultBleScanTimeoutSeconds;

    /// <summary>RSSI 门限（dBm）：-100~-30 生效，0 = 不过滤；只影响取 MAC 的候选集，不影响设置页诊断列表</summary>
    public int BleMinRssi { get; set; } = DefaultBleMinRssi;

    /// <summary>同一 PN 的 MAC 缓存时长（秒），0 = 不缓存；命中缓存不再启动扫描</summary>
    public int BleMacCacheSeconds { get; set; } = DefaultBleMacCacheSeconds;

    // ---------- HTTP 接口配置（仅老化架 / 点位接口使用） ----------
    /// <summary>HTTP 接口超时（秒），供 RackPageList / AvailablePointList 使用</summary>
    public int TimeoutSeconds { get; set; } = 10;
    public bool AllowInvalidCertificate { get; set; } = true;
    /// <summary>响应体里的"成功业务码"，逗号分隔。默认 0,1,200；留空则按 0/1/200 处理</summary>
    public string ApiSuccessCodes { get; set; } = DefaultApiSuccessCodes;

    // ---------- API 模式接口（选老化架 / 点位） ----------
    /// <summary>老化架列表接口（API 模式第一个下拉框）</summary>
    public string RackPageListUrl { get; set; } = DefaultRackPageListUrl;

    /// <summary>点位列表接口（API 模式第二个下拉框，依赖老化架选中项）</summary>
    public string AvailablePointListUrl { get; set; } = DefaultAvailablePointListUrl;

    // ---------- Redis 配置 ----------
    public string RedisConnectionString { get; set; } = "192.168.1.65:6379,password=123456,defaultDatabase=0";
    public string OnlineListKey { get; set; } = "lgw:devices:online";
    public int RedisTimeoutMs { get; set; } = 5000;
    public int OnlineCacheSeconds { get; set; } = 30;
    public bool AllowSubmitWhenRedisUnavailable { get; set; }

    // ---------- 扫码规则 ----------
    /// <summary>储能设备 PN 的"字符串部分"，支持逗号/分号/换行分隔多个前缀。例如 PN123ABC 填 PN</summary>
    public string PnPrefixes { get; set; } = "PN";
    public bool IgnorePnCase { get; set; } = true;
    /// <summary>开启后，PN 前缀之后的剩余部分必须全部是数字才判定为储能设备 PN，可大幅降低误判</summary>
    public bool RequireNumericSuffix { get; set; } = true;
    /// <summary>未命中 PN 前缀的码按此正则校验，通过则判定为网关 ID</summary>
    public string GatewayPattern { get; set; } = "^[A-Za-z0-9][A-Za-z0-9\\-_]{2,63}$";
    public int ScanDedupMs { get; set; } = 1200;
    /// <summary>识别一维条码（Code128 / Code39 / EAN / UPC 等）</summary>
    public bool Scan1D { get; set; } = true;
    /// <summary>识别二维码（QR / DataMatrix / PDF417 / Aztec）</summary>
    public bool Scan2D { get; set; } = true;
    /// <summary>是否强制"先扫网关 ID，再扫储能 PN"</summary>
    public bool RequireScanOrder { get; set; }
    /// <summary>同类型重复扫码时：true=覆盖，false=忽略并提示</summary>
    public bool OverwriteDuplicate { get; set; }

    // ---------- 行为 ----------
    public bool AutoSubmit { get; set; } = true;
    public bool AutoResetAfterSuccess { get; set; } = true;
    public bool SoundAndVibration { get; set; } = true;

    // ---------- 派生属性 ----------

    /// <summary>是否已配置储能设备字符串前缀——未配置时不允许扫码</summary>
    public bool IsScanConfigured => GetPnPrefixList().Count > 0;

    public IReadOnlyList<string> GetPnPrefixList()
    {
        if (string.IsNullOrWhiteSpace(PnPrefixes)) return Array.Empty<string>();

        var separators = new[] { ',', ';', '\r', '\n', '|' };
        return PnPrefixes
            .Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.Length > 0)
            .Distinct()
            .ToArray();
    }

    public AppConfig Clone() => (AppConfig)MemberwiseClone();

    /// <summary>
    /// 校验配置合法性。返回 (字段名, 错误说明) 列表，空列表表示合法。
    /// </summary>
    public List<(string Field, string Message)> Validate()
    {
        var errors = new List<(string, string)>();

        if (string.IsNullOrWhiteSpace(MqttHost))
            errors.Add((nameof(MqttHost), "MQTT 服务器地址不能为空"));
        else if (MqttHost.Contains("://", StringComparison.Ordinal)
                 || MqttHost.Contains('/')
                 || MqttHost.Any(char.IsWhiteSpace))
            errors.Add((nameof(MqttHost), "MQTT 服务器只填 IP 或域名，不要带 http:// 前缀或路径"));

        if (MqttPort < 1 || MqttPort > 65535)
            errors.Add((nameof(MqttPort), "MQTT 端口需在 1~65535 之间"));

        if (MqttReplyTimeoutSeconds < 1 || MqttReplyTimeoutSeconds > 60)
            errors.Add((nameof(MqttReplyTimeoutSeconds), "回执超时需在 1~60 秒之间"));

        if (BleScanTimeoutSeconds < 1 || BleScanTimeoutSeconds > 15)
            errors.Add((nameof(BleScanTimeoutSeconds), "蓝牙扫描超时需在 1~15 秒之间"));

        if (BleMinRssi != 0 && (BleMinRssi < -100 || BleMinRssi > -30))
            errors.Add((nameof(BleMinRssi), "蓝牙信号门限需为 0（不过滤）或 -100~-30 dBm 之间"));

        if (BleMacCacheSeconds < 0 || BleMacCacheSeconds > 600)
            errors.Add((nameof(BleMacCacheSeconds), "MAC 缓存时间需在 0~600 秒之间（0 = 不缓存）"));

        if (string.IsNullOrWhiteSpace(RackPageListUrl))
            errors.Add((nameof(RackPageListUrl), "老化架接口地址不能为空"));
        else if (!Uri.TryCreate(RackPageListUrl.Trim(), UriKind.Absolute, out var rackUri)
                 || (rackUri.Scheme != Uri.UriSchemeHttp && rackUri.Scheme != Uri.UriSchemeHttps))
            errors.Add((nameof(RackPageListUrl), "老化架接口地址必须是 http:// 或 https:// 开头的完整地址"));

        if (string.IsNullOrWhiteSpace(AvailablePointListUrl))
            errors.Add((nameof(AvailablePointListUrl), "点位接口地址不能为空"));
        else if (!Uri.TryCreate(AvailablePointListUrl.Trim(), UriKind.Absolute, out var pointUri)
                 || (pointUri.Scheme != Uri.UriSchemeHttp && pointUri.Scheme != Uri.UriSchemeHttps))
            errors.Add((nameof(AvailablePointListUrl), "点位接口地址必须是 http:// 或 https:// 开头的完整地址"));

        if (TimeoutSeconds < 1 || TimeoutSeconds > 120)
            errors.Add((nameof(TimeoutSeconds), "超时时间需在 1~120 秒之间"));

        var badCodes = (ApiSuccessCodes ?? string.Empty)
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !int.TryParse(x, out _))
            .ToArray();
        if (badCodes.Length > 0)
            errors.Add((nameof(ApiSuccessCodes), $"成功业务码只能是整数，逗号分隔：{string.Join("、", badCodes)}"));

        if (string.IsNullOrWhiteSpace(RedisConnectionString))
            errors.Add((nameof(RedisConnectionString), "Redis 连接串不能为空"));

        if (string.IsNullOrWhiteSpace(OnlineListKey))
            errors.Add((nameof(OnlineListKey), "在线列表 Key 不能为空"));

        if (RedisTimeoutMs < 500 || RedisTimeoutMs > 60000)
            errors.Add((nameof(RedisTimeoutMs), "Redis 超时需在 500~60000 毫秒之间"));

        if (OnlineCacheSeconds < 0 || OnlineCacheSeconds > 300)
            errors.Add((nameof(OnlineCacheSeconds), "在线缓存时间需在 0~300 秒之间（0 = 不缓存）"));

        if (ScanDedupMs < 0 || ScanDedupMs > 10000)
            errors.Add((nameof(ScanDedupMs), "去重窗口需在 0~10000 毫秒之间"));

        if (!Scan1D && !Scan2D)
            errors.Add((nameof(Scan1D), "一维码与二维码至少要开启一种，否则无法扫码"));

        if (!string.IsNullOrWhiteSpace(GatewayPattern))
        {
            try
            {
                _ = new Regex(GatewayPattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException)
            {
                errors.Add((nameof(GatewayPattern), "网关 ID 校验正则表达式不合法"));
            }
        }

        return errors;
    }
}
