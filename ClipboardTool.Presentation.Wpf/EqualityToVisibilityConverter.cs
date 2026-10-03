using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ClipboardTool.Presentation.Wpf;

/// <summary>
/// 双值相等 → Visible（备注编辑占位：卡片 Id 与视图模型 NoteEditingId 相等时才显示内联编辑框）。
/// </summary>
public sealed class EqualityToVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length == 2
            && values[0] is string left
            && values[1] is string right
            && left == right
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
