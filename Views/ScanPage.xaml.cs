using System.ComponentModel;
using LightGatewayScanner.Models;
using LightGatewayScanner.Services;
using LightGatewayScanner.ViewModels;
using Microsoft.Maui.ApplicationModel;
using ZXing.Net.Maui;
using ZXing.Net.Maui.Controls;

namespace LightGatewayScanner.Views;

public partial class ScanPage : ContentPage
{
    private readonly ScanViewModel _viewModel;
    private readonly IAppConfigService _configService;

    /// <summary>当前相机实例；为 null 表示相机已彻底关闭并释放</summary>
    private CameraBarcodeReaderView? _camera;

    private bool _cameraPermissionGranted;

    /// <summary>上次建相机时的条码格式：只有格式开关变化才重建，避免保存其他配置时画面闪断</summary>
    private (bool Scan1D, bool Scan2D) _cameraFormats;

    public ScanPage(ScanViewModel viewModel, IAppConfigService configService)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
        _configService = configService;

        // 相机开关状态由 ViewModel 驱动，这里跟随它真正地创建 / 释放相机
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        // 设置里改了"识别一维码/二维码"开关后，重建相机让新格式生效
        _configService.ConfigChanged += OnConfigChangedForCamera;

        // API 模式的两个下拉框。ItemDisplayBinding 的绑定对象是列表元素而不是 ViewModel，
        // 放代码后置绑定可以绕开 XAML 编译期对绑定路径的类型检查（它不是 BindableProperty）。
        RackPicker.SetBinding(Picker.ItemsSourceProperty, new Binding(nameof(ScanViewModel.Racks)));
        RackPicker.ItemDisplayBinding = new Binding(nameof(AgingRackItem.Display));
        RackPicker.SetBinding(Picker.SelectedItemProperty, new Binding(nameof(ScanViewModel.SelectedRack)));

        PointPicker.SetBinding(Picker.ItemsSourceProperty, new Binding(nameof(ScanViewModel.Points)));
        PointPicker.ItemDisplayBinding = new Binding(nameof(AgingPointItem.Display));
        PointPicker.SetBinding(Picker.SelectedItemProperty, new Binding(nameof(ScanViewModel.SelectedPoint)));
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        // 用户可能刚从系统设置打开蓝牙再返回，这里刷新一次提示条
        _viewModel.RefreshBluetoothBanner();

        if (!BarcodeScanning.IsSupported)
        {
            _viewModel.CameraVisible = false;
            await DisplayAlertAsync("相机不可用", "当前设备没有可用相机，已切换为手动输入模式。", "知道了");
            return;
        }

        if (!await EnsureCameraPermissionAsync())
        {
            // 权限没拿到就必须把状态置回"关闭"，否则按钮文字会说"关闭相机"，画面却是一片黑
            _viewModel.CameraVisible = false;
            _viewModel.StatusText = "相机权限未授予，扫码不可用，点『打开相机』可重新授权";
            _viewModel.StatusColor = Color.FromArgb("#854F0B");
            await DisplayAlertAsync("需要相机权限", "请在系统设置中授予相机权限，否则只能使用手动输入。", "知道了");
            return;
        }

        ApplyCameraState();
    }

    /// <summary>
    /// 检查并在需要时申请相机权限。返回 true 表示已获得权限。
    /// </summary>
    private async Task<bool> EnsureCameraPermissionAsync()
    {
        try
        {
            var status = await Permissions.CheckStatusAsync<Permissions.Camera>();
            if (status != PermissionStatus.Granted)
                status = await Permissions.RequestAsync<Permissions.Camera>();
            _cameraPermissionGranted = status == PermissionStatus.Granted;
        }
        catch (Exception)
        {
            _cameraPermissionGranted = false;
        }

        return _cameraPermissionGranted;
    }

    /// <summary>
    /// 离开页面（切到设置页、退到后台等）必须释放摄像头，否则相机会被持续占用。
    /// </summary>
    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        ReleaseCamera();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScanViewModel.CameraVisible))
            ApplyCameraState();
    }

    private void ApplyCameraState()
    {
        if (_viewModel.CameraVisible && _cameraPermissionGranted)
        {
            if (_camera is null) BuildCamera();
            return;
        }

        if (_viewModel.CameraVisible && !_cameraPermissionGranted)
        {
            // 权限没到位：状态拨回"关闭"，保证按钮文字与画面一致（不再显示一片黑）
            _viewModel.CameraVisible = false;
            return;
        }

        ReleaseCamera();
    }

    /// <summary>
    /// 只有"识别一维码/二维码"开关变化才重建相机。
    /// 保存 PN 过滤等其他配置也会触发 ConfigChanged，不能每次都闪断预览。
    /// </summary>
    private void OnConfigChangedForCamera(object? sender, EventArgs e)
    {
        if (_camera is null) return;

        var config = _configService.Current;
        if (config.Scan1D == _cameraFormats.Scan1D && config.Scan2D == _cameraFormats.Scan2D) return;

        ReleaseCamera();
        ApplyCameraState();
    }

    /// <summary>
    /// 按配置组装要识别的条码格式。
    /// 一维：Code128 / Code39 / Code93 / EAN / UPC 等；
    /// 二维：**QR 码**、DataMatrix、PDF417、Aztec（TwoDimensional 已覆盖，这里再显式列出便于确认）。
    /// </summary>
    private ZXing.Net.Maui.BarcodeFormat BuildFormats()
    {
        var config = _configService.Current;

        // BarcodeFormats（复数）是分组帮助类；具体的 QR 等格式在 BarcodeFormat 枚举上
        var twoD = BarcodeFormats.TwoDimensional
                   | BarcodeFormat.QrCode
                   | BarcodeFormat.DataMatrix
                   | BarcodeFormat.Pdf417
                   | BarcodeFormat.Aztec;

        if (config.Scan1D && config.Scan2D) return BarcodeFormats.OneDimensional | twoD;
        if (config.Scan1D) return BarcodeFormats.OneDimensional;
        return twoD;
    }

    private void BuildCamera()
    {
        try
        {
            var camera = new CameraBarcodeReaderView
            {
                // 一维 + 二维（含 QR 码）全开；只开一种时可以更快、更省电
                Options = new BarcodeReaderOptions
                {
                    Formats = BuildFormats(),
                    AutoRotate = true,
                    Multiple = false,
                    TryHarder = true,
                    DelayBetweenAnalyzingFrames = 150,
                    InitialDelayBeforeAnalyzingFrames = 300,
                    DelayBetweenContinuousScans = 1000
                }
            };

            camera.BarcodesDetected += OnBarcodesDetected;
            _camera = camera;
            CameraHost.Content = camera;
            TorchButton.Text = "照明";

            var config = _configService.Current;
            _cameraFormats = (config.Scan1D, config.Scan2D);
        }
        catch (Exception)
        {
            _camera = null;
            CameraHost.Content = null;
            // 相机没真正起来就把状态拨回"关闭"，按钮文字必须和现实一致
            if (_viewModel.CameraVisible) _viewModel.CameraVisible = false;
        }
    }

    /// <summary>
    /// 彻底关闭相机：停止取帧 → 解绑事件 → 从可视化树摘除 → 断开平台 Handler 释放摄像头。
    /// 只把控件设为不可见是不会释放摄像头的，这正是"点了关闭相机但画面仍在渲染"的根因。
    /// </summary>
    private void ReleaseCamera()
    {
        var camera = _camera;
        if (camera is null) return;

        _camera = null;

        try { camera.IsDetecting = false; } catch { /* 忽略 */ }
        try { camera.BarcodesDetected -= OnBarcodesDetected; } catch { /* 忽略 */ }

        CameraHost.Content = null;

        try { camera.Handler?.DisconnectHandler(); } catch { /* 忽略 */ }

        TorchButton.Text = "照明";
    }

    private void OnBarcodesDetected(object? sender, BarcodeDetectionEventArgs e)
    {
        if (_camera is null) return;

        var value = e.Results?.FirstOrDefault()?.Value;
        if (string.IsNullOrWhiteSpace(value)) return;

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            await _viewModel.OnCodeDetectedAsync(value);
        });
    }

    private void OnTorchClicked(object? sender, EventArgs e)
    {
        if (_camera is null) return;

        try
        {
            _camera.IsTorchOn = !_camera.IsTorchOn;
            if (sender is Button button)
                button.Text = _camera.IsTorchOn ? "关灯" : "照明";
        }
        catch
        {
            // 部分设备不支持手电筒
        }
    }

    private async void OnCameraToggleClicked(object? sender, EventArgs e)
    {
        // 按钮文字由 CameraButtonText 跟随 CameraVisible 自动更新，
        // 不在这里再手写一次，避免状态与文字脱钩。
        // 另外：准备"打开"但权限还没拿到时，先补一次授权，
        // 否则会把文字翻成"关闭相机"而画面仍是黑的。
        if (!_viewModel.CameraVisible && !_cameraPermissionGranted)
        {
            if (!await EnsureCameraPermissionAsync())
            {
                var page = Application.Current?.Windows.FirstOrDefault()?.Page;
                if (page is not null)
                    await page.DisplayAlertAsync(
                        "需要相机权限",
                        "未获得相机权限，无法打开相机。可到系统设置里授予权限后重试。",
                        "知道了");
                _viewModel.CameraVisible = false;
                return;
            }
        }

        _viewModel.ToggleCameraCommand.Execute(null);

        // 兜底：权限是刚刚才拿到的场景，属性可能没变化，这里再同步一次
        ApplyCameraState();
    }

    // ---------- PN 过滤快捷面板 ----------

    private void OnPnPrefixCompleted(object? sender, EventArgs e)
        => _viewModel.SavePnFilterCommand.Execute(null);

    private void OnPnPrefixUnfocused(object? sender, FocusEventArgs e)
        => _viewModel.SavePnFilterCommand.Execute(null);

    private void OnPnFilterSwitchToggled(object? sender, ToggledEventArgs e)
        => _viewModel.SavePnFilterCommand.Execute(null);
}
