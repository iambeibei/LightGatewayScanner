namespace LightGatewayScanner.Models;

public enum OnlineCheckStatus
{
    /// <summary>网关在线</summary>
    Online,

    /// <summary>网关不在在线列表中</summary>
    Offline,

    /// <summary>Redis 连不上、超时、认证失败等，无法判定</summary>
    RedisUnavailable,

    /// <summary>配置问题（Key 类型不对、列表为空等）</summary>
    ConfigError,

    /// <summary>还没做在线校验（提交未真正发起）。用于避免把状态徽标误显示成"在线"</summary>
    NotChecked
}

/// <summary>在线校验结果</summary>
public sealed class OnlineCheckResult
{
    public OnlineCheckStatus Status { get; init; }
    public string Message { get; init; } = string.Empty;
    /// <summary>在线列表中的设备总数（用于界面展示）</summary>
    public int OnlineCount { get; init; }
    public bool FromCache { get; init; }

    public bool IsOnline => Status == OnlineCheckStatus.Online;

    public static OnlineCheckResult Ok(string message, int count, bool fromCache = false) =>
        new() { Status = OnlineCheckStatus.Online, Message = message, OnlineCount = count, FromCache = fromCache };

    public static OnlineCheckResult NotOnline(string message, int count, bool fromCache = false) =>
        new() { Status = OnlineCheckStatus.Offline, Message = message, OnlineCount = count, FromCache = fromCache };

    public static OnlineCheckResult Unavailable(string message) =>
        new() { Status = OnlineCheckStatus.RedisUnavailable, Message = message };

    public static OnlineCheckResult BadConfig(string message) =>
        new() { Status = OnlineCheckStatus.ConfigError, Message = message };
}

/// <summary>设置页"测试连接"结果</summary>
public sealed class RedisTestResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public int OnlineCount { get; init; }
    /// <summary>在线网关 ID 样例，便于现场核对</summary>
    public IReadOnlyList<string> SampleIds { get; init; } = Array.Empty<string>();
}
