using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MDReader;

/// <summary>
/// 大纲层级缩进：int 像素 → Thickness(Left,0,0,0)。
/// </summary>
public sealed class IndentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double left = 0;
        if (value is int i) left = i;
        else if (value is double d) left = d;
        return new Thickness(left, 0, 0, 0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
