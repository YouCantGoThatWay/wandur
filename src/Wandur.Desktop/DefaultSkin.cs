using Avalonia.Media;
using Wandur.Core.Discovery;
using Wandur.Core.Settings;

namespace Wandur.Desktop;

/// <summary>
/// The client's own skin: the title band with its nameplate, the toolbar row, shaded panel headers and
/// the device edge, drawn for every world and every personal theme.
///
/// It carries structure only. Every colour is derived from the palette in force, so the same default is
/// right on a light theme and a dark one, and a world changes how it looks by naming colours rather than
/// by shipping a replacement frame. World skin metadata can override paint per surface, not geometry.
/// </summary>
internal static class DefaultSkin
{
    public static WorldThemeSkinRadii Radii { get; } = new() { Panel = 2, Control = 3 };

    public static WorldThemeSkin For(string panel, string text, string terminal, string terminalText, string accent)
    {
        var p = Color.Parse(panel);
        var t = Color.Parse(text);
        var term = Color.Parse(terminal);
        var termText = Color.Parse(terminalText);
        // Toward the text colour is darker on a light theme and lighter on a dark one, so shading by it is
        // the one direction that reads as depth either way.
        string Toward(double amount) => Hex(Mix(p, t, amount));
        // A raised face is lit away from the text colour: toward white on a light theme and toward black on
        // a dark one. Lifting toward white regardless made the footer lighter than its muted text could read
        // on, on every dark preset.
        var away = UserTheme.IsLightBackground(text) ? Colors.Black : Colors.White;
        string Lit(double amount) => Hex(Mix(p, away, amount));
        var outline = Toward(0.55);

        return new WorldThemeSkin
        {
            Version = 1,
            Layout = new WorldThemeSkinLayout
            {
                TitleBar = new WorldThemeSkinTitleBar
                {
                    // The controls keep their own row beneath the band, as on the design: the band holds the
                    // traffic lights and the nameplate and nothing else.
                    HostsToolbar = false, ToolbarAlign = "right", TitleAlign = "center", Height = 50,
                    Padding = default,
                    Plaque = new WorldThemeSkinPlaque
                    {
                        Shape = "fleet", Cap = 3,
                        Fill = Hex(Mix(term, termText, 0.14)), Edge = Hex(term),
                        Accent = Hex(Readable(Color.Parse(accent), Mix(term, termText, 0.14))),
                        Padding = new SkinBox(64, 0, 64, 0),
                        Wings = new WorldThemeSkinWings { Extend = 32, Fill = Lit(0.10), Edge = outline },
                        Shadow = new WorldThemeSkinShadow { Color = "#000000", Opacity = 0.30, Blur = 4, Y = 2 },
                    },
                },
                PanelHeader = new WorldThemeSkinPanelHeader { Height = 38, Inset = new SkinBox(8, 0, 8, 0) },
            },
            Surfaces = new WorldThemeSkinSurfaces
            {
                TitleBar = new() { From = Lit(0.12), To = Toward(0.04), Bevel = "raised", BevelStrength = 0.30 },
                Toolbar = new() { From = Toward(0.06), To = Toward(0.12), Bevel = "raised", BevelStrength = 0.25 },
                // Held close to the panel colour: the header's buttons draw in the muted colour, which only just
                // clears contrast on a flat panel on several presets, so the header can barely move before they
                // stop being readable. The bevel carries the definition instead.
                // Darker than the panel, as on the design: the header glyphs switch to the text colour when a
                // header surface is present, which is what makes that legible.
                PanelHeader = new() { From = Toward(0.08), To = Toward(0.15), Bevel = "raised", BevelStrength = 0.30 },
                PanelBody = new() { From = panel, To = Toward(0.02) },
                Footer = new() { From = Lit(0.08), To = Toward(0.05), Bevel = "raised", BevelStrength = 0.30 },
                // The dark chassis the panels rest on. Flat: it is a gap, not a face.
                Ground = new() { From = Toward(0.66), To = Toward(0.66) },
            },
            Edge = new WorldThemeSkinEdge { Color = panel, Outline = outline, Thickness = 6,
                Accent = Hex(Readable(Color.Parse(accent), p)) },
            Radii = Radii,
        };
    }

    /// <summary>
    /// Merge world paint into the fixed chassis. Textured chrome keeps its material unless a world
    /// explicitly overrides that surface. Legacy bitmap frames and layout dimensions are ignored.
    /// </summary>
    public static WorldThemeSkin Merge(WorldThemeSkin? world, WorldThemeSkin fallback, bool keepSurfaces = false)
    {
        var surfaces = keepSurfaces ? null : fallback.Surfaces;
        var paint = world?.Surfaces;
        var plate = fallback.Layout!.TitleBar!.Plaque!;
        var platePaint = world?.Layout?.TitleBar?.Plaque;
        return fallback with
        {
            // Themes paint the shared chassis. Legacy shapes, dimensions, bitmaps and radii
            // remain readable metadata, but cannot replace the client's geometry.
            Layout = fallback.Layout with { TitleBar = fallback.Layout.TitleBar with
            {
                Plaque = plate with
                {
                    Fill = platePaint?.Fill ?? plate.Fill, Edge = platePaint?.Edge ?? plate.Edge,
                    Accent = platePaint?.Accent ?? plate.Accent,
                    Wings = plate.Wings! with { Fill = platePaint?.Wings?.Fill ?? plate.Wings.Fill,
                        Edge = platePaint?.Wings?.Edge ?? plate.Wings.Edge }
                }
            } },
            Surfaces = new()
            {
                TitleBar = paint?.TitleBar ?? surfaces?.TitleBar,
                Toolbar = paint?.Toolbar ?? surfaces?.Toolbar,
                PanelHeader = paint?.PanelHeader ?? surfaces?.PanelHeader,
                PanelBody = paint?.PanelBody ?? surfaces?.PanelBody,
                Footer = paint?.Footer ?? surfaces?.Footer,
                Ground = paint?.Ground ?? surfaces?.Ground
            },
            Edge = fallback.Edge! with { Color = world?.Edge?.Color ?? fallback.Edge.Color,
                Outline = world?.Edge?.Outline ?? fallback.Edge.Outline,
                Accent = world?.Edge?.Accent ?? fallback.Edge.Accent },
        };
    }

    /// <summary>
    /// The accent lifted until it reads on the plate. A preset's accent is chosen to read on its panels, and
    /// on a dark plate a dark accent vanishes; the stripes need to be seen, not matched.
    /// </summary>
    private static Color Readable(Color accent, Color plate)
    {
        var lifted = accent;
        for (var i = 0; i < 12 && Contrast(lifted, plate) < 3.5; i++)
            lifted = Mix(lifted, Colors.White, 0.18);
        return lifted;
    }

    private static double Contrast(Color a, Color b)
    {
        static double Lum(Color c)
        {
            static double Ch(byte v) { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
        }
        var (la, lb) = (Lum(a), Lum(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
