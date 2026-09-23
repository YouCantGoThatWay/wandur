using Avalonia.Media;
using Wandur.Core.Discovery;

namespace Wandur.Desktop;

/// <summary>
/// Paints the skin's shaded surfaces: the docking chrome of the desktop component suites this look comes
/// from. A vertical gradient, a brighter band over the top half with a hard step at the midline, and a
/// hairline light edge above a hairline dark edge. It is deliberately not art. A gradient stays crisp at
/// any panel size and any display scale, costs no download, and cannot be stretched into mush by a slice
/// that does not match the picture, which is every failure mode the nine-slice frames had.
/// </summary>
internal static class ThemeSkinSurfaces
{
    /// <summary>Hairline edges as a fraction of the surface height. At a 28 DIP header this is about a pixel.</summary>
    private const double EdgeStop = 0.035;
    /// <summary>Accent band depth as a fraction: about a pixel under a 28 DIP header, three under a title bar.</summary>
    private const double RuleStop = 0.04;

    public static void Apply(WorldThemeSkin? skin, ThemeResources resources)
    {
        if (skin?.Surfaces is not { HasContent: true } surfaces)
        {
            Wandur.Core.Diagnostics.ThemeTrace.Write("surfaces",
                $"none applied (skin={(skin is null ? "null" : "present")}, surfaces={(skin?.Surfaces is null ? "null" : "empty")})");
            return;
        }
        Wandur.Core.Diagnostics.ThemeTrace.Write("surfaces",
            $"titlebar={(surfaces.TitleBar is null ? "-" : surfaces.TitleBar.From + ">" + surfaces.TitleBar.To)} " +
            $"panel_header={(surfaces.PanelHeader is null ? "-" : surfaces.PanelHeader.From + ">" + surfaces.PanelHeader.To)} " +
            $"panel_body={(surfaces.PanelBody is null ? "-" : "set")} footer={(surfaces.Footer is null ? "-" : "set")}");

        if (Build(surfaces.PanelHeader) is { } header)
        {
            foreach (var key in new[] { "DockHeaderBrush", "DockSurfaceHeaderBrush", "DockSurfaceHeaderActiveBrush" })
                resources.Brush(key, header);
            // A shaded header is usually darker than the panel, and the muted glyph colour that only just
            // reads on a flat panel does not read on it. The glyphs take the text colour instead.
            resources.Brush("DockHeaderGlyphBrush", resources.Read("TextBrush"));
        }

        // The ground goes on the dock's own surfaces, never on the window shell: the shell's lightness is
        // what decides the light or dark variant, and a dark chassis under light panels must not flip it.
        if (Build(surfaces.Ground) is { } ground)
            foreach (var key in new[] { "DockSurfaceWorkbenchBrush", "DockWindowChromeBackgroundBrush" })
                resources.Brush(key, ground);

        if (Build(surfaces.Toolbar) is { } toolbar)
            resources.Brush("ToolbarBrush", toolbar);

        if (Build(surfaces.TitleBar) is { } titleBar)
            foreach (var key in new[] { "ChromeBrush", "DockWindowChromeTitleBarBackgroundBrush", "DockDocumentTabStripBackgroundBrush" })
                resources.Brush(key, titleBar);

        if (Build(surfaces.PanelBody) is { } body)
            foreach (var key in new[] { "DockSurfacePanelBrush", "DockSurfaceSidebarBrush" })
                resources.Brush(key, body);

        if (Build(surfaces.Footer) is { } footer)
            resources.Brush("FooterBrush", footer);
    }

    /// <summary>Null for an absent surface, so a slot the theme left out keeps whatever the client painted.</summary>
    public static IBrush? Build(WorldThemeSkinSurface? surface)
    {
        if (surface is null) return null;
        if (!Color.TryParse(surface.From, out var from) || !Color.TryParse(surface.To, out var to)) return null;

        var stops = new GradientStops();
        var raised = surface.Bevel == "raised";
        var sunken = surface.Bevel == "sunken";

        // The bevel is the first and last stop rather than a border, so it belongs to the brush and needs
        // no cooperation from whatever control the brush lands on.
        var strength = Math.Clamp(surface.BevelStrength, 0, 1);
        if (raised) stops.Add(new GradientStop(Shift(from, 0.55 * strength), 0));
        else if (sunken) stops.Add(new GradientStop(Shift(to, -0.35 * strength), 0));

        stops.Add(new GradientStop(from, raised || sunken ? EdgeStop : 0));
        if (surface.Gloss > 0)
        {
            // The hard step at the midline is what reads as machined metal rather than a soft fade.
            stops.Add(new GradientStop(Mix(from, to, 0.20 * (1 - surface.Gloss) + 0.20), 0.499));
            stops.Add(new GradientStop(Mix(from, to, 0.45 + 0.25 * surface.Gloss), 0.5));
        }
        stops.Add(new GradientStop(to, raised || sunken ? 1 - EdgeStop : 1));

        if (raised) stops.Add(new GradientStop(Shift(to, -0.30 * strength), 1));
        else if (sunken) stops.Add(new GradientStop(Shift(from, 0.45 * strength), 1));

        if (surface.Grain > 0) AddGrain(stops, surface.Grain);

        // The accent band replaces the bottom of the surface rather than sitting under it, so it cannot
        // add height to a header the layout already measured.
        if (surface.Rule is { Length: > 0 } ruleColor && Color.TryParse(ruleColor, out var rule))
        {
            foreach (var stop in stops.Where(s => s.Offset > 1 - RuleStop).ToList()) stops.Remove(stop);
            stops.Add(new GradientStop(stops[^1].Color, 1 - RuleStop));
            stops.Add(new GradientStop(rule, 1 - RuleStop));
            stops.Add(new GradientStop(rule, 1));
        }

        return new LinearGradientBrush
        {
            StartPoint = new(0, 0, Avalonia.RelativeUnit.Relative),
            EndPoint = new(0, 1, Avalonia.RelativeUnit.Relative),
            GradientStops = stops,
        };
    }

    /// <summary>How many streaks a brushed surface carries. Enough to read as metal on a title bar,
    /// few enough that a 28 DIP header does not turn into a moire.</summary>
    private const int GrainLines = 34;

    /// <summary>
    /// Lays brushed streaks over whatever shading is already there, by sampling the gradient at each
    /// streak and nudging it. The jitter is a fixed sequence rather than random so a surface looks the
    /// same every time it is painted; a texture that shimmered between repaints would be worse than none.
    /// </summary>
    private static void AddGrain(GradientStops stops, double grain)
    {
        var existing = stops.OrderBy(stop => stop.Offset).ToList();
        var amount = 0.05 * Math.Clamp(grain, 0, 1);
        // A small irrational step walks the unit interval without ever repeating a value, which gives an
        // even scatter of light and dark streaks with no visible period.
        var walk = 0.0;
        for (var i = 1; i < GrainLines; i++)
        {
            var offset = (double)i / GrainLines;
            walk = (walk + 0.6180339887498949) % 1.0;
            var nudge = (walk - 0.5) * 2 * amount;
            stops.Add(new GradientStop(Shift(SampleAt(existing, offset), nudge), offset));
        }
    }

    /// <summary>The colour the shading already had at an offset, so grain adds to it rather than flattening it.</summary>
    private static Color SampleAt(IReadOnlyList<GradientStop> stops, double offset)
    {
        if (stops.Count == 0) return Colors.Transparent;
        if (offset <= stops[0].Offset) return stops[0].Color;
        for (var i = 0; i < stops.Count - 1; i++)
        {
            var (a, b) = (stops[i], stops[i + 1]);
            if (offset < a.Offset || offset > b.Offset) continue;
            var span = b.Offset - a.Offset;
            return span <= 0 ? b.Color : Mix(a.Color, b.Color, (offset - a.Offset) / span);
        }
        return stops[^1].Color;
    }

    /// <summary>Toward white for a positive amount, toward black for a negative one.</summary>
    private static Color Shift(Color color, double amount)
    {
        var target = amount >= 0 ? (byte)255 : (byte)0;
        var weight = Math.Abs(amount);
        return Color.FromArgb(color.A,
            (byte)Math.Round(color.R + (target - color.R) * weight),
            (byte)Math.Round(color.G + (target - color.G) * weight),
            (byte)Math.Round(color.B + (target - color.B) * weight));
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (byte)Math.Round(a.A + (b.A - a.A) * t),
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));
}
