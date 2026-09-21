using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.VisualTree;
using Shape = Avalonia.Controls.Shapes.Shape;

namespace Wandur.Desktop.Tests;

/// <summary>
/// Measures what a glyph actually looks like on screen: its brush composited over the surface it lands
/// on, through every opacity its ancestors apply. Used to hold the presets legible and to catch chrome
/// that a theme switch left behind.
/// </summary>
internal static class ContrastProbe
{
    /// <summary>WCAG 2.x AA for body text.</summary>
    public const double EnabledFloor = 4.5;
    /// <summary>Disabled chrome is exempt from AA, but below this it stops reading as present at all.</summary>
    public const double DisabledFloor = 3.0;
    /// <summary>
    /// Fluent writes a fixed 0.5 opacity onto the placeholder element itself, and a local value outranks
    /// every style setter, so 4.5:1 is unreachable over a light surface whatever colour is used: pure black
    /// at half opacity over Daylight's shell tops out near 4.0:1. Removing that cap means retemplating
    /// TextBox and ComboBox. Until then a placeholder is held to the same floor as disabled chrome.
    /// </summary>
    public const double PlaceholderFloor = 3.0;

    public static bool IsPlaceholder(string? name) =>
        name is "PART_Placeholder" or "PART_Watermark" or "PlaceholderTextBlock";

    private static double Channel(byte v)
    {
        var s = v / 255.0;
        return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }
    public static double Luminance(Color c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    public static double Contrast(Color a, Color b)
    {
        var (x, y) = (Luminance(a), Luminance(b));
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }
    public static Color Over(Color foreground, Color surface, double opacity)
    {
        opacity *= foreground.A / 255.0;
        return Color.FromRgb(
            (byte)(surface.R + (foreground.R - surface.R) * opacity),
            (byte)(surface.G + (foreground.G - surface.G) * opacity),
            (byte)(surface.B + (foreground.B - surface.B) * opacity));
    }
    /// <summary>The nearest ancestor that paints an opaque color is the surface a glyph lands on.</summary>
    public static Color Surface(Visual visual, Color fallback)
    {
        for (Visual? v = visual; v is not null; v = v.GetVisualParent())
        {
            var background = v switch
            {
                Border b => b.Background, Panel p => p.Background, TemplatedControl c => c.Background, _ => null
            };
            if (Opaque(background) is { } painted) return painted;
        }
        return fallback;
    }
    /// <summary>Total opacity applied to a glyph, and who applies it, since the dimming is rarely local.</summary>
    public static (double Opacity, string Owner) Dimming(Visual visual)
    {
        double opacity = 1; var owners = new List<string>();
        for (Visual? v = visual; v is not null; v = v.GetVisualParent())
            if (v.Opacity < 1) { opacity *= v.Opacity; owners.Add($"{v.GetType().Name}{(v is Control { Name: { } n } ? "#" + n : "")}@{v.Opacity:0.00}"); }
        return (opacity, owners.Count == 0 ? "" : " dimmed by " + string.Join(" x ", owners));
    }

    /// <summary>A brush's colour if it fully covers what is behind it, averaging a gradient's stops.</summary>
    private static Color? Opaque(IBrush? brush) => brush switch
    {
        ISolidColorBrush { Color.A: 255 } solid => solid.Color,
        IGradientBrush { GradientStops.Count: > 0 } gradient when gradient.GradientStops.All(s => s.Color.A == 255) =>
            Color.FromRgb(
                (byte)gradient.GradientStops.Average(s => s.Color.R),
                (byte)gradient.GradientStops.Average(s => s.Color.G),
                (byte)gradient.GradientStops.Average(s => s.Color.B)),
        _ => null
    };

    public static Color Resource(string key) => ((ISolidColorBrush)Application.Current!.Resources[key]!).Color;

    /// <summary>Every visible label and control foreground that falls below the floor it has to clear.</summary>
    public static List<string> Scan(Visual root)
    {
        var shell = Resource("ShellBrush");
        var failures = new List<string>();

        void Check(string what, IBrush? brush, Visual host, bool enabled)
        {
            if (brush is not ISolidColorBrush solid) return;
            var surface = Surface(host, shell);
            var (opacity, owner) = Dimming(host);
            var ratio = Contrast(Over(solid.Color, surface, opacity), surface);
            var floor = IsPlaceholder((host as Control)?.Name) ? PlaceholderFloor : enabled ? EnabledFloor : DisabledFloor;
            if (ratio < floor) failures.Add($"{ratio,5:0.00} (need {floor:0.0})  {what}  {solid.Color} on {surface}{owner}");
        }

        foreach (var label in root.GetVisualDescendants().OfType<TextBlock>())
        {
            if (string.IsNullOrWhiteSpace(label.Text) || !label.IsEffectivelyVisible) continue;
            Check($"TextBlock {label.Name ?? "(unnamed)"} \"{Trim(label.Text)}\" in {label.TemplatedParent?.GetType().Name ?? "(none)"}",
                label.Foreground, label, label.IsEffectivelyEnabled);
        }
        foreach (var control in root.GetVisualDescendants().OfType<TemplatedControl>())
        {
            // Slider and scrollbar parts carry a foreground they never draw text with.
            if (control is RepeatButton or ScrollBar || !control.IsEffectivelyVisible) continue;
            if (control is not (ComboBox or TextBox or Button or TabStripItem or ToggleButton or CheckBox)) continue;
            if (!control.GetVisualDescendants().OfType<Shape>().Any()
                && !control.GetVisualDescendants().OfType<TextBlock>().Any(t => !string.IsNullOrWhiteSpace(t.Text))) continue;
            Check($"{control.GetType().Name} {control.Name ?? "(unnamed)"}", control.Foreground, control, control.IsEffectivelyEnabled);
        }
        return failures;
    }

    public static string Trim(string text) => text.Length <= 24 ? text : text[..24] + "…";
}
