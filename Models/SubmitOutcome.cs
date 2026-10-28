namespace LightGatewayScanner.Models;

/// <summary>一次完整提交（在线校验 + MQTT 写入）的结果</summary>
public sealed class SubmitOutcome
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? Detail { get; init; }
    /// <summary>网关回执里的 Msg（MQTT）</summary>
    public string? ServerMessage { get; init; }
    /// <summary>本次发送的 Seq（Unix 秒时间戳），便于对照网关日志</summary>
    public long? Seq { get; init; }
    public long ElapsedMs { get; init; }
    /// <summary>随命令一起发出的 BLE MAC（null = 未扫到 / 未开启扫描）</summary>
    public string? DeviceMac { get; init; }
    /// <summary>未扫到 MAC 时的原因（用于结果详情展示）</summary>
    public string? BleNote { get; init; }
    /// <summary>BLE 扫描耗时</summary>
    public long BleElapsedMs { get; init; }
    /// <summary>命中的 BLE 设备 RSSI（dBm）；0 = 未命中</summary>
    public int BleRssi { get; init; }
    /// <summary>扫描窗口内同名设备数量（>1 表示取了最强）</summary>
    public int BleDeviceCount { get; init; }
    /// <summary>MAC 来自缓存，本次没有真正扫描</summary>
    public bool BleFromCache { get; init; }
    /// <summary>默认 NotChecked，避免未真正校验时把徽标误显示成"在线"</summary>
    public OnlineCheckStatus OnlineStatus { get; init; } = OnlineCheckStatus.NotChecked;
    /// <summary>Redis 不可用时，是否允许人工强制提交（需要调用方二次确认）</summary>
    public bool CanForce { get; init; }
    public string GatewayId { get; init; } = string.Empty;
    public string Pn { get; init; } = string.Empty;
}
