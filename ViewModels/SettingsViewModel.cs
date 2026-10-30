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
    private readonly IMqttSetPnService _mqttSetPnService;
    private readonly IDeviceCallbackApiService _deviceCallbackApi;
    private readonly IBleScanner _bleScanner;
    private readonly IAppLogService _logService;

    public SettingsViewModel(
        IAppConfigService configService,
        IRedisOnlineService redisOnlineService,
        IMqttSetPnService mqttSetPnService,
        IDeviceCallbackApiService deviceCallbackApi,
        IBleScanner bleScanner,
        IAppLogService logService)
    {
        _configService = configService;
        _redisOnlineService = redisOnlineService;
        _mqttSetPnService = mqttSetPnService;
        _deviceCallbackApi = deviceCallbackApi;
        _bleScanner = bleScanner;
        _logService = logService;

        _mqttSetPnService.StateChanged += (_, _) => MainThread.BeginInvokeOnMainThread(RefreshMqttState);

        // 主界面 PN 快捷面板保存后，设置页要同步这三个字段；
        // 只同步扫码规则，避免把用户在本页尚未保存的其他编辑覆盖掉。
        _configService.ConfigChanged += (_, _) =>
            MainThread.BeginInvokeOnMainThread(SyncScanRuleFields);

        LoadFromConfig();
        RefreshMqttState();
    }

    /// <summary>只回显与主界面快捷面板共用的 PN 过滤字段</summary>
    private void SyncScanRuleFields()
    {
        var c = _configService.Current;

        if (!string.Equals(PnPrefixes, c.PnPrefixes, StringComparison.Ordinal))
            PnPrefixes = c.PnPrefixes;

        if (IgnorePnCase != c.IgnorePnCase)
            IgnorePnCase = c.IgnorePnCase;

        if (RequireNumericSuffix != c.RequireNumericSuffix)
            RequireNumericSuffix = c.RequireNumericSuffix;
    }

    // ---------- 蓝牙（BLE） ----------
    [ObservableProperty] private bool bleScanEnabled;
    [ObservableProperty] private string bleScanTimeoutText = "5";
    [ObservableProperty] private string bleMinRssiText = "0";
    [ObservableProperty] private string bleMacCacheSecondsText = "120";
    [ObservableProperty] private string? bleScanTestResult;
    [ObservableProperty] private bool isBleTesting;

    // ---------- MQTT（设置 PN） ----------
    [ObservableProperty] private string mqttHost = string.Empty;
    [ObservableProperty] private string mqttPortText = "1883";
    [ObservableProperty] private string mqttUsername = string.Empty;
    [ObservableProperty] private string mqttPassword = string.Empty;
    [ObservableProperty] private string mqttReplyTimeoutText = "5";
    [ObservableProperty] private string mqttStateText = "未连接";
    [ObservableProperty] private Color mqttStateColor = Colors.Gray;
    [ObservableProperty] private string? mqttTestResult;

    // ---------- HTTP 接口（老化架 / 点位） ----------
    [ObservableProperty] private string timeoutSecondsText = "10";
    [ObservableProperty] private bool allowInvalidCertificate;
    [ObservableProperty] private string apiSuccessCodes = "0,1,200";

    // ---------- API 模式接口（选老化架 / 点位） ----------
    [ObservableProperty] private string rackPageListUrl = string.Empty;
    [ObservableProperty] private string availablePointListUrl = string.Empty;

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
    [ObservableProperty] private bool scan1D = true;
    [ObservableProperty] private bool scan2D = true;

    // ---------- 行为 ----------
    [ObservableProperty] private bool autoSubmit;
    [ObservableProperty] private bool autoResetAfterSuccess;
    [ObservableProperty] private bool soundAndVibration;

    // ---------- 状态 ----------
    [ObservableProperty] private string? statusMessage;
    [ObservableProperty] private Color statusColor = Colors.Gray;
    [ObservableProperty] private string? redisTestResult;
    [ObservableProperty] private string? rackTestResult;
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
        MqttHost = c.MqttHost;
        MqttPortText = c.MqttPort.ToString();
        MqttUsername = c.MqttUsername;
        MqttPassword = c.MqttPassword;
        MqttReplyTimeoutText = c.MqttReplyTimeoutSeconds.ToString();

        BleScanEnabled = c.BleScanEnabled;
        BleScanTimeoutText = c.BleScanTimeoutSeconds.ToString();
        BleMinRssiText = c.BleMinRssi.ToString();
        BleMacCacheSecondsText = c.BleMacCacheSeconds.ToString();

        TimeoutSecondsText = c.TimeoutSeconds.ToString();
        AllowInvalidCertificate = c.AllowInvalidCertificate;
        ApiSuccessCodes = c.ApiSuccessCodes;

        RackPageListUrl = c.RackPageListUrl;
        AvailablePointListUrl = c.AvailablePointListUrl;

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
        Scan1D = c.Scan1D;
        Scan2D = c.Scan2D;

        AutoSubmit = c.AutoSubmit;
        AutoResetAfterSuccess = c.AutoResetAfterSuccess;
        SoundAndVibration = c.SoundAndVibration;
    }

    [RelayCommand]
    private void Save()
    {
        var config = new AppConfig
        {
            MqttHost = MqttHost?.Trim() ?? string.Empty,
            MqttPort = ParseInt(MqttPortText, 1883),
            MqttUsername = MqttUsername?.Trim() ?? string.Empty,
            MqttPassword = MqttPassword ?? string.Empty,
            MqttReplyTimeoutSeconds = ParseInt(MqttReplyTimeoutText, 5),

            BleScanEnabled = BleScanEnabled,
            BleScanTimeoutSeconds = ParseInt(BleScanTimeoutText, 5),
            BleMinRssi = ParseInt(BleMinRssiText, 0),
            BleMacCacheSeconds = ParseInt(BleMacCacheSecondsText, 120),

            TimeoutSeconds = ParseInt(TimeoutSecondsText, 10),
            AllowInvalidCertificate = AllowInvalidCertificate,
            ApiSuccessCodes = ApiSuccessCodes ?? string.Empty,

            RackPageListUrl = RackPageListUrl?.Trim() ?? string.Empty,
            AvailablePointListUrl = AvailablePointListUrl?.Trim() ?? string.Empty,

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
            Scan1D = Scan1D,
            Scan2D = Scan2D,

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

    /// <summary>测试 MQTT：用页面当前输入值建临时连接并订阅回执主题，不会发送写入命令</summary>
    [RelayCommand]
    private async Task TestMqttAsync()
    {
        IsTesting = true;
        MqttTestResult = "正在连接 MQTT…";
        try
        {
            MqttTestResult = await _mqttSetPnService.ProbeAsync(
                MqttHost?.Trim() ?? string.Empty,
                ParseInt(MqttPortText, 1883),
                MqttUsername?.Trim(),
                MqttPassword);
        }
        catch (Exception ex)
        {
            MqttTestResult = $"异常：{ex.Message}";
        }
        finally
        {
            IsTesting = false;
        }
    }

    /// <summary>刷新常驻连接状态显示</summary>
    private void RefreshMqttState()
    {
        MqttStateText = _mqttSetPnService.IsConnected
            ? $"● 已连接 {_mqttSetPnService.Endpoint}"
            : $"○ 未连接 {_mqttSetPnService.Endpoint}";
        MqttStateColor = _mqttSetPnService.IsConnected
            ? Color.FromArgb("#3B6D11")
            : Color.FromArgb("#A32D2D");
    }

    /// <summary>测试蓝牙：扫 3 秒列出附近带名称的广播设备，便于现场确认 PN 是否在广播</summary>
    [RelayCommand]
    private async Task TestBleScanAsync()
    {
        IsBleTesting = true;
        BleScanTestResult = "正在扫描蓝牙（3 秒）…";
        try
        {
            var result = await _bleScanner.ScanNearbyAsync(3);

            if (!result.Success)
            {
                BleScanTestResult = $"扫描失败：{result.Error}";
                return;
            }

            if (result.Devices.Count == 0)
            {
                BleScanTestResult = "3 秒内未发现任何带名称的广播设备";
                return;
            }

            var shown = Math.Min(10, result.Devices.Count);
            var lines = result.Devices.Take(shown).Select(d =>
            {
                var head = $"{d.Name}  {d.Mac}  {d.Rssi}dBm{(d.Connectable == false ? "  不可连" : string.Empty)}";
                return string.IsNullOrWhiteSpace(d.AdText)
                    ? head
                    : head + "\n    " + d.AdText.Replace("\n", "\n    ");
            });

            BleScanTestResult = $"发现 {result.Devices.Count} 个设备（显示前 {shown} 个）：\n" + string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            BleScanTestResult = $"异常：{ex.Message}";
        }
        finally
        {
            IsBleTesting = false;
        }
    }

    /// <summary>测试 API 模式的老化架接口：真发一次请求，返回条数或失败原因</summary>
    [RelayCommand]
    private async Task TestRackApiAsync()
    {
        IsTesting = true;
        RackTestResult = "正在请求老化架列表…";
        try
        {
            var result = await _deviceCallbackApi.GetRackListAsync();
            RackTestResult = result.Success
                ? $"成功：共 {result.Items.Count} 个老化架（total={result.Total}）"
                : $"失败：{result.Message}";
        }
        catch (Exception ex)
        {
            RackTestResult = $"异常：{ex.Message}";
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
                    placeholder: "{\"MqttHost\":\"192.168.1.65\"}", keyboard: Keyboard.Text);
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
