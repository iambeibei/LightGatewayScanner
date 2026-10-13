using System.ComponentModel;
using LightGatewayScanner.ViewModels;
using Microsoft.Maui.ApplicationModel;
using ZXing.Net.Maui;
using ZXing.Net.Maui.Controls;

namespace LightGatewayScanner.Views;

public partial class ScanPage : ContentPage
{
    private readonly ScanViewModel _viewModel;

    /// <summary>当前相机实例；为 null 表示相机已彻底关闭并释放</summary>
    private CameraBarcodeReaderView? _camera;

    private bool _cameraPermissionGranted;

    public ScanPage(ScanViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;

        // 相机开关状态由 ViewModel 驱动，这里跟随它真正地创建 / 释放相机
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (!BarcodeScanning.IsSupported)
        {
            _viewModel.CameraVisible = false;
            await DisplayAlertAsync("相机不可用", "当前设备没有可用相机，已切换为手动输入模式。", "知道了");
            return;
        }

        var status = await Permissions.CheckStatusAsync<Permissions.Camera>();
        if (status != PermissionStatus.Granted)
            status = await Permissions.RequestAsync<Permissions.Camera>();

        _cameraPermissionGranted = status == PermissionStatus.Granted;

        if (!_cameraPermissionGranted)
        {
            _viewModel.CameraVisible = false;
            await DisplayAlertAsync("需要相机权限", "请在系统设置中授予相机权限，否则只能使用手动输入。", "知道了");
            return;
        }

        ApplyCameraState();
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
        }
        else
        {
            ReleaseCamera();
        }
    }

    private void BuildCamera()
    {
        try
        {
            var camera = new CameraBarcodeReaderView
            {
                // 只识别常见工业条码格式，减少误识别并提高帧率
                Options = new BarcodeReaderOptions
                {
                    Formats = BarcodeFormats.OneDimensional | BarcodeFormats.TwoDimensional,
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
        }
        catch (Exception)
        {
            _camera = null;
            CameraHost.Content = null;
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

    private void OnCameraToggleClicked(object? sender, EventArgs e)
    {
        // 按钮文字由 CameraButtonText 跟随 CameraVisible 自动更新，
        // 不在这里再手写一次，避免状态与文字脱钩
        _viewModel.ToggleCameraCommand.Execute(null);
    }
}
