using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightGatewayScanner.Models;
using LightGatewayScanner.Services;

namespace LightGatewayScanner.ViewModels;

public partial class ScanViewModel : ObservableObject
{
    private readonly IScanSessionService _sessionService;
    private readonly IAppConfigService _configService;
    private readonly IFeedbackService _feedback;
    private readonly IAppLogService _logService;

    private CancellationTokenSource? _submitCts;

    public ScanViewModel(
        IScanSessionService sessionService,
        IAppConfigService configService,
        IFeedbackService feedback,
        IAppLogService logService)
    {
        _sessionService = sessionService;
        _configService = configService;
        _feedback = feedback;
        _logService = logService;

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
    [ObservableProperty] private bool resultVisible;
    [ObservableProperty] private string? resultDetail;

    [ObservableProperty] private bool isBusy;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CameraButtonText))]
    private bool cameraVisible = true;

    public string CameraButtonText => CameraVisible ? "关闭相机" : "打开相机";
    [ObservableProperty] private bool canSubmit;

    [ObservableProperty] private bool isConfigured;
    [ObservableProperty] private string? configHint;

    [ObservableProperty] private string armHint = string.Empty;

    public string GatewayDisplay => string.IsNullOrWhiteSpace(GatewayId) ? "未扫描" : GatewayId;
    public string PnDisplay => string.IsNullOrWhiteSpace(Pn) ? "未扫描" : Pn;

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
        ResultVisible = false;
        ResultText = null;
        ResultDetail = null;
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
        SyncStatus();
    }

    [RelayCommand]
    private void ToggleCamera() => CameraVisible = !CameraVisible;

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
        ResultVisible = false;
        StatusText = "正在校验网关在线状态…";
        StatusColor = Color.FromArgb("#185FA5");
        OnlineText = "校验中…";
        OnlineColor = Color.FromArgb("#185FA5");

        _submitCts = new CancellationTokenSource(TimeSpan.FromSeconds(_configService.Current.TimeoutSeconds + 15));

        try
        {
            var outcome = await _sessionService.SubmitAsync(force, _submitCts.Token);

            ApplyOnlineStatus(outcome.OnlineStatus);

            ResultText = outcome.Message;
            ResultDetail = BuildResultDetail(outcome);
            ResultVisible = true;
            ResultColor = outcome.Success ? Color.FromArgb("#3B6D11") : Color.FromArgb("#A32D2D");

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
            ResultText = $"提交异常：{ex.Message}";
            ResultColor = Color.FromArgb("#A32D2D");
            ResultVisible = true;
            StatusText = "提交异常";
            StatusColor = Color.FromArgb("#A32D2D");
        }
        finally
        {
            IsBusy = false;
            SyncStatus();
        }
    }

    private static string BuildResultDetail(SubmitOutcome outcome)
    {
        var parts = new List<string>();

        if (outcome.StatusCode is int statusCode) parts.Add($"HTTP {statusCode}");
        if (outcome.ElapsedMs > 0) parts.Add($"耗时 {outcome.ElapsedMs}ms");
        if (!string.IsNullOrWhiteSpace(outcome.ServerMessage)) parts.Add($"服务端：{outcome.ServerMessage}");

        var head = string.Join(" · ", parts);
        if (string.IsNullOrWhiteSpace(outcome.Detail)) return head;

        return string.IsNullOrEmpty(head) ? outcome.Detail : $"{head}\n响应：{outcome.Detail}";
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
    }
}
