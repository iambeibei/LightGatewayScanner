namespace LightGatewayScanner.Models;

/// <summary>
/// MQTT 设置 PN 的结果（发布 + 等待网关回执）。
/// </summary>
public sealed class SetPnResult
{
    public bool Success { get; init; }

    /// <summary>纯原因文本，不带"写入失败："前缀（前缀统一由 ScanSessionService 拼）</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>本次发送的 Seq（Unix 秒时间戳，同秒重复时 +1）</summary>
    public long Seq { get; init; }

    /// <summary>网关回执里的 Code（未收到回执时为 null）</summary>
    public int? Code { get; init; }

    /// <summary>网关回执里的 Msg（可能为 null）</summary>
    public string? GatewayMessage { get; init; }

    /// <summary>原始回执 JSON（截断保存，便于现场排查）</summary>
    public string? RawResponse { get; init; }

    public long ElapsedMs { get; init; }

    /// <summary>本次随命令一起发出的设备 MAC（null = 未带 MAC）</summary>
    public string? DeviceMac { get; init; }

    public static SetPnResult Ok(string message, long seq, int? code, string? gatewayMessage, string? raw, long elapsedMs,
        string? deviceMac = null) =>
        new()
        {
            Success = true,
            Message = message,
            Seq = seq,
            Code = code,
            GatewayMessage = gatewayMessage,
            RawResponse = raw,
            ElapsedMs = elapsedMs,
            DeviceMac = deviceMac
        };

    public static SetPnResult Fail(string message, long seq = 0, int? code = null, string? gatewayMessage = null,
        string? raw = null, long elapsedMs = 0, string? deviceMac = null) =>
        new()
        {
            Success = false,
            Message = message,
            Seq = seq,
            Code = code,
            GatewayMessage = gatewayMessage,
            RawResponse = raw,
            ElapsedMs = elapsedMs,
            DeviceMac = deviceMac
        };
}
