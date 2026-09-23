using Avalonia;
using Avalonia.Media;
using Wandur.Core.Discovery;

namespace Wandur.Desktop;

/// <summary>The shared Fleet chassis and its palette-dependent materials.</summary>
internal static class FleetSkin
{
    // All applied themes retain Fleet geometry, including legacy world themes.
    public static bool IsActive => ThemeService.UsesFleetSkin;

    public static WorldThemeSkin Create() => new()
    {
        Layout = new()
        {
            TitleBar = new()
            {
                Height = FleetTitleLayout.BandHeight, HostsToolbar = false,
                TitleAlign = "center", Padding = new(0, 0, 0, 0),
                Plaque = new()
                {
                    Shape = "fleet", Fill = "#20292D", Edge = "#424743", Accent = "#8DDEE5", Cap = 3,
                    Padding = new(64, 0, 64, 0),
                    Wings = new() { Extend = 32, Fill = "#D1D3D1", Edge = "#454C50" },
                },
            },
            PanelHeader = new() { Height = 38, Inset = new(8, 0, 8, 0) },
        },
        Surfaces = new()
        {
            TitleBar = new() { From = "#E8EAE8", To = "#BCC0BF" },
            Toolbar = new() { From = "#DBDEDD", To = "#B8BEBD" },
            PanelHeader = new() { From = "#E8EAE8", To = "#B8BEBD" },
            PanelBody = new() { From = "#DEE1DF", To = "#CFD3D1" },
            Footer = new() { From = "#DDDED8", To = "#BFC2BD" },
            Ground = new() { From = "#424743", To = "#424743" },
        },
        Radii = new() { Panel = 2, Control = 3 },
        Edge = new() { Color = "#D0D0CA", Outline = "#424743", Thickness = 6, Accent = "#8DDEE5" },
    };

    private static readonly IBrush ReferenceMetal = Gradient(
        ("#FAFCFA", 0), ("#E8EAE8", .06), ("#D5D8D6", .36), ("#BEC3C1", .90), ("#A3AAA7", .97), ("#616B69", 1));
    private static readonly IBrush ReferenceToolbar = Gradient(
        ("#F4F5F3", 0), ("#DFE1DF", .05), ("#D5D8D6", .38), ("#C3C7C5", .95), ("#6D7775", 1));
    private static readonly IBrush ReferenceInstrument = Gradient(
        ("#48575D", 0), ("#2E3A40", .12), ("#253138", .92), ("#172229", 1));

    public static IBrush Metal { get; private set; } = ReferenceMetal;
    public static IBrush DockMetal { get; private set; } = ReferenceMetal;
    public static IBrush Wings { get; private set; } = ReferenceMetal;
    public static IBrush Toolbar { get; private set; } = ReferenceToolbar;
    public static IBrush Instrument { get; private set; } = ReferenceInstrument;
    public static IBrush Plaque { get; private set; } = ReferenceInstrument;
    public static IBrush RimEdge { get; private set; } = Brush.Parse("#596260");
    public static IBrush RimHighlight { get; private set; } = Brush.Parse("#F8FAF7");
    public static IBrush RimShadow { get; private set; } = Brush.Parse("#303B3D");

    public static void SynchronizeMaterials(ThemeResources resources, WorldThemeSkin skin, bool referencePalette)
    {
        Metal = resources.Read("ChromeBrush");
        DockMetal = resources.Read("DockHeaderBrush");
        Toolbar = resources.Read("ToolbarBrush");
        Wings = Metal;
        var plaque = skin.Layout!.TitleBar!.Plaque!;
        Plaque = referencePalette ? ReferenceInstrument : Shade(Color.Parse(plaque.Fill!));
        var panel = ((ISolidColorBrush)resources.Read("PanelBrush")).Color;
        RimEdge = Brush.Parse(skin.Edge!.Outline!);
        RimHighlight = new SolidColorBrush(Mix(panel, Colors.White, .55));
        RimShadow = new SolidColorBrush(Mix(panel, Colors.Black, .72));
        // Icon and control states follow their actual surfaces, including light terminal palettes.
        var terminal = ((ISolidColorBrush)resources.Read("TerminalBrush")).Color;
        var terminalText = ((ISolidColorBrush)resources.Read("TerminalTextBrush")).Color;
        // Plaque paint is independent of work-area chrome, whose text follows the terminal palette.
        Instrument = referencePalette ? ReferenceInstrument : Shade(Mix(terminal, terminalText, .14));
        var accent = ((ISolidColorBrush)resources.Read("AccentBrush")).Color;
        resources.Brush("ToolbarIconBrush", resources.Read("TextBrush"));
        resources.Brush("DockHeaderGlyphBrush", resources.Read("TextBrush"));
        resources.Brush("DockChromeButtonForegroundBrush", resources.Read("TextBrush"));
        resources.Color("FleetInstrumentFaceBrush", Mix(terminal, terminalText, .08));
        resources.Color("FleetInstrumentHoverBrush", Mix(terminal, terminalText, .18));
        resources.Color("FleetInstrumentPressedBrush", Mix(terminal, terminalText, .04));
        resources.Color("FleetInstrumentSelectedBrush", Mix(terminal, accent, .26));
        resources.Color("FleetInstrumentEdgeBrush", Mix(terminal, terminalText, .40));
        resources.Brush("FleetInstrumentAccentBrush", resources.Read("SecondaryAccentBrush"));
        if (referencePalette) resources.Color("FleetInstrumentAccentBrush", "#80D3E1");
    }

    internal static IBrush Shade(Color color) => new LinearGradientBrush
    {
        StartPoint = new(0, 0, RelativeUnit.Relative), EndPoint = new(0, 1, RelativeUnit.Relative),
        GradientStops = [new(Mix(color, Colors.White, .10), 0), new(color, .15), new(Mix(color, Colors.Black, .12), 1)]
    };

    private static Color Mix(Color a, Color b, double amount) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * amount),
        (byte)Math.Round(a.G + (b.G - a.G) * amount),
        (byte)Math.Round(a.B + (b.B - a.B) * amount));

    private static IBrush Gradient(params (string Color, double Offset)[] stops)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new(0, 0, RelativeUnit.Relative), EndPoint = new(0, 1, RelativeUnit.Relative),
        };
        foreach (var (color, offset) in stops) brush.GradientStops.Add(new GradientStop(Color.Parse(color), offset));
        return brush;
    }

    public static void Apply(ThemeResources resources)
    {
        foreach (var key in new[] { "ChromeBrush", "DockHeaderBrush", "DockSurfaceHeaderBrush", "DockSurfaceHeaderActiveBrush",
                     "DockWindowChromeTitleBarBackgroundBrush", "DockDocumentTabStripBackgroundBrush", "FooterBrush" })
            resources.Brush(key, ReferenceMetal);
        resources.Brush("ToolbarBrush", ReferenceToolbar);
        resources.Color("ToolbarIconBrush", "#36464E");
        resources.Color("WorldSelectionBrush", "#A4DBE6");
        resources.Color("DockHeaderGlyphBrush", "#283942");
        resources.Color("DockChromeButtonForegroundBrush", "#283942");
        resources.Color("PrimaryTextBrush", "#F2F7FA");
        resources.Gradient("PrimaryFaceBrush", Color.Parse("#436F89"), Color.Parse("#1B435B"));
        resources.Gradient("PrimaryHoverBrush", Color.Parse("#558BA4"), Color.Parse("#265A73"));
        resources.Color("PrimaryEdgeBrush", "#92ABB7");
    }
}
