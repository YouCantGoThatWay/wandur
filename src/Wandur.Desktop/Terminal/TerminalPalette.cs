using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Wandur.Core.Terminal;

namespace Wandur.Desktop.Terminal;

/// <summary>
/// The one mapping from a transcript's ANSI colors to the themed brushes. The transcript surface and the
/// tail pane both read it, so the same output keeps the same color in both. The brushes themselves are the
/// application's palette instances, which a theme change recolors in place.
/// </summary>
internal static class TerminalPalette
{
    public const string Monospace = "Menlo, Consolas, DejaVu Sans Mono";
    /// <summary>One property per ANSI color, bound to the application palette by <see cref="Bind"/>.</summary>
    public static readonly StyledProperty<IBrush?>[] Colors = new StyledProperty<IBrush?>[AnsiPalette.Defaults.Count];
    // A 24-bit sequence can name any color, so the cache of literal brushes is bounded rather than unbounded.
    private static readonly Dictionary<string, IBrush> Literals = [];

    static TerminalPalette()
    { for (var i = 0; i < Colors.Length; i++) Colors[i] = AvaloniaProperty.Register<Control, IBrush?>("AnsiPalette" + i); }

    public static void Bind(Control control, List<IDisposable> bindings)
    { for (var i = 0; i < Colors.Length; i++) bindings.Add(control.Bind(Colors[i], new DynamicResourceExtension($"AnsiColor{i}Brush"))); }

    public static string Hex(IBrush? brush, string fallback) => brush is ISolidColorBrush b ? $"#{b.Color.R:X2}{b.Color.G:X2}{b.Color.B:X2}" : fallback;

    /// <summary>The brush for one styled run, or null where the reader's own foreground already applies.</summary>
    public static IBrush? Resolve(Control control, int? index, string? color)
    {
        if (index is { } palette && palette >= 0 && palette < Colors.Length) return control.GetValue(Colors[palette]);
        if (color is null) return null;
        if (Literals.TryGetValue(color, out var brush)) return brush;
        if (!Color.TryParse(color, out var parsed)) return null;
        if (Literals.Count >= 512) Literals.Clear();
        return Literals[color] = new SolidColorBrush(parsed);
    }
}
