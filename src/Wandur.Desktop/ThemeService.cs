using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Wandur.Core.Settings;
using Wandur.Core.Discovery;
using Avalonia.Themes.Fluent;
using Avalonia.Media.Imaging;

namespace Wandur.Desktop;

/// <summary>
/// Writes the application palette in place. Every write to <see cref="Application.Resources"/> raises a
/// resources-changed notification that re-evaluates every DynamicResource in the visual tree, which costs
/// milliseconds per key, so each key keeps one brush for the life of the process and a theme change only
/// updates that brush's color. Views that read brush colors instead of binding them listen to
/// <see cref="ThemeService.Applied"/>.
/// </summary>
internal sealed class ThemeResources(Application app)
{
    private readonly Dictionary<string, SolidColorBrush> _solid = [];
    private readonly Dictionary<string, LinearGradientBrush> _gradients = [];
    public Application App { get; } = app;

    public IBrush Read(string key) => (IBrush)App.Resources[key]!;

    public void Color(string key, string color) => Color(key, Avalonia.Media.Color.Parse(color));
    public void Color(string key, Color color)
    {
        if (_solid.TryGetValue(key, out var brush)) brush.Color = color;
        else _solid[key] = brush = new SolidColorBrush(color);
        Brush(key, brush);
    }

    public void Gradient(string key, Color top, Color bottom)
    {
        if (_gradients.TryGetValue(key, out var brush))
        {
            brush.GradientStops[0].Color = top;
            brush.GradientStops[1].Color = bottom;
        }
        else
            _gradients[key] = brush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = [new GradientStop(top, 0), new GradientStop(bottom, 1)]
            };
        Brush(key, brush);
    }

    /// <summary>Publishes a brush this class does not own, such as a world theme's texture.</summary>
    public void Brush(string key, IBrush brush) => Value(key, brush);

    public void Value(string key, object value)
    {
        // Reference equality is what matters for brushes: the same instance needs no notification.
        if (App.Resources.TryGetValue(key, out var existing) && (ReferenceEquals(existing, value) || Equals(existing, value))) return;
        App.Resources[key] = value;
    }
}

public static class ThemeService
{
    public static readonly string[] Names = UserTheme.PresetNames.ToArray();
    /// <summary>Raised once per applied palette, for views that read brush colors rather than binding them.</summary>
    public static event Action? Applied;
    private static (Application App, string Theme, string? Foreground, string? Background, WorldTheme? World, UserTheme? Personal, UserTheme? AnsiTheme, IReadOnlyDictionary<string, Bitmap>? Images)? _lastAppearance;
    /// <summary>The world theme currently on screen, for tests that assert what the palette came from.</summary>
    internal static WorldTheme? AppliedWorldTheme => _lastAppearance?.World;
    private static ThemeResources? _resources;
    private static (string Accent, string Panel, string Text, string Muted, string Shell, string Line)? _lastFluentPalette;
    private static Color? _gripColor;
    public static void Apply(ClientSettings settings) => Apply(settings, null);
    public static void Apply(ClientSettings settings, WorldTheme? worldTheme, IReadOnlyDictionary<string, Bitmap>? images = null)
    {
        var app = Application.Current!;
        SessionOpenTrace.Count("theme apply calls");
        worldTheme = settings.UseWorldThemes && worldTheme is { IsValid: true } ? worldTheme : null;
        var personal = worldTheme is null ? settings.CustomThemes.FirstOrDefault(t => t.Id == settings.Theme) : null;
        if (personal is not null) { worldTheme = personal.ToWorldTheme(); images = null; }
        var ansiTheme = settings.CustomThemes.FirstOrDefault(t => t.Id == settings.Theme);
        var appearance = (app, settings.Theme, settings.Foreground, settings.Background, worldTheme, personal, ansiTheme, images);
        // Repainting an appearance already on screen changes nothing, and a session open raises this
        // a dozen times or more, so the identical case leaves without validating or writing a brush.
        if (_lastAppearance == appearance) return;
        SessionOpenTrace.Count("theme repaints");
        settings.Validate();
        if (_resources is null || !ReferenceEquals(_resources.App, app))
        { _resources = new(app); _lastFluentPalette = null; _gripColor = null; }
        var resources = _resources;
        resources.Value("ControlCornerRadius", new CornerRadius(worldTheme?.CornerRadius ?? 8));
        resources.Value("CardCornerRadius", new CornerRadius(worldTheme?.CornerRadius ?? 12));
        var presetTheme = UserTheme.FromPreset(settings.Theme);
        bool light = worldTheme is null ? presetTheme.IsLight : worldTheme.Variant == "light";
        // Avalonia ignores a repeated variant; the Fluent palettes are rebuilt only when they change.
        app.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        var preset = presetTheme.Colors;
        var (shell, panel, terminal, text, muted, accent, line) =
            (preset["Shell"], preset["Panel"], preset["Terminal"], preset["Text"], preset["Muted"], preset["Accent"], preset["Border"]);
        if (worldTheme is { Colors: var colors })
            (shell, panel, terminal, text, muted, accent, line) =
                (colors.Shell, colors.Panel, colors.Terminal, colors.Text, colors.Muted, colors.Accent, colors.Border);
        var fluentPalette = worldTheme is null ? default((string, string, string, string, string, string)?) : (accent, panel, text, muted, shell, line);
        if (_lastFluentPalette != fluentPalette)
        {
            _lastFluentPalette = fluentPalette;
            foreach (var fluent in app.Styles.OfType<FluentTheme>())
                foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
                    fluent.Palettes[variant] = worldTheme is null ? new ColorPaletteResources() : new ColorPaletteResources
                    {
                        Accent = Color.Parse(accent), RegionColor = Color.Parse(panel),
                        BaseHigh = Color.Parse(text), BaseMedium = Color.Parse(muted),
                        ChromeLow = Color.Parse(shell), ChromeMedium = Color.Parse(panel),
                        ListLow = Color.Parse(panel), ListMedium = Color.Parse(line)
                    };
        }
        void Set(string key, string color) => resources.Color(key, color);
        var background = settings.Background ?? terminal;
        // A preset's sixteen colors are chosen for its own terminal background. A world theme or a custom
        // background color can invert that lightness, and the preset palette would then be illegible, so
        // the colors come from a preset that suits the background actually in use. A custom theme keeps
        // the colors its editor shows, including over a world theme.
        var readable = UserTheme.PaletteForBackground(background);
        var presetPalette = UserTheme.IsLightBackground(preset["Terminal"]) == UserTheme.IsLightBackground(background)
            ? presetTheme.AnsiColors : null;
        for (var i = 0; i < Wandur.Core.Terminal.AnsiPalette.Defaults.Count; i++)
            Set($"AnsiColor{i}Brush", ansiTheme is not null
                ? ansiTheme.AnsiColors.GetValueOrDefault(i) ?? Wandur.Core.Terminal.AnsiPalette.Defaults[i]
                : presetPalette?.GetValueOrDefault(i) ?? readable[i]);
        Set("ShellBrush", shell); Set("PanelBrush", panel); Set("TextBrush", text);
        Set("TerminalBrush", background);
        Set("TerminalTextBrush", settings.Foreground ?? worldTheme?.Colors.TerminalText ?? text);
        Set("MapCanvasBrush", worldTheme?.Colors.Terminal ?? preset["MapBackground"]);
        Set("MapGridBrush", worldTheme?.Colors.Border ?? preset["MapGrid"]);
        Set("MutedBrush", muted); Set("AccentBrush", accent); Set("LineBrush", line);
        Set("SecondaryAccentBrush", worldTheme?.Colors.AccentSecondary ?? accent);
        Set("EditorBackgroundBrush", worldTheme?.Colors.Terminal ?? preset["EditorBackground"]);
        Set("EditorTextBrush", worldTheme?.Colors.Text ?? preset["EditorText"]);
        Set("EditorLineNumbersBrush", worldTheme?.Colors.Muted ?? (light ? "#57606A" : "#8B949E"));
        var surface = Color.Parse(personal?.Colors["Button"] ?? panel);
        var highlight = Color.Parse(worldTheme?.Colors.AccentSecondary ?? accent);
        Color Mix(Color color, Color target, double amount) => Color.FromRgb(
            (byte)(color.R + (target.R - color.R) * amount),
            (byte)(color.G + (target.G - color.G) * amount),
            (byte)(color.B + (target.B - color.B) * amount));
        if (worldTheme is null) Set("WorldSelectionBrush", line);
        else resources.Color("WorldSelectionBrush", Mix(surface, Color.Parse(accent), .16));
        resources.Gradient("ButtonFaceBrush", Mix(surface, Colors.White, light ? .8 : .065), surface);
        resources.Gradient("ButtonHoverBrush", Mix(surface, Colors.White, light ? 1 : .13), Mix(surface, Colors.White, .04));
        resources.Gradient("ButtonEdgeBrush", Mix(surface, Colors.White, light ? .1 : .2), Mix(surface, Colors.Black, .1));
        resources.Color("ToolbarHoverBrush", Mix(surface, light ? Colors.Black : Colors.White, .07));
        resources.Color("ToolbarPressedBrush", Mix(surface, light ? Colors.Black : Colors.White, .13));
        resources.Gradient("PrimaryFaceBrush", Mix(highlight, Colors.White, .25), Mix(highlight, Colors.Black, .08));
        resources.Gradient("PrimaryHoverBrush", Mix(highlight, Colors.White, .4), highlight);
        resources.Gradient("PrimaryEdgeBrush", Mix(highlight, Colors.White, .55), Mix(highlight, Colors.Black, .25));

        // Dock shares the application's palette, including detached tool windows.
        foreach (var key in new[] { "DockSurfacePanelBrush", "DockSurfaceSidebarBrush", "DockSurfaceWorkbenchBrush", "DockSurfaceHeaderBrush", "DockSurfaceHeaderActiveBrush", "DockTabBackgroundBrush", "DockDocumentTabStripBackgroundBrush", "DockWindowChromeBackgroundBrush", "DockWindowChromeTitleBarBackgroundBrush" }) Set(key, shell);
        foreach (var key in new[] { "DockBorderSubtleBrush", "DockBorderStrongBrush", "DockDocumentContentBorderBrush", "DockWindowChromeBorderBrush", "DockSplitterHoverBrush" }) Set(key, line);
        foreach (var key in new[] { "DockTabForegroundBrush", "DockChromeButtonForegroundBrush", "DockWindowChromeForegroundBrush" }) Set(key, muted);
        Set("DockTabActiveBackgroundBrush", terminal); Set("DockTabHoverBackgroundBrush", panel);
        Set("DockDocumentTabSelectedForegroundBrush", text); Set("DockDocumentTabPointerOverForegroundBrush", text);
        Set("DockApplicationAccentBrushHigh", panel); Set("DockApplicationAccentBrushMed", panel); Set("DockApplicationAccentBrushLow", panel);
        Set("DockApplicationAccentBrushIndicator", accent); Set("DockApplicationAccentForegroundBrush", text);
        Set("DockSurfaceEditorBrush", terminal); Set("DockTabActiveForegroundBrush", text); Set("DockTabActiveIndicatorBrush", accent);
        Set("DockSplitterIdleBrush", "Transparent");
        ApplyGrip(resources, Color.Parse(muted));
        resources.Value("DockDocumentContentBorderThickness", new Thickness(0));
        if (!app.Resources.ContainsKey("DockDocumentControlTabStripVisible")) app.Resources["DockDocumentControlTabStripVisible"] = false;
        resources.Value("DockDocumentTabStripSeparatorVisible", false);
        resources.Value("DockToolChromeHeaderMargin", new Thickness(12, 9, 6, 9));
        resources.Value("DockToolChromeTitleMargin", new Thickness(0));
        resources.Value("DockChromeButtonWidth", 24d);
        resources.Value("DockChromeButtonHeight", 24d);
        resources.Value("DockFontSizeNormal", 12d);
        ThemeMaterials.Apply(resources, worldTheme, images, panel);
        Set("ButtonTextBrush", personal?.Colors["ButtonText"] ?? text);
        Set("PrimaryTextBrush", personal?.Colors["PrimaryText"] ?? "#242424");
        if (personal is not null)
        {
            var overrides = personal.Colors;
            foreach (var key in new[] { "ChromeBrush", "DockHeaderBrush", "DockSurfaceHeaderBrush", "DockSurfaceHeaderActiveBrush", "DockWindowChromeTitleBarBackgroundBrush", "DockWindowChromeBackgroundBrush" }) Set(key, overrides["Chrome"]);
            Set("WorldSelectionBrush", overrides["Selection"]);
            Set("MapCanvasBrush", overrides["MapBackground"]); Set("MapGridBrush", overrides["MapGrid"]);
            Set("EditorBackgroundBrush", overrides["EditorBackground"]); Set("EditorTextBrush", overrides["EditorText"]);
        }
        _lastAppearance = appearance;
        Applied?.Invoke();
    }

    // The grip is a drawing rather than a single brush, so it is rebuilt only when its color moves.
    private static void ApplyGrip(ThemeResources resources, Color color)
    {
        if (_gripColor == color && resources.App.Resources.ContainsKey("DockChromeGripBrush")) return;
        _gripColor = color;
        var grip = new DrawingGroup();
        for (int row = 0; row < 3; row++)
            for (int column = 0; column < 2; column++)
                grip.Children.Add(new GeometryDrawing
                {
                    Brush = new SolidColorBrush(color),
                    Geometry = new EllipseGeometry(new Rect(column * 5, row * 5, 2, 2))
                });
        resources.Brush("DockChromeGripBrush", new DrawingBrush { Drawing = grip, Stretch = Stretch.Uniform });
    }
}
