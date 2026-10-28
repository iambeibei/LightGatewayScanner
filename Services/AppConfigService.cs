using System.Text.Json;
using LightGatewayScanner.Models;
using Microsoft.Maui.Storage;

namespace LightGatewayScanner.Services;

/// <summary>
/// 配置持久化：Preferences（Android 底层为 SharedPreferences），重启后仍然生效。
/// 整个 AppConfig 以 JSON 形式存放在一个 key 下，便于导入导出与版本迁移。
/// </summary>
public sealed class AppConfigService : IAppConfigService
{
    private const string StorageKey = "app_config";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public AppConfig Current { get; private set; } = new();

    public event EventHandler? ConfigChanged;

    public void Load()
    {
        try
        {
            var json = Preferences.Default.Get<string>(StorageKey, string.Empty);
            if (!string.IsNullOrWhiteSpace(json))
            {
                json = MigrateLegacyKeys(json);
                var loaded = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
                if (loaded is not null)
                {
                    Current = Migrate(loaded);
                    Persist(Current); // 迁移结果立即落盘，避免每次启动重复判断
                    return;
                }
            }
        }
        catch (Exception)
        {
            // 配置损坏时回退到默认值，保证 APP 能启动
        }

        Current = new AppConfig();
        Persist(Current);
    }

    public bool Save(AppConfig config, out string error)
    {
        var errors = config.Validate();
        if (errors.Count > 0)
        {
            error = string.Join("\n", errors.Select(e => $"· {e.Message}"));
            return false;
        }

        var previousMqtt = MqttSignature(Current);
        var previousRedis = Current.RedisConnectionString;
        var previousKey = Current.OnlineListKey;
        var previousPrefixes = Current.PnPrefixes;
        var previousCert = Current.AllowInvalidCertificate;

        Current = config.Clone();
        Current.ConfigVersion = AppConfig.CurrentVersion;
        Persist(Current);

        // MQTT 连接参数变化需要让客户端重连；回执超时不参与（不影响连接）
        bool material = !string.Equals(previousMqtt, MqttSignature(Current), StringComparison.Ordinal)
                        || !string.Equals(previousRedis, Current.RedisConnectionString, StringComparison.Ordinal)
                        || !string.Equals(previousKey, Current.OnlineListKey, StringComparison.Ordinal)
                        || !string.Equals(previousPrefixes, Current.PnPrefixes, StringComparison.Ordinal)
                        || previousCert != Current.AllowInvalidCertificate;

        error = material ? string.Empty : "已保存";
        ConfigChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private static string MqttSignature(AppConfig config)
        => $"{config.MqttHost?.Trim()}|{config.MqttPort}|{config.MqttUsername?.Trim()}|{config.MqttPassword}";

    public void ResetToDefault()
    {
        Current = new AppConfig();
        Persist(Current);
        ConfigChanged?.Invoke(this, EventArgs.Empty);
    }

    public string ExportJson() => JsonSerializer.Serialize(Current, JsonOptions);

    public bool ImportJson(string json, out string error)
    {
        try
        {
            var imported = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            if (imported is null)
            {
                error = "配置内容为空或格式不正确";
                return false;
            }

            return Save(imported, out error);
        }
        catch (JsonException ex)
        {
            error = $"配置 JSON 解析失败：{ex.Message}";
            return false;
        }
    }

    private void Persist(AppConfig config)
    {
        try
        {
            Preferences.Default.Set<string>(StorageKey, JsonSerializer.Serialize(config, JsonOptions));
        }
        catch (Exception)
        {
            // 持久化失败不应导致崩溃
        }
    }

    /// <summary>
    /// 旧版本配置用的是"网关字符串前缀"，现已改为"储能设备字符串前缀"。
    /// 这里把老 key 名映射过来，避免老用户升级后配置丢失。
    /// </summary>
    private static string MigrateLegacyKeys(string json)
        => json
            .Replace("\"GatewayPrefixes\"", "\"PnPrefixes\"", StringComparison.OrdinalIgnoreCase)
            .Replace("\"IgnoreGatewayCase\"", "\"IgnorePnCase\"", StringComparison.OrdinalIgnoreCase)
            .Replace("\"PnPattern\"", "\"GatewayPattern\"", StringComparison.OrdinalIgnoreCase);

    private static AppConfig Migrate(AppConfig loaded)
    {
        if (loaded.ConfigVersion >= AppConfig.CurrentVersion) return loaded;

        // v2：旧默认成功码 0,200 不认 code=1，会把 {"code":1,...} 误判成失败，
        // 这里统一换成 0,1,200（用户显式改过的保持不动）。
        // 该字段现在只服务于老化架 / 点位接口，仍需保留迁移。
        if (string.Equals((loaded.ApiSuccessCodes ?? string.Empty).Trim(), AppConfig.LegacyApiSuccessCodes, StringComparison.OrdinalIgnoreCase))
            loaded.ApiSuccessCodes = AppConfig.DefaultApiSuccessCodes;

        // v3：设置 PN 改为 MQTT，SetPnUrl 字段已删除。
        // 老配置 JSON 里残留的 "SetPnUrl" 会被 System.Text.Json 默认忽略（未开启 Disallow），
        // 不会导致反序列化异常，下次保存后自动消失；MQTT 参数直接用出厂默认值。

        // v4：新增 BLE 扫描配置（BleScanEnabled / BleScanTimeoutSeconds），
        // 老配置缺这两个字段时保留属性初始值（true / 5），无需数据迁移。

        // v5：新增 BleMinRssi / BleMacCacheSeconds，缺字段时保留初始值（0 / 120），同样无需迁移。

        loaded.ConfigVersion = AppConfig.CurrentVersion;
        return loaded;
    }
}
