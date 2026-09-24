using Avalonia.Controls;
using Avalonia.Layout;

namespace Wandur.Desktop;

/// <summary>Original vector symbols, using a common 16-unit optical grid.</summary>
internal static class FleetIcons
{
    public const string Connect = "M 13.4,5.2 A 5.7,5.7 0 1 0 13.5,10 M 13.4,1.4 V 5.4 H 9.4";
    public const string Search = "M 6.5,1 A 5.5,5.5 0 1 0 6.5,12 A 5.5,5.5 0 1 0 6.5,1 M 10.5,10.5 L 15,15";
    public const string Settings = "M 6.5,0 H 9.5 L 10,2.3 L 11.3,2.9 L 13.3,1.7 L 15.3,3.8 L 14,5.7 L 14.6,7 L 17,7.5 V 10.5 L 14.6,11 L 14,12.3 L 15.3,14.2 L 13.3,16.3 L 11.3,15.1 L 10,15.7 L 9.5,18 H 6.5 L 6,15.7 L 4.7,15.1 L 2.7,16.3 L .7,14.2 L 2,12.3 L 1.4,11 L -1,10.5 V 7.5 L 1.4,7 L 2,5.7 L .7,3.8 L 2.7,1.7 L 4.7,2.9 L 6,2.3 Z M 8,5.7 A 3.3,3.3 0 1 0 8,12.3 A 3.3,3.3 0 1 0 8,5.7 Z";

    public static Control Action(string geometry, string labelKey, bool filled = false)
    {
        var label = Ui.TextKey(labelKey, 13);
        // Older button captions include a text glyph; this toolbar supplies its own vector.
        var binding = LocalizedText.Binding(labelKey);
        binding.Converter = new Avalonia.Data.Converters.FuncValueConverter<string?, string?>(text => text?.TrimStart('▶', ' '));
        label.Bind(TextBlock.TextProperty, binding);
        label.Classes.Add("fleet-action-label");
        label.VerticalAlignment = VerticalAlignment.Center;
        var glyph = Ui.ChromeGlyph(geometry, filled, 16);
        glyph.VerticalAlignment = VerticalAlignment.Center;
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center, Children = { glyph, label } };
    }
}
