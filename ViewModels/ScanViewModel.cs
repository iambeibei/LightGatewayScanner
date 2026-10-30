using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightGatewayScanner.Models;
using LightGatewayScanner.Services;
using Microsoft.Maui.ApplicationModel;

namespace LightGatewayScanner.ViewModels;

public partial class ScanViewModel : ObservableObject
{
    private readonly IScanSessionService _sessionService;
    private readonly IAppConfigService _configService;
    private readonly IFeedbackService _feedback;
    private readonly IAppLogService _logService;
    private readonly IDeviceCallbackApiService _deviceCallbackApi;
    private readonly IBleScanner _bleScanner;

    private CancellationTokenSource? _submitCts;

    /// <summary>结果条代际：旧的成功提示延时任务不得把新结果条隐藏掉</summary>
    private int _resultGeneration;

    /// <summary>点位接口的请求代际：快速切换老化架时丢弃过期响应</summary>
    private int _pointLoadGeneration;

    public ScanViewModel(
        IScanSessionService sessionService,
        IAppConfigService configService,
        IFeedbackService feedback,
        IAppLogService logService,
        IDeviceCallbackApiService deviceCallbackApi,
        IBleScanner bleScanner)
    {
        _sessionService = sessionService;
        _configService = configService;
        _feedback = feedback;
        _logService = logService;
        _deviceCallbackApi = deviceCallbackApi;
        _bleScanner = bleScanner;

        _configService.ConfigChanged += (_, _) => RefreshFromConfig();
        RefreshFromConfig();
    }

    // ---------- 展示属性 ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GatewayDisplay))]
    private string? gatewayId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PnDisplay))]
    private string? pn;

    [ObservableProperty] private string statusText = "等待扫描";
    [ObservableProperty] private Color statusColor = Colors.Gray;

    [ObservableProperty] private string onlineText = "未校验";
    [ObservableProperty] private Color onlineColor = Colors.Gray;

    [ObservableProperty] private string? resultText;
    [ObservableProperty] private Color resultColor = Colors.Gray;

    // 结果条有浮层（相机可见时叠在预览底部）与行内（相机不可见时兜底）两个落点，
    // 由 ResultVisible + CameraVisible 组合决定，所以两者变化都要通知这两个派生属性。
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResultOverlayVisible))]
    [NotifyPropertyChangedFor(nameof(ResultInlineVisible))]
    private bool resultVisible;

    [ObservableProperty] private bool isBusy;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CameraButtonText))]
    [NotifyPropertyChangedFor(nameof(ShowCameraToggle))]
    [NotifyPropertyChangedFor(nameof(ResultOverlayVisible))]
    [NotifyPropertyChangedFor(nameof(ResultInlineVisible))]
    private bool cameraVisible = true;

    public string CameraButtonText => CameraVisible ? "关闭相机" : "打开相机";

    /// <summary>结果条浮层：相机可见时叠在预览底部，不参与布局测量，避免压缩画面</summary>
    public bool ResultOverlayVisible => ResultVisible && CameraVisible;

    /// <summary>结果条行内兜底：相机不可见时没有浮层可叠，回到状态区显示</summary>
    public bool ResultInlineVisible => ResultVisible && !CameraVisible;

    // ---------- 网关 ID 输入模式（扫码 / API 二选一，默认扫码） ----------

    [ObservableProperty] private bool isApiMode;

    [ObservableProperty] private ObservableCollection<AgingRackItem> racks = new();
    [ObservableProperty] private AgingRackItem? selectedRack;
    [ObservableProperty] private bool isRackLoading;
    [ObservableProperty] private string? rackError;
    [ObservableProperty] private string? rackHint;

    [ObservableProperty] private ObservableCollection<AgingPointItem> points = new();
    [ObservableProperty] private AgingPointItem? selectedPoint;
    [ObservableProperty] private bool isPointLoading;
    [ObservableProperty] private string? pointError;
    [ObservableProperty] private string? pointHint;

    /// <summary>顶部切换按钮文字：显示要点一下会进入的模式</summary>
    public string ModeButtonText => IsApiMode ? "切到扫码模式" : "切到 API 模式";

    /// <summary>相机关闭时才显示的"打开相机"按钮（API 模式下相机保持开启扫 PN）</summary>
    public bool ShowCameraToggle => !CameraVisible;
    [ObservableProperty] private bool canSubmit;

    [ObservableProperty] private bool isConfigured;
    [ObservableProperty] private string? configHint;

    [ObservableProperty] private string armHint = string.Empty;

    // ---------- PN 过滤快捷面板（与设置页共用同一份配置） ----------

    [ObservableProperty] private string pnPrefixesQuick = string.Empty;
    [ObservableProperty] private bool ignorePnCaseQuick;
    [ObservableProperty] private bool requireNumericSuffixQuick;

    /// <summary>默认收起：现场主界面优先保证相机与提交按钮的可用面积</summary>
    [ObservableProperty] private bool isPnPanelExpanded;
    [ObservableProperty] private string? pnQuickStatus;

    /// <summary>蓝牙未开启提示条：只在"配置启用 BLE 扫描 + 系统蓝牙关闭"时显示</summary>
    [ObservableProperty] private bool bluetoothOffBannerVisible;

    public string PnPanelToggleText => IsPnPanelExpanded ? "收起" : "PN 过滤";

    public string PnPrefixSummary => string.IsNullOrWhiteSpace(PnPrefixesQuick)
        ? "未设置前缀，扫码不可用"
        : $"前缀：{PnPrefixesQuick}";

    public string GatewayDisplay => string.IsNullOrWhiteSpace(GatewayId) ? "未扫描" : GatewayId;
    public string PnDisplay => string.IsNullOrWhiteSpace(Pn) ? "未扫描" : Pn;

    /// <summary>副标题（带构建标记，便于确认手机装的是哪一版）</summary>
    public string Subtitle => $"连续扫描：网关 ID + 储能设备 PN · 构建 {AppConfig.BuildTag}";

    // ---------- 命令 ----------

    [RelayCommand]
    private void ArmGateway()
    {
        _sessionService.Arm(ScanCodeType.GatewayId);
        ArmHint = "下一次扫码将填入：网关 ID";
        StatusText = "请扫描网关 ID";
        StatusColor = Color.FromArgb("#185FA5");
    }

    [RelayCommand]
    private void ArmPn()
    {
        _sessionService.Arm(ScanCodeType.Pn);
        ArmHint = "下一次扫码将填入：储能设备 PN";
        StatusText = "请扫描储能设备 PN";
        StatusColor = Color.FromArgb("#185FA5");
    }

    [RelayCommand]
    private void ClearArm()
    {
        _sessionService.Arm(null);
        ArmHint = string.Empty;
        SyncStatus();
    }

    [RelayCommand]
    private void Reset()
    {
        _submitCts?.Cancel();
        ClearSessionState();
        HideResult();

        // API 模式：同时清掉两个下拉框的选中项（列表保留，选同一老化架不必重新请求）
        SelectedPoint = null;
        SelectedRack = null;
        Points.Clear();
        PointError = null;
        PointHint = null;
    }

    /// <summary>清空会话（两个码槽位与在线徽标），但保留结果条展示</summary>
    private void ClearSessionState()
    {
        _sessionService.Reset();
        GatewayId = null;
        Pn = null;
        OnlineText = "未校验";
        OnlineColor = Colors.Gray;
        ArmHint = string.Empty;

        // API 模式：网关 ID 来自下拉框，清空后重新填回来，
        // 保证"结果条清掉"不会连点位选择一起作废，便于同一台设备连续作业
        if (IsApiMode && _sessionService.SetGatewayId(SelectedPoint?.DeviceId ?? string.Empty))
            GatewayId = SelectedPoint!.DeviceId;

        SyncStatus();
    }

    [RelayCommand]
    private void ToggleCamera() => CameraVisible = !CameraVisible;

    // ---------- PN 过滤快捷面板 ----------

    [RelayCommand]
    private void TogglePnPanel()
    {
        IsPnPanelExpanded = !IsPnPanelExpanded;
        if (IsPnPanelExpanded) PnQuickStatus = null;
    }

    /// <summary>
    /// 把快捷面板的值写回配置。Entry 失焦/回车与开关切换时调用；
    /// 与当前配置相同则直接返回，避免 ConfigChanged 自触发造成重复重建相机。
    /// </summary>
    [RelayCommand]
    private void SavePnFilter()
    {
        var current = _configService.Current;
        var prefixes = (PnPrefixesQuick ?? string.Empty).Trim();

        if (string.Equals(prefixes, current.PnPrefixes, StringComparison.Ordinal)
            && IgnorePnCaseQuick == current.IgnorePnCase
            && RequireNumericSuffixQuick == current.RequireNumericSuffix)
            return;

        var config = current.Clone();
        config.PnPrefixes = prefixes;
        config.IgnorePnCase = IgnorePnCaseQuick;
        config.RequireNumericSuffix = RequireNumericSuffixQuick;

        if (!_configService.Save(config, out var error))
        {
            PnQuickStatus = $"保存失败：{error}";
            StatusText = "PN 过滤保存失败";
            StatusColor = Color.FromArgb("#A32D2D");
            _feedback.Error();
            return;
        }

        PnQuickStatus = prefixes.Length == 0 ? "已清空前缀，扫码已停用" : "PN 过滤已生效";
        _feedback.Tap();
    }

    // ---------- 蓝牙状态 ----------

    /// <summary>
    /// 启动检测入口：后台执行、立即返回，绝不阻塞首帧。
    /// 只在"配置启用 BLE 扫描 + 系统蓝牙关闭"时点亮提示条。
    /// </summary>
    public void BeginStartupBluetoothCheck()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var state = _bleScanner.GetBluetoothState();
                await _logService.WriteAsync($"启动蓝牙检测 state={state}");

                var shouldShow = state == BleState.Off && _configService.Current.BleScanEnabled;
                MainThread.BeginInvokeOnMainThread(() => BluetoothOffBannerVisible = shouldShow);
            }
            catch (Exception ex)
            {
                // 检测失败不能影响启动，也不弹窗打扰用户
                await _logService.WriteAsync("启动蓝牙检测异常", ex);
            }
        });
    }

    /// <summary>回到扫码页时刷新提示条（用户可能刚去系统设置开了蓝牙）</summary>
    public void RefreshBluetoothBanner()
    {
        try
        {
            BluetoothOffBannerVisible =
                _bleScanner.GetBluetoothState() == BleState.Off
                && _configService.Current.BleScanEnabled;
        }
        catch (Exception)
        {
            BluetoothOffBannerVisible = false;
        }
    }

    [RelayCommand]
    private async Task ShowBluetoothGuideAsync()
    {
        var page = Application.Current?.Windows.FirstOrDefault()?.Page;
        if (page is null) return;

        var go = await page.DisplayAlertAsync(
            "蓝牙未开启",
            "提交写入前会按储能设备 PN 扫描蓝牙广播以获取 MAC，MAC 会随 PN 一起下发。\n\n" +
            "蓝牙关闭时仍可提交，但只会下发 PN、不带 MAC。\n\n" +
            "请点击「去开启蓝牙」，在系统设置中打开蓝牙后返回本页。",
            "去开启蓝牙", "暂不");

        if (!go) return;

        try
        {
#if ANDROID
            var intent = new Android.Content.Intent(Android.Provider.Settings.ActionBluetoothSettings);
            intent.AddFlags(Android.Content.ActivityFlags.NewTask);
            Android.App.Application.Context.StartActivity(intent);
#else
            await Launcher.Default.OpenAsync("https://support.google.com/android/answer/9075921");
#endif
        }
        catch (Exception ex)
        {
            await _logService.WriteAsync("打开蓝牙设置失败", ex);
            StatusText = "无法打开系统蓝牙设置，请手动进入设置 → 蓝牙";
            StatusColor = Color.FromArgb("#A32D2D");
        }
    }

    // ---------- API 模式 ----------

    /// <summary>扫码模式 / API 模式切换（默认扫码模式）</summary>
    [RelayCommand]
    private async Task ToggleModeAsync()
    {
        IsApiMode = !IsApiMode;

        if (IsApiMode)
        {
            // 进入 API 模式：相机保持开启（用来扫储能 PN），面板叠在相机上方
            CameraVisible = true;
            RackError = null;
            PointError = null;

            // 网关 ID 由下拉框决定，扫码只负责储能 PN
            _sessionService.Arm(ScanCodeType.Pn);
            ArmHint = "API 模式：扫码只填入储能设备 PN，网关 ID 由下方下拉框选择";
            StatusText = "API 模式：请选择老化架与点位，然后扫描储能 PN";
            StatusColor = Color.FromArgb("#185FA5");

            if (Racks.Count == 0)
                await LoadRacksAsync();
        }
        else
        {
            // 回到扫码模式：两个槽位都交还给扫码自动分类
            _sessionService.Arm(null);
            ArmHint = string.Empty;
            StatusText = "已切回扫码模式";
            StatusColor = Color.FromArgb("#185FA5");
            CameraVisible = true;
            SyncStatus();
        }
    }

    /// <summary>拉取老化架列表（第一个下拉框的数据源）</summary>
    [RelayCommand]
    private async Task LoadRacksAsync()
    {
        if (IsRackLoading) return;

        IsRackLoading = true;
        RackError = null;
        StatusText = "正在加载老化架列表…";
        StatusColor = Color.FromArgb("#185FA5");

        try
        {
            var result = await _deviceCallbackApi.GetRackListAsync();

            if (result.Success)
            {
                Racks.Clear();
                foreach (var rack in result.Items) Racks.Add(rack);

                RackHint = result.Items.Count == 0 ? "未获取到老化架" : $"共 {result.Total} 个老化架";
                if (result.Items.Count == 0)
                {
                    Points.Clear();
                    SelectedPoint = null;
                    PointHint = null;
                }

                StatusText = result.Items.Count == 0 ? "未获取到老化架" : "请选择老化架";
                StatusColor = result.Items.Count == 0 ? Color.FromArgb("#854F0B") : Color.FromArgb("#185FA5");
                if (result.Items.Count == 0) _feedback.Warning();
            }
            else
            {
                RackError = result.Message ?? "加载老化架失败";
                StatusText = "老化架加载失败，可点右侧按钮重试";
                StatusColor = Color.FromArgb("#A32D2D");
                _feedback.Error();
            }
        }
        catch (Exception ex)
        {
            await _logService.WriteAsync("加载老化架异常", ex);
            RackError = $"加载失败：{ex.Message}";
            StatusText = "老化架加载异常";
            StatusColor = Color.FromArgb("#A32D2D");
        }
        finally
        {
            IsRackLoading = false;
        }
    }

    /// <summary>
    /// 拉取点位列表（第二个下拉框的数据源）。
    /// 由第一个下拉框选中项联动触发，也可手动重试。
    /// </summary>
    [RelayCommand]
    private async Task LoadPointsAsync()
    {
        var rack = SelectedRack;
        if (rack is null)
        {
            PointError = "请先选择老化架";
            return;
        }
        if (IsPointLoading) return;

        var generation = ++_pointLoadGeneration;
        IsPointLoading = true;
        PointError = null;
        Points.Clear();
        SelectedPoint = null;
        StatusText = $"正在加载「{rack.Name}」的点位…";
        StatusColor = Color.FromArgb("#185FA5");

        try
        {
            var result = await _deviceCallbackApi.GetAvailablePointListAsync(rack.Id);

            // 加载期间又切换了老化架 → 丢弃这次响应，避免串数据
            if (generation != _pointLoadGeneration) return;

            if (result.Success)
            {
                foreach (var point in result.Items) Points.Add(point);

                PointHint = result.Items.Count == 0
                    ? "该老化架下没有可用点位（deviceId 均为 0）"
                    : $"共 {result.Total} 个点位（已过滤 deviceId=0）";

                StatusText = result.Items.Count == 0 ? "该老化架下没有可用点位" : "请选择点位";
                StatusColor = result.Items.Count == 0 ? Color.FromArgb("#854F0B") : Color.FromArgb("#185FA5");
                if (result.Items.Count == 0) _feedback.Warning();
            }
            else
            {
                PointError = result.Message ?? "加载点位失败";
                StatusText = "点位加载失败，可点下方按钮重试";
                StatusColor = Color.FromArgb("#A32D2D");
                _feedback.Error();
            }
        }
        catch (Exception ex)
        {
            if (generation == _pointLoadGeneration)
            {
                await _logService.WriteAsync("加载点位异常", ex);
                PointError = $"加载失败：{ex.Message}";
                StatusText = "点位加载异常";
                StatusColor = Color.FromArgb("#A32D2D");
            }
        }
        finally
        {
            if (generation == _pointLoadGeneration)
                IsPointLoading = false;
        }
    }

    partial void OnIsApiModeChanged(bool value)
    {
        OnPropertyChanged(nameof(ModeButtonText));
        OnPropertyChanged(nameof(ShowCameraToggle));
    }

    partial void OnIsPnPanelExpandedChanged(bool value)
        => OnPropertyChanged(nameof(PnPanelToggleText));

    partial void OnPnPrefixesQuickChanged(string value)
        => OnPropertyChanged(nameof(PnPrefixSummary));

    /// <summary>第一个下拉框选中 → 联动拉取第二个接口的点位</summary>
    partial void OnSelectedRackChanged(AgingRackItem? value)
    {
        if (value is null) return;

        // 换老化架意味着原来的网关 ID 已失效，先清掉
        _sessionService.ClearGatewayId();
        GatewayId = null;
        Points.Clear();
        SelectedPoint = null;
        PointHint = null;
        _ = LoadPointsAsync();
    }

    /// <summary>第二个下拉框选中 → 把 deviceId 填进网关 ID</summary>
    partial void OnSelectedPointChanged(AgingPointItem? value)
    {
        if (value is null) return;

        if (_sessionService.SetGatewayId(value.DeviceId))
        {
            GatewayId = value.DeviceId;
            StatusText = $"已选点位 {value.Display} → 网关 ID：{value.DeviceId}";
            StatusColor = Color.FromArgb("#3B6D11");
            _feedback.Tap();
            SyncStatus();
        }
        else
        {
            PointError = "该点位数据异常（deviceId 为空），请重新选择";
            StatusColor = Color.FromArgb("#A32D2D");
        }
    }

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        await Shell.Current.GoToAsync("//settings");
    }

    [RelayCommand]
    private async Task SubmitAsync(bool force = false)
    {
        await DoSubmitAsync(force);
    }

    /// <summary>界面"提交"按钮使用（不带参数版本，避免 XAML 参数类型转换问题）</summary>
    [RelayCommand]
    private async Task SubmitNowAsync() => await DoSubmitAsync(false);

    // ---------- 扫码入口 ----------

    /// <summary>摄像头识别或手动输入的结果统一走这里</summary>
    public async Task OnCodeDetectedAsync(string rawCode)
    {
        if (!IsConfigured)
        {
            StatusText = "请先在设置中配置储能设备字符串前缀";
            StatusColor = Color.FromArgb("#A32D2D");
            _feedback.Error();
            return;
        }

        var outcome = _sessionService.Accept(rawCode);

        switch (outcome.Status)
        {
            case ScanAcceptStatus.Accepted:
                GatewayId = _sessionService.Current.GatewayId;
                Pn = _sessionService.Current.Pn;
                ArmHint = string.Empty;
                StatusText = outcome.Message;
                StatusColor = Color.FromArgb("#3B6D11");
                _feedback.Tap();

                // API 模式下每次扫完都重新锁定 PN 槽位，避免下一次扫码把网关 ID 覆盖掉
                if (IsApiMode)
                {
                    _sessionService.Arm(ScanCodeType.Pn);
                    ArmHint = "API 模式：扫码只填入储能设备 PN，网关 ID 由下方下拉框选择";
                }

                SyncStatus();

                if (outcome.Ready && _configService.Current.AutoSubmit)
                    await DoSubmitAsync(false);
                break;

            case ScanAcceptStatus.Duplicate:
                StatusText = outcome.Message;
                StatusColor = Color.FromArgb("#854F0B");
                _feedback.Warning();
                break;

            case ScanAcceptStatus.WrongOrder:
            case ScanAcceptStatus.WrongType:
                StatusText = outcome.Message;
                StatusColor = Color.FromArgb("#854F0B");
                _feedback.Warning();
                break;

            case ScanAcceptStatus.Unknown:
                StatusText = outcome.Message;
                StatusColor = Color.FromArgb("#A32D2D");
                _feedback.Error();
                break;

            case ScanAcceptStatus.Busy:
                StatusText = outcome.Message;
                StatusColor = Color.FromArgb("#854F0B");
                break;

            case ScanAcceptStatus.NotConfigured:
                StatusText = outcome.Message;
                StatusColor = Color.FromArgb("#A32D2D");
                ConfigHint = outcome.Message;
                _feedback.Error();
                break;
        }
    }

    /// <summary>手动输入兜底（摄像头扫不出时使用）</summary>
    [RelayCommand]
    private async Task ManualInputAsync()
    {
        var page = Application.Current?.Windows.FirstOrDefault()?.Page;
        if (page is null) return;

        var result = await page.DisplayPromptAsync(
            "手动输入条码", "扫码不清时可手动输入，将按同样规则自动分类",
            "确定", "取消", placeholder: "输入条码内容", maxLength: 128, keyboard: Keyboard.Text);

        if (string.IsNullOrWhiteSpace(result)) return;
        await OnCodeDetectedAsync(result);
    }

    // ---------- 内部实现 ----------

    private async Task DoSubmitAsync(bool force)
    {
        var session = _sessionService.Current;
        if (!session.IsReady)
        {
            StatusText = "两个条码都扫到后才能提交";
            StatusColor = Color.FromArgb("#854F0B");
            return;
        }

        if (IsBusy) return;

        // 与上一条完全相同时给出确认，避免误重复写入
        if (!force && _sessionService.LastSubmittedKey == $"{session.GatewayId}|{session.Pn}")
        {
            var page = Application.Current?.Windows.FirstOrDefault()?.Page;
            if (page is not null)
            {
                var again = await page.DisplayAlertAsync(
                    "重复提交确认",
                    $"网关 {session.GatewayId} + PN {session.Pn} 与上一次提交相同，确认再次写入？",
                    "再次写入", "取消");
                if (!again) return;
            }
        }

        IsBusy = true;
        CanSubmit = false;
        HideResult();
        StatusText = "正在校验网关在线状态…";
        StatusColor = Color.FromArgb("#185FA5");
        OnlineText = "校验中…";
        OnlineColor = Color.FromArgb("#185FA5");

        // 总预算 = Redis 查询 + 蓝牙扫描 + MQTT 建连/发布 + 等待回执 + 余量
        var config = _configService.Current;
        var bleBudget = config.BleScanEnabled ? Math.Clamp(config.BleScanTimeoutSeconds, 1, 15) + 2 : 0;
        var budgetSeconds = config.RedisTimeoutMs / 1000.0 + bleBudget + config.MqttReplyTimeoutSeconds + 18;
        _submitCts = new CancellationTokenSource(TimeSpan.FromSeconds(budgetSeconds));

        // 阶段状态（校验 → 扫描蓝牙 → 发送）由服务层推回来
        var progress = new Progress<string>(text =>
        {
            StatusText = text;
            StatusColor = Color.FromArgb("#185FA5");
        });

        try
        {
            var outcome = await _sessionService.SubmitAsync(force, progress, _submitCts.Token);

            ApplyOnlineStatus(outcome.OnlineStatus);

            ResultText = BuildResultText(outcome);
            ResultColor = outcome.Success ? Color.FromArgb("#3B6D11") : Color.FromArgb("#A32D2D");
            ResultVisible = true;
            ScheduleResultAutoHide(outcome.Success);

            // 界面只给三态结论，失败原因与诊断细节全部落到日志，现场排查仍可拉日志对照
            await _logService.WriteAsync(
                $"提交结果 {(outcome.Success ? "成功" : "失败")} gateway={outcome.GatewayId} pn={outcome.Pn} " +
                $"mac={(string.IsNullOrWhiteSpace(outcome.DeviceMac) ? "无" : "有")} " +
                $"seq={(outcome.Seq?.ToString() ?? "-")} 耗时={outcome.ElapsedMs}ms " +
                $"原因={(outcome.Success ? "-" : outcome.Message)}");

            if (outcome.Success)
            {
                StatusText = "提交完成";
                StatusColor = Color.FromArgb("#3B6D11");
                _feedback.Success();

                if (_configService.Current.AutoResetAfterSuccess)
                {
                    // 只清空两个码槽位与在线徽标，保留成功结果条，避免成功提示一闪而过
                    await Task.Delay(600);
                    ClearSessionState();
                }
            }
            else
            {
                StatusText = outcome.CanForce ? "需要确认" : "提交失败";
                StatusColor = Color.FromArgb("#A32D2D");
                _feedback.Error();

                if (outcome.CanForce)
                {
                    // 确认弹窗会盖住页面，先把结果条收起，避免确认后残留一条"发送失败"
                    HideResult();

                    var page = Application.Current?.Windows.FirstOrDefault()?.Page;
                    if (page is not null)
                    {
                        var ok = await page.DisplayAlertAsync(
                            "Redis 不可用",
                            outcome.Message,
                            "强制提交", "取消");
                        if (ok) await DoSubmitAsync(true);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            await _logService.WriteAsync("提交流程异常", ex);
            ResultText = "发送失败";
            ResultColor = Color.FromArgb("#A32D2D");
            ResultVisible = true;
            ScheduleResultAutoHide(success: false);
            StatusText = "提交异常";
            StatusColor = Color.FromArgb("#A32D2D");
        }
        finally
        {
            IsBusy = false;
            SyncStatus();
        }
    }

    /// <summary>
    /// 结果条只给三态结论。失败原因与诊断细节（Seq、耗时、MAC、蓝牙、回执原文）只写日志，
    /// 界面保持一行，浮层才不会越撑越高挡住相机预览。
    /// </summary>
    private static string BuildResultText(SubmitOutcome outcome)
    {
        if (!outcome.Success) return "发送失败";

        return string.IsNullOrWhiteSpace(outcome.DeviceMac)
            ? "发送成功（无 MAC 地址）"
            : "发送成功（带 MAC 地址）";
    }

    /// <summary>
    /// 成功提示停留 4 秒后自动消失，避免浮层长期压住预览；失败提示保留到下次提交或清空。
    /// 用代际令牌作废旧延时，防止它把新一次提交的结果条误隐藏。
    /// </summary>
    private void ScheduleResultAutoHide(bool success)
    {
        var generation = ++_resultGeneration;
        if (!success) return;

        _ = Task.Run(async () =>
        {
            await Task.Delay(4000);
            if (generation != _resultGeneration) return;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                // 回到 UI 线程再确认一次：延时期间可能已有新提交或用户清空
                if (generation != _resultGeneration) return;
                HideResult();
            });
        });
    }

    private void HideResult()
    {
        // 自增即作废所有在途的自动隐藏任务
        _resultGeneration++;
        ResultVisible = false;
        ResultText = null;
    }

    private void ApplyOnlineStatus(OnlineCheckStatus status)
    {
        // 没有真正做在线校验（例如"两个码没扫齐"）时不要改动徽标，避免误显示成"在线"
        if (status == OnlineCheckStatus.NotChecked) return;

        switch (status)
        {
            case OnlineCheckStatus.Online:
                OnlineText = "在线";
                OnlineColor = Color.FromArgb("#3B6D11");
                break;
            case OnlineCheckStatus.Offline:
                OnlineText = "不在线";
                OnlineColor = Color.FromArgb("#A32D2D");
                break;
            case OnlineCheckStatus.RedisUnavailable:
                OnlineText = "校验失败";
                OnlineColor = Color.FromArgb("#854F0B");
                break;
            default:
                OnlineText = "配置错误";
                OnlineColor = Color.FromArgb("#A32D2D");
                break;
        }
    }

    private void SyncStatus()
    {
        var session = _sessionService.Current;
        CanSubmit = session.IsReady && !IsBusy;

        if (!session.IsReady)
        {
            StatusText = session.HasGateway ? "请继续扫描储能设备 PN"
                       : session.HasPn ? "请继续扫描网关 ID"
                       : "请连续扫描两个条码";
            StatusColor = Colors.Gray;
        }
        else if (!IsBusy)
        {
            StatusText = "两个条码已就位，可提交";
            StatusColor = Color.FromArgb("#185FA5");
        }
    }

    private void RefreshFromConfig()
    {
        var config = _configService.Current;

        // 快捷面板与设置页共用同一份配置：外部保存后这里同步回显
        PnPrefixesQuick = config.PnPrefixes;
        IgnorePnCaseQuick = config.IgnorePnCase;
        RequireNumericSuffixQuick = config.RequireNumericSuffix;

        IsConfigured = config.IsScanConfigured;
        ConfigHint = IsConfigured
            ? null
            : "尚未配置储能设备字符串前缀，扫码不可用，请到「设置」中填写";

        if (!IsConfigured)
        {
            // 未配置前缀时扫码无意义，收起相机预览，只展示配置引导
            CameraVisible = false;
            StatusText = "请先在设置中配置储能设备字符串前缀";
            StatusColor = Color.FromArgb("#A32D2D");
        }
        else
        {
            CameraVisible = true;
            SyncStatus();
        }

        // 配置变化可能开启/关闭 BLE 扫描，提示条要跟着更新
        RefreshBluetoothBanner();
    }
}
