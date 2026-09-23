using Avalonia;
using Avalonia.Media;
using Wandur.Core.Discovery;

namespace Wandur.Desktop;

/// <summary>The built-in Hull finish. All surfaces are drawn; no skin assets are loaded.</summary>
internal static class FleetSkin
{
    // The reusable plaque shape alone must not opt a world's palette/layout into the built-in skin.
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

    public static IBrush Metal { get; } = Gradient(
        ("#FAFCFA", 0), ("#E8EAE8", .06), ("#D5D8D6", .36), ("#BEC3C1", .90), ("#A3AAA7", .97), ("#616B69", 1));
    public static IBrush Toolbar { get; } = Gradient(
        ("#F4F5F3", 0), ("#DFE1DF", .05), ("#D5D8D6", .38), ("#C3C7C5", .95), ("#6D7775", 1));
    public static IBrush Instrument { get; } = Gradient(
        ("#48575D", 0), ("#2E3A40", .12), ("#253138", .92), ("#172229", 1));

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
            resources.Brush(key, Metal);
        resources.Brush("ToolbarBrush", Toolbar);
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
