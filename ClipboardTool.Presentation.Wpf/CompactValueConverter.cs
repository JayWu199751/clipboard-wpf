using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ClipboardTool.Presentation.Wpf;

/// <summary>
/// 页脚紧凑态取值（F47 窄窗收紧）：bool（FooterCompact）→ 按 parameter「标准值|紧凑值」分段取值。
/// 用于字号（"10.5|9.5"）、组距 Margin（"8,0,0,0|3,0,0,0"）、留白 Padding（"16,0|10,0"）等标量，
/// 数值经 TypeConverter 由目标属性自行解析。
/// </summary>
public sealed class CompactValueConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var parts = (parameter as string)?.Split('|') ?? [string.Empty, string.Empty];
        var compact = value is bool flag && flag;
        var text = parts.Length > 1 && compact ? parts[1] : parts[0];
        return targetType.IsEnum
            ? Enum.Parse(targetType, text, ignoreCase: true)
            : System.ComponentModel.TypeDescriptor.GetConverter(targetType).ConvertFromInvariantString(text)!;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
