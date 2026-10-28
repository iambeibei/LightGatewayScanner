using Microsoft.Maui.ApplicationModel;

namespace LightGatewayScanner;

/// <summary>
/// 蓝牙扫描权限。
/// Android 12+（API 31）：BLUETOOTH_SCAN + BLUETOOTH_CONNECT（读 MAC 必须同时有 CONNECT）。
/// Android 11 及以下：BLUETOOTH / BLUETOOTH_ADMIN（安装即授予）+ ACCESS_FINE_LOCATION（运行时）。
/// </summary>
public sealed class BleScanPermission : Permissions.BasePlatformPermission
{
    public override (string androidPermission, bool isRuntime)[] RequiredPermissions =>
        OperatingSystem.IsAndroidVersionAtLeast(31)
            ? new[]
            {
                (Android.Manifest.Permission.BluetoothScan, true),
                (Android.Manifest.Permission.BluetoothConnect, true)
            }
            : new[]
            {
                (Android.Manifest.Permission.Bluetooth, false),
                (Android.Manifest.Permission.BluetoothAdmin, false),
                (Android.Manifest.Permission.AccessFineLocation, true)
            };
}
