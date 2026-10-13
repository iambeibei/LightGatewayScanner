using System.Text.RegularExpressions;
using LightGatewayScanner.Models;

namespace LightGatewayScanner.Services;

/// <summary>
/// 条码分类器。
/// 规则：扫码值命中 APP 里配置的"储能设备字符串前缀"→ 储能设备 PN；
/// 未命中前缀的再按网关 ID 正则校验 → 网关 ID；都不满足 → 无法识别。
/// 这样两个槽位的识别互不依赖"是否纯数字"，避免纯数字 PN / 纯数字网关 ID 的误判。
/// </summary>
public sealed partial class ScanClassifier : IScanClassifier
{
    private const int MaxCodeLength = 128;

    [GeneratedRegex(@"^\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex NumericRegex();

    public string Normalize(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;

        var chars = raw.Where(c => !char.IsControl(c)).ToArray();
        return new string(chars).Trim();
    }

    public bool IsPn(string code, AppConfig config, out string matchedPrefix)
    {
        matchedPrefix = string.Empty;
        if (string.IsNullOrEmpty(code) || code.Length > MaxCodeLength) return false;

        var comparison = config.IgnorePnCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        foreach (var prefix in config.GetPnPrefixList())
        {
            if (!code.StartsWith(prefix, comparison)) continue;
            if (code.Length <= prefix.Length) continue; // 只有前缀没有编号，不算设备 PN

            if (config.RequireNumericSuffix)
            {
                var suffix = code[prefix.Length..];
                if (!NumericRegex().IsMatch(suffix)) continue;
            }

            matchedPrefix = prefix;
            return true;
        }

        return false;
    }

    public ScanCodeType Classify(string code, AppConfig config)
    {
        if (string.IsNullOrEmpty(code) || code.Length > MaxCodeLength)
            return ScanCodeType.Unknown;

        // 命中配置的储能设备字符串前缀 → 储能 PN
        if (IsPn(code, config, out _))
            return ScanCodeType.Pn;

        // 未命中前缀 → 按网关 ID 规则校验（另一个码就是网关 ID）
        if (string.IsNullOrWhiteSpace(config.GatewayPattern))
            return ScanCodeType.GatewayId;

        try
        {
            var regex = new Regex(config.GatewayPattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            return regex.IsMatch(code) ? ScanCodeType.GatewayId : ScanCodeType.Unknown;
        }
        catch (ArgumentException)
        {
            // 配置里的正则非法时，退化为"非 PN 即网关 ID"，避免完全不可用
            return ScanCodeType.GatewayId;
        }
    }
}
