using System.Globalization;

namespace LightGatewayScanner.Converters;

/// <summary>
/// 字符串非空 → true（用于"有错误时才显示重试按钮"这类场景）
/// </summary>
public sealed class StringToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => !string.IsNullOrWhiteSpace(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
