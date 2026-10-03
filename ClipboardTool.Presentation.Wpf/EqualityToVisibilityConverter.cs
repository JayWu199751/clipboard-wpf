using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ClipboardTool.Presentation.Wpf;

/// <summary>
/// 双值相等 → Visible（备注编辑：卡片 Id 与视图模型 NoteEditingId 相等时显示内联编辑框）。
/// ConverterParameter="Invert" 反转结果：meta 行在非编辑时显示，两态等高（F47 几何零位移）。
/// </summary>
public sealed class EqualityToVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var equal = values.Length == 2
            && values[0] is string left
            && values[1] is string right
            && left == right;
        if (parameter as string == "Invert")
        {
            equal = !equal;
        }
        return equal ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
