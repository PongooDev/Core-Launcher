using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Core.Launcher;

/// <summary>Current page → whether a nav item is checked (two-way) or a page is visible; the parameter names the page.</summary>
public sealed class PageConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        var matches = value is LauncherPage page && Enum.TryParse<LauncherPage>(parameter as string, out var expected) && page == expected;
        return targetType == typeof(Visibility) ? matches ? Visibility.Visible : Visibility.Collapsed : matches;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && Enum.TryParse<LauncherPage>(parameter as string, out var page) ? page : Binding.DoNothing;
}
