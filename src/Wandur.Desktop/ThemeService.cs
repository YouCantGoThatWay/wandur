using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Wandur.Core.Settings;
using Wandur.Core.Discovery;
using Avalonia.Themes.Fluent;
using Avalonia.Media.Imaging;

namespace Wandur.Desktop;

public static class ThemeService
{
    public static readonly string[] Names = UserTheme.PresetNames.ToArray();
    private static (Application App, string Theme, string? Foreground, string? Background, WorldTheme? World, UserTheme? Personal, UserTheme? AnsiTheme, IReadOnlyDictionary<string, Bitmap>? Images)? _lastAppearance;
    public static void Apply(ClientSettings settings) => Apply(settings, null);
    public static void Apply(ClientSettings settings, WorldTheme? worldTheme, IReadOnlyDictionary<string, Bitmap>? images = null)
    {
        var app = Application.Current!;
        settings.Validate();
        worldTheme = settings.UseWorldThemes && worldTheme is { IsValid: true } ? worldTheme : null;
        var personal = worldTheme is null ? settings.CustomThemes.FirstOrDefault(t => t.Id == settings.Theme) : null;
        if (personal is not null) { worldTheme = personal.ToWorldTheme(); images = null; }
        var ansiTheme = settings.CustomThemes.FirstOrDefault(t => t.Id == settings.Theme);
        var appearance = (app, settings.Theme, settings.Foreground, settings.Background, worldTheme, personal, ansiTheme, images);
        if (_lastAppearance == appearance) return;
        settings.Validate();
        app.Resources["ControlCornerRadius"] = new CornerRadius(worldTheme?.CornerRadius ?? 8);
        app.Resources["CardCornerRadius"] = new CornerRadius(worldTheme?.CornerRadius ?? 12);
        var presetTheme = UserTheme.FromPreset(settings.Theme);
        bool light = worldTheme is null ? presetTheme.IsLight : worldTheme.Variant == "light";
        app.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        var preset = presetTheme.Colors;
        var (shell, panel, terminal, text, muted, accent, line) =
            (preset["Shell"], preset["Panel"], preset["Terminal"], preset["Text"], preset["Muted"], preset["Accent"], preset["Border"]);
        if (worldTheme is { Colors: var colors })
            (shell, panel, terminal, text, muted, accent, line) =
                (colors.Shell, colors.Panel, colors.Terminal, colors.Text, colors.Muted, colors.Accent, colors.Border);
        foreach (var fluent in app.Styles.OfType<FluentTheme>())
        {
            foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
                fluent.Palettes[variant] = worldTheme is null ? new ColorPaletteResources() : new ColorPaletteResources
                {
                    Accent = Color.Parse(accent), RegionColor = Color.Parse(panel),
                    BaseHigh = Color.Parse(text), BaseMedium = Color.Parse(muted),
                    ChromeLow = Color.Parse(shell), ChromeMedium = Color.Parse(panel),
                    ListLow = Color.Parse(panel), ListMedium = Color.Parse(line)
                };
        }
        void Set(string key, string color) => app.Resources[key] = Brush.Parse(color);
        // Presets carry their own sixteen colors; personal themes override them per index.
        for (var i = 0; i < Wandur.Core.Terminal.AnsiPalette.Defaults.Count; i++)
            Set($"AnsiColor{i}Brush", ansiTheme?.AnsiColors.GetValueOrDefault(i)
                ?? (ansiTheme is null ? presetTheme.AnsiColors.GetValueOrDefault(i) : null)
                ?? Wandur.Core.Terminal.AnsiPalette.Defaults[i]);
        Set("ShellBrush", shell); Set("PanelBrush", panel); Set("TextBrush", text);
        Set("TerminalBrush", settings.Background ?? terminal);
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
        LinearGradientBrush Gradient(Color top, Color bottom) => new()
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = [new GradientStop(top, 0), new GradientStop(bottom, 1)]
        };
        app.Resources["WorldSelectionBrush"] = worldTheme is null ? Brush.Parse(line)
            : new SolidColorBrush(Mix(surface, Color.Parse(accent), .16));
        app.Resources["ButtonFaceBrush"] = Gradient(Mix(surface, Colors.White, light ? .8 : .065), surface);
        app.Resources["ButtonHoverBrush"] = Gradient(Mix(surface, Colors.White, light ? 1 : .13), Mix(surface, Colors.White, .04));
        app.Resources["ButtonEdgeBrush"] = Gradient(Mix(surface, Colors.White, light ? .1 : .2), Mix(surface, Colors.Black, .1));
        app.Resources["ToolbarHoverBrush"] = new SolidColorBrush(Mix(surface, light ? Colors.Black : Colors.White, .07));
        app.Resources["ToolbarPressedBrush"] = new SolidColorBrush(Mix(surface, light ? Colors.Black : Colors.White, .13));
        app.Resources["PrimaryFaceBrush"] = Gradient(Mix(highlight, Colors.White, .25), Mix(highlight, Colors.Black, .08));
        app.Resources["PrimaryHoverBrush"] = Gradient(Mix(highlight, Colors.White, .4), highlight);
        app.Resources["PrimaryEdgeBrush"] = Gradient(Mix(highlight, Colors.White, .55), Mix(highlight, Colors.Black, .25));

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
        var grip = new DrawingGroup();
        for (int row = 0; row < 3; row++)
            for (int column = 0; column < 2; column++)
                grip.Children.Add(new GeometryDrawing
                {
                    Brush = Brush.Parse(muted),
                    Geometry = new EllipseGeometry(new Rect(column * 5, row * 5, 2, 2))
                });
        app.Resources["DockChromeGripBrush"] = new DrawingBrush { Drawing = grip, Stretch = Stretch.Uniform };
        app.Resources["DockDocumentContentBorderThickness"] = new Thickness(0);
        if (!app.Resources.ContainsKey("DockDocumentControlTabStripVisible")) app.Resources["DockDocumentControlTabStripVisible"] = false;
        app.Resources["DockDocumentTabStripSeparatorVisible"] = false;
        app.Resources["DockToolChromeHeaderMargin"] = new Thickness(12, 9, 6, 9);
        app.Resources["DockToolChromeTitleMargin"] = new Thickness(0);
        app.Resources["DockChromeButtonWidth"] = 24d;
        app.Resources["DockChromeButtonHeight"] = 24d;
        app.Resources["DockFontSizeNormal"] = 12d;
        ThemeMaterials.Apply(app.Resources, worldTheme, images);
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
    }
}
