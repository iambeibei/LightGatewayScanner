using LightGatewayScanner.Models;

namespace LightGatewayScanner.Services;

public interface IScanClassifier
{
    /// <summary>标准化条码（去空白与控制字符）</summary>
    string Normalize(string? raw);

    /// <summary>判断是否为储能设备 PN（命中配置前缀）</summary>
    bool IsPn(string code, AppConfig config, out string matchedPrefix);

    /// <summary>分类：命中配置前缀 = 储能 PN；未命中且匹配网关正则 = 网关 ID；否则无法识别</summary>
    ScanCodeType Classify(string code, AppConfig config);
}
