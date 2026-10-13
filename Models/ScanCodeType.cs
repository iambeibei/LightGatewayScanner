namespace LightGatewayScanner.Models;

/// <summary>条码分类结果</summary>
public enum ScanCodeType
{
    /// <summary>无法识别</summary>
    Unknown,

    /// <summary>网关 ID → pointId</summary>
    GatewayId,

    /// <summary>储能设备 PN → pn</summary>
    Pn
}

/// <summary>扫码接收结果状态</summary>
public enum ScanAcceptStatus
{
    /// <summary>已接收，字段已填入</summary>
    Accepted,

    /// <summary>与刚刚扫过的码重复，已忽略</summary>
    Duplicate,

    /// <summary>顺序错误（强制先扫网关时先扫到了 PN）</summary>
    WrongOrder,

    /// <summary>类型重复（已扫过网关却又扫到网关）</summary>
    WrongType,

    /// <summary>条码无法识别</summary>
    Unknown,

    /// <summary>业务被占用（提交进行中）</summary>
    Busy,

    /// <summary>未配置储能设备字符串前缀</summary>
    NotConfigured
}

/// <summary>扫码接收结果</summary>
public sealed class ScanAcceptOutcome
{
    public ScanAcceptStatus Status { get; init; }
    public ScanCodeType Type { get; init; } = ScanCodeType.Unknown;
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    /// <summary>两个码都已扫到，可以提交</summary>
    public bool Ready { get; init; }

    public static ScanAcceptOutcome Ok(ScanCodeType type, string code, string message, bool ready) =>
        new() { Status = ScanAcceptStatus.Accepted, Type = type, Code = code, Message = message, Ready = ready };

    public static ScanAcceptOutcome Fail(ScanAcceptStatus status, string message) =>
        new() { Status = status, Message = message };
}
