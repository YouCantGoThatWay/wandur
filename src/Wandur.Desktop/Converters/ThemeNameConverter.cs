using System.Globalization;
using Avalonia.Data.Converters;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.Converters;

public sealed class ThemeNameConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => (value as string) switch
    {
        "Ember" => L.ThemeEmber, "Moonlight" => L.ThemeMoonlight, "Forest" => L.ThemeForest, "Paper" => L.ThemePaper, _ => value
    };
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
