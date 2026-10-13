using System.Text.RegularExpressions;

namespace LightGatewayScanner.Models;

/// <summary>
/// APP 全部可配置项。持久化到 Preferences（Android → SharedPreferences），重启后生效。
/// </summary>
public sealed class AppConfig
{
    public const int CurrentVersion = 1;

    public int ConfigVersion { get; set; } = CurrentVersion;

    // ---------- 接口配置 ----------
    public string SetPnUrl { get; set; } = "https://192.168.40.116:7088/AgingService/SetPn";
    public int TimeoutSeconds { get; set; } = 10;
    public bool AllowInvalidCertificate { get; set; } = true;
    /// <summary>响应体里的"成功业务码"，逗号分隔。默认 0,1,200；留空则按 0/1/200 处理</summary>
    public string ApiSuccessCodes { get; set; } = "0,1,200";

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

        if (string.IsNullOrWhiteSpace(SetPnUrl))
            errors.Add((nameof(SetPnUrl), "接口地址不能为空"));
        else if (!Uri.TryCreate(SetPnUrl.Trim(), UriKind.Absolute, out var uri)
                 || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            errors.Add((nameof(SetPnUrl), "接口地址必须是 http:// 或 https:// 开头的完整地址"));

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
