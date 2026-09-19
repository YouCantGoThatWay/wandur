using System.Globalization;
using Avalonia.Data.Converters;
using Wandur.Core.Settings;

namespace Wandur.Desktop.Converters;

public sealed class ThemeNameConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string name ? UserTheme.DisplayName(name) : value;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
