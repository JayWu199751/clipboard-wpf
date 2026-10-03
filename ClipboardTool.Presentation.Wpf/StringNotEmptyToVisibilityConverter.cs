using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ClipboardTool.Presentation.Wpf;

/// <summary>非空字符串 → Visible（toast 的 dim 次级文本与动作按钮按需显示）。</summary>
public sealed class StringNotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string text && text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
