namespace LightGatewayScanner.Models;

/// <summary>接口调用结果</summary>
public sealed class ApiOperationResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public int? StatusCode { get; init; }
    /// <summary>服务端返回的业务提示（message / msg / error 等字段），可能为 null</summary>
    public string? ServerMessage { get; init; }
    /// <summary>服务端返回的原始内容（截断保存，便于现场排查）</summary>
    public string? RawResponse { get; init; }
    public long ElapsedMs { get; init; }

    public static ApiOperationResult Ok(string message, int? statusCode, string? raw, long elapsedMs, string? serverMessage = null) =>
        new() { Success = true, Message = message, StatusCode = statusCode, RawResponse = raw, ElapsedMs = elapsedMs, ServerMessage = serverMessage };

    public static ApiOperationResult Fail(string message, int? statusCode = null, string? raw = null, long elapsedMs = 0, string? serverMessage = null) =>
        new() { Success = false, Message = message, StatusCode = statusCode, RawResponse = raw, ElapsedMs = elapsedMs, ServerMessage = serverMessage };
}

/// <summary>一次完整提交（在线校验 + 接口调用）的结果</summary>
public sealed class SubmitOutcome
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? Detail { get; init; }
    /// <summary>服务端返回的业务提示</summary>
    public string? ServerMessage { get; init; }
    public int? StatusCode { get; init; }
    public long ElapsedMs { get; init; }
    /// <summary>默认 NotChecked，避免未真正校验时把徽标误显示成"在线"</summary>
    public OnlineCheckStatus OnlineStatus { get; init; } = OnlineCheckStatus.NotChecked;
    /// <summary>Redis 不可用时，是否允许人工强制提交（需要调用方二次确认）</summary>
    public bool CanForce { get; init; }
    public string GatewayId { get; init; } = string.Empty;
    public string Pn { get; init; } = string.Empty;
}
