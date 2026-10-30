using LightGatewayScanner.Models;

namespace LightGatewayScanner.Services;

/// <summary>
/// 蓝牙（BLE）扫描：按广播设备名找到储能设备并取 MAC。
/// 设备名即储能 PN（与网关固件 ble_connect_by_name 的约定一致）。
/// </summary>
public interface IBleScanner
{
    /// <summary>
    /// 扫描广播名等于 <paramref name="targetName"/> 的设备并取 MAC（内部会按需申请权限）。
    /// 找不到时返回未命中结果，不抛异常。
    /// </summary>
    Task<BleScanResult> FindByAdvertisedNameAsync(
        string targetName,
        int timeoutSeconds,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>设置页"扫描附近设备"：列出指定时长内所有带名称的广播设备（RSSI 降序）</summary>
    Task<BleNearbyResult> ScanNearbyAsync(int durationSeconds, CancellationToken cancellationToken = default);

    /// <summary>检查并按需申请蓝牙权限；返回 null 表示已授权，否则为中文原因</summary>
    Task<string?> EnsurePermissionsAsync(bool forceRequest = false);

    /// <summary>
    /// 轻量读取蓝牙开关状态：不扫描、不申请权限、不消耗 Android 扫描配额。
    /// 用于启动检测与提示条刷新；无法判断时返回 <see cref="BleState.Unknown"/>，不抛异常。
    /// </summary>
    BleState GetBluetoothState();

    /// <summary>作废某个 PN 的 MAC 缓存（网关回执失败时调用，下次重新扫描）</summary>
    void InvalidateCache(string pn);
}
