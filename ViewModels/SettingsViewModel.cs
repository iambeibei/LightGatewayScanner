using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightGatewayScanner.Models;
using LightGatewayScanner.Services;
using Microsoft.Maui.ApplicationModel;

namespace LightGatewayScanner.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly IAppConfigService _configService;
    private readonly IRedisOnlineService _redisOnlineService;
    private readonly ISetPnApiService _setPnApiService;
    private readonly IAppLogService _logService;

    public SettingsViewModel(
        IAppConfigService configService,
        IRedisOnlineService redisOnlineService,
        ISetPnApiService setPnApiService,
        IAppLogService logService)
    {
        _configService = configService;
        _redisOnlineService = redisOnlineService;
        _setPnApiService = setPnApiService;
        _logService = logService;
        LoadFromConfig();
    }

    // ---------- 接口 ----------
    [ObservableProperty] private string setPnUrl = string.Empty;
    [ObservableProperty] private string timeoutSecondsText = "10";
    [ObservableProperty] private bool allowInvalidCertificate;
    [ObservableProperty] private string apiSuccessCodes = "0,1,200";

    // ---------- Redis ----------
    [ObservableProperty] private string redisConnectionString = string.Empty;
    [ObservableProperty] private string onlineListKey = string.Empty;
    [ObservableProperty] private string redisTimeoutMsText = "5000";
    [ObservableProperty] private string onlineCacheSecondsText = "30";
    [ObservableProperty] private bool allowSubmitWhenRedisUnavailable;

    // ---------- 扫码规则 ----------
    [ObservableProperty] private string pnPrefixes = string.Empty;
    [ObservableProperty] private bool ignorePnCase;
    [ObservableProperty] private bool requireNumericSuffix;
    [ObservableProperty] private string gatewayPattern = string.Empty;
    [ObservableProperty] private string scanDedupMsText = "1200";
    [ObservableProperty] private bool requireScanOrder;
    [ObservableProperty] private bool overwriteDuplicate;

    // ---------- 行为 ----------
    [ObservableProperty] private bool autoSubmit;
    [ObservableProperty] private bool autoResetAfterSuccess;
    [ObservableProperty] private bool soundAndVibration;

    // ---------- 状态 ----------
    [ObservableProperty] private string? statusMessage;
    [ObservableProperty] private Color statusColor = Colors.Gray;
    [ObservableProperty] private string? redisTestResult;
    [ObservableProperty] private string? apiTestResult;
    [ObservableProperty] private bool isTesting;

    public string PrefixHint =>
        "储能设备 PN = 字符串部分 + 数字部分，例如 PN 为 PN123ABC 时这里填 PN。" +
        "多个前缀用逗号分隔。扫到的码命中前缀即为储能设备 PN，未命中的按网关 ID 规则处理。";

    /// <summary>构建标记，显示在设置页底部，用于确认手机装的是哪一版</summary>
    public string VersionText => $"扫码写入 APP · 构建 {AppConfig.BuildTag}";

    // ---------- 命令 ----------

    [RelayCommand]
    private void LoadFromConfig()
    {
        var c = _configService.Current;
        SetPnUrl = c.SetPnUrl;
        TimeoutSecondsText = c.TimeoutSeconds.ToString();
        AllowInvalidCertificate = c.AllowInvalidCertificate;
        ApiSuccessCodes = c.ApiSuccessCodes;

        RedisConnectionString = c.RedisConnectionString;
        OnlineListKey = c.OnlineListKey;
        RedisTimeoutMsText = c.RedisTimeoutMs.ToString();
        OnlineCacheSecondsText = c.OnlineCacheSeconds.ToString();
        AllowSubmitWhenRedisUnavailable = c.AllowSubmitWhenRedisUnavailable;

        PnPrefixes = c.PnPrefixes;
        IgnorePnCase = c.IgnorePnCase;
        RequireNumericSuffix = c.RequireNumericSuffix;
        GatewayPattern = c.GatewayPattern;
        ScanDedupMsText = c.ScanDedupMs.ToString();
        RequireScanOrder = c.RequireScanOrder;
        OverwriteDuplicate = c.OverwriteDuplicate;

        AutoSubmit = c.AutoSubmit;
        AutoResetAfterSuccess = c.AutoResetAfterSuccess;
        SoundAndVibration = c.SoundAndVibration;
    }

    [RelayCommand]
    private void Save()
    {
        var config = new AppConfig
        {
            SetPnUrl = SetPnUrl?.Trim() ?? string.Empty,
            TimeoutSeconds = ParseInt(TimeoutSecondsText, 10),
            AllowInvalidCertificate = AllowInvalidCertificate,
            ApiSuccessCodes = ApiSuccessCodes ?? string.Empty,

            RedisConnectionString = RedisConnectionString?.Trim() ?? string.Empty,
            OnlineListKey = OnlineListKey?.Trim() ?? string.Empty,
            RedisTimeoutMs = ParseInt(RedisTimeoutMsText, 5000),
            OnlineCacheSeconds = ParseInt(OnlineCacheSecondsText, 30),
            AllowSubmitWhenRedisUnavailable = AllowSubmitWhenRedisUnavailable,

            PnPrefixes = PnPrefixes ?? string.Empty,
            IgnorePnCase = IgnorePnCase,
            RequireNumericSuffix = RequireNumericSuffix,
            GatewayPattern = GatewayPattern ?? string.Empty,
            ScanDedupMs = ParseInt(ScanDedupMsText, 1200),
            RequireScanOrder = RequireScanOrder,
            OverwriteDuplicate = OverwriteDuplicate,

            AutoSubmit = AutoSubmit,
            AutoResetAfterSuccess = AutoResetAfterSuccess,
            SoundAndVibration = SoundAndVibration
        };

        if (!_configService.Save(config, out var error))
        {
            StatusMessage = $"保存失败：\n{error}";
            StatusColor = Color.FromArgb("#A32D2D");
            return;
        }

        LoadFromConfig();
        StatusMessage = "已保存，重启后仍然生效";
        StatusColor = Color.FromArgb("#3B6D11");
    }

    [RelayCommand]
    private async Task ResetAsync()
    {
        var page = Application.Current?.Windows.FirstOrDefault()?.Page;
        if (page is not null)
        {
            var ok = await page.DisplayAlertAsync("恢复默认", "将恢复全部配置为出厂默认值，确认继续？", "恢复", "取消");
            if (!ok) return;
        }

        _configService.ResetToDefault();
        LoadFromConfig();
        StatusMessage = "已恢复默认配置";
        StatusColor = Color.FromArgb("#3B6D11");
    }

    [RelayCommand]
    private async Task TestRedisAsync()
    {
        IsTesting = true;
        RedisTestResult = "正在连接 Redis…";
        try
        {
            var result = await _redisOnlineService.TestAsync();
            RedisTestResult = result.Success
                ? result.Message + (result.SampleIds.Count > 0 ? $"\n示例：{string.Join("、", result.SampleIds)}" : "")
                : $"失败：{result.Message}";
        }
        catch (Exception ex)
        {
            RedisTestResult = $"异常：{ex.Message}";
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand]
    private async Task TestApiAsync()
    {
        IsTesting = true;
        ApiTestResult = "正在探测接口连通性…";
        try
        {
            ApiTestResult = await _setPnApiService.ProbeAsync();
        }
        catch (Exception ex)
        {
            ApiTestResult = $"异常：{ex.Message}";
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        var json = _configService.ExportJson();
        try
        {
            await Clipboard.Default.SetTextAsync(json);
            StatusMessage = "配置已复制到剪贴板";
            StatusColor = Color.FromArgb("#3B6D11");
        }
        catch (Exception)
        {
            StatusMessage = json;
            StatusColor = Colors.Gray;
        }
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        string? json = null;
        try
        {
            if (Clipboard.Default.HasText)
                json = await Clipboard.Default.GetTextAsync();
        }
        catch (Exception)
        {
            // 忽略剪贴板读取失败
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            var page = Application.Current?.Windows.FirstOrDefault()?.Page;
            if (page is not null)
                json = await page.DisplayPromptAsync("导入配置", "粘贴配置 JSON", "导入", "取消",
                    placeholder: "{\"SetPnUrl\":\"...\"}", keyboard: Keyboard.Text);
        }

        if (string.IsNullOrWhiteSpace(json)) return;

        if (_configService.ImportJson(json, out var error))
        {
            LoadFromConfig();
            StatusMessage = "配置已导入";
            StatusColor = Color.FromArgb("#3B6D11");
        }
        else
        {
            StatusMessage = $"导入失败：{error}";
            StatusColor = Color.FromArgb("#A32D2D");
        }
    }

    [RelayCommand]
    private async Task ViewLogAsync()
    {
        var text = await _logService.ReadTodayAsync();
        if (text.Length > 2000) text = "…" + text[^2000..];

        var page = Application.Current?.Windows.FirstOrDefault()?.Page;
        if (page is not null)
            await page.DisplayAlertAsync("今日日志", text, "关闭");
    }

    private static int ParseInt(string? text, int fallback)
        => int.TryParse(text?.Trim(), out var value) ? value : fallback;
}
