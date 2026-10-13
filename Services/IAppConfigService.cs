using LightGatewayScanner.Models;

namespace LightGatewayScanner.Services;

public interface IAppConfigService
{
    /// <summary>当前生效配置</summary>
    AppConfig Current { get; }

    event EventHandler? ConfigChanged;

    /// <summary>从 Preferences 载入（首次启动写入默认值）</summary>
    void Load();

    /// <summary>校验并保存，成功返回 true</summary>
    bool Save(AppConfig config, out string error);

    void ResetToDefault();

    string ExportJson();

    bool ImportJson(string json, out string error);
}
