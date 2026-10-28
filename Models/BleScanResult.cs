namespace LightGatewayScanner.Models;

/// <summary>蓝牙扫描的结局分类，用于决定给用户什么提示</summary>
public enum BleScanStatus
{
    /// <summary>扫到目标设备并拿到 MAC</summary>
    Found,
    /// <summary>扫描窗口内没有出现同名广播</summary>
    NotFound,
    /// <summary>等待回执/扫描超时</summary>
    Timeout,
    /// <summary>提交被取消</summary>
    Canceled,
    /// <summary>缺少蓝牙扫描权限</summary>
    PermissionDenied,
    /// <summary>蓝牙未开启</summary>
    BluetoothOff,
    /// <summary>Android 11 及以下需要定位服务</summary>
    LocationDisabled,
    /// <summary>本机没有 BLE 能力</summary>
    NotSupported,
    /// <summary>上一次扫描还没结束</summary>
    Busy,
    /// <summary>扫描失败 / 异常</summary>
    Error,
    /// <summary>扫描被节流（本地 30 秒 5 次限制，或系统返回 SCAN_FAILED_SCANNING_TOO_FREQUENTLY）</summary>
    Throttled,
    /// <summary>找到了同名设备，但全部低于 RSSI 门限</summary>
    BelowRssiThreshold,
    /// <summary>开关关闭或 PN 为空，主动跳过</summary>
    Skipped
}

/// <summary>一个带名称的 BLE 广播设备；AdText 为 AD 结构摘要（设置页取证用）</summary>
public sealed record BleDeviceInfo(
    string Name,
    string Mac,
    int Rssi,
    string AdText = "",
    bool? Connectable = null);

/// <summary>设置页"扫描附近设备"的结果</summary>
public sealed record BleNearbyResult(IReadOnlyList<BleDeviceInfo> Devices, string? Error)
{
    public bool Success => Error is null;

    public static BleNearbyResult Ok(IReadOnlyList<BleDeviceInfo> devices) => new(devices, null);

    public static BleNearbyResult Fail(string error) => new(Array.Empty<BleDeviceInfo>(), error);
}

/// <summary>按 PN 扫描 BLE 的结果</summary>
public sealed class BleScanResult
{
    public BleScanStatus Status { get; init; }

    public bool Found => Status == BleScanStatus.Found;

    /// <summary>小写冒号格式（c7:2a:18:32:34:d3）；未命中为 null</summary>
    public string? Mac { get; init; }

    public string? DeviceName { get; init; }

    public int Rssi { get; init; }

    /// <summary>扫描期间出现过的同名设备数量（判断是否有多台重名）</summary>
    public int DeviceCount { get; init; }

    public long ElapsedMs { get; init; }

    /// <summary>未命中/失败的中文原因，直接展示在结果详情里</summary>
    public string? Reason { get; init; }

    /// <summary>MAC 来自缓存，本次没有真正扫描</summary>
    public bool FromCache { get; init; }

    public static BleScanResult Hit(string mac, string name, int rssi, int count, long elapsedMs,
        bool fromCache = false) => new()
    {
        Status = BleScanStatus.Found,
        Mac = mac,
        DeviceName = name,
        Rssi = rssi,
        DeviceCount = count,
        ElapsedMs = elapsedMs,
        FromCache = fromCache
    };

    public static BleScanResult Miss(BleScanStatus status, string reason, long elapsedMs = 0, int count = 0) => new()
    {
        Status = status,
        Reason = reason,
        ElapsedMs = elapsedMs,
        DeviceCount = count
    };

    public static BleScanResult Skipped(string reason) => Miss(BleScanStatus.Skipped, reason);
}
