using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Core.Launcher;

/// <summary>Negates a bool; with the parameter "Visibility" it gives Visible for false and Collapsed for true.</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        var inverted = value is not true;
        return parameter as string == "Visibility" ? inverted ? Visibility.Visible : Visibility.Collapsed : inverted;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}
