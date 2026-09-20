using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Dock.Model.Core;

namespace Wandur.Desktop.Converters;

/// <summary>
/// Shapes the tool chrome from the dock's <see cref="Alignment"/> so a side panel is rounded only on
/// its outer edge and meets the centre document flush: a left dock curves on the left, a right dock on
/// the right, and the centre document stays square. The converter parameter picks which part of the
/// chrome is being shaped: the header strip on top or the content panel below it.
/// </summary>
public sealed class DockChromeConverter : IValueConverter
{
    public static readonly DockChromeConverter Instance = new();

    /// <summary>Corner radius kept on the outer edge of a docked panel.</summary>
    public const double Radius = 10;

    /// <summary>Space left around a docked panel; between two panels a splitter adds the rest of the gap.</summary>
    public const double Edge = 4;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var alignment = value as Alignment? ?? Alignment.Unset;
        var left = alignment == Alignment.Left;
        var right = alignment == Alignment.Right;
        return (parameter as string) switch
        {
            "HeaderCorner" => new CornerRadius(right ? 0 : Radius, left ? 0 : Radius, 0, 0),
            "ContentCorner" => new CornerRadius(0, 0, left ? 0 : Radius, right ? 0 : Radius),
            "HeaderMargin" => new Thickness(right ? 0 : Edge, Edge, left ? 0 : Edge, 0),
            "ContentMargin" => new Thickness(right ? 0 : Edge, 0, left ? 0 : Edge, Edge),
            _ => AvaloniaProperty.UnsetValue
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
