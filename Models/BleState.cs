namespace LightGatewayScanner.Models;

/// <summary>
/// 蓝牙开关的轻量状态。启动检测只关心"是否开启"，
/// 因此不触发扫描、不申请权限，避免为了检测打断用户。
/// </summary>
public enum BleState
{
    /// <summary>蓝牙已开启</summary>
    On,

    /// <summary>蓝牙已关闭</summary>
    Off,

    /// <summary>本机没有蓝牙 / 不支持 BLE</summary>
    NotSupported,

    /// <summary>无法判断（权限不足或系统异常）</summary>
    Unknown
}
