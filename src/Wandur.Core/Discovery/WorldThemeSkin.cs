namespace Wandur.Core.Discovery;

/// <summary>Optional modular skin decoration. Missing or malformed skins never invalidate the palette.</summary>
public sealed record WorldThemeSkin
{
    public int Version { get; init; } = 1;
    public WorldThemeWindowSkin? Window { get; init; }
    public WorldThemePanelStyles? Panels { get; init; }
    public WorldThemeSkinLayout? Layout { get; init; }
    public WorldThemeSkinSurfaces? Surfaces { get; init; }
    public WorldThemeSkinRadii? Radii { get; init; }
    public WorldThemeSkinEdge? Edge { get; init; }
    /// <summary>True when at least one understood section survived sanitizing.</summary>
    public bool HasContent =>
        Window is not null || Panels?.Default is not null || Layout is not null ||
        Surfaces is { HasContent: true } || Radii is { HasContent: true } || Edge is not null;
}

public sealed record WorldThemeWindowSkin
{
    public WorldThemeSkinBorder Border { get; init; } = new();
    public SkinBox Inset { get; init; }
    public IReadOnlyList<WorldThemeSkinOverlay> Overlays { get; init; } = Array.Empty<WorldThemeSkinOverlay>();
    public SkinFooterClearance FooterClearance { get; init; }
    public SkinSize? CompactBelow { get; init; }

    public bool Equals(WorldThemeWindowSkin? other) =>
        other is not null &&
        Border == other.Border &&
        Inset == other.Inset &&
        FooterClearance == other.FooterClearance &&
        CompactBelow == other.CompactBelow &&
        Overlays.SequenceEqual(other.Overlays);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Border);
        hash.Add(Inset);
        hash.Add(FooterClearance);
        hash.Add(CompactBelow);
        foreach (var overlay in Overlays) hash.Add(overlay);
        return hash.ToHashCode();
    }
}

/// <summary>
/// Where the client's own chrome sits inside the painted frame. The set of slots is fixed by the
/// client and a theme only moves and sizes them: a skin can say the toolbar belongs in the metal
/// band, but it cannot invent regions or bind arbitrary content into them.
/// </summary>
public sealed record WorldThemeSkinLayout
{
    public WorldThemeSkinTitleBar? TitleBar { get; init; }
    public WorldThemeSkinPanelHeader? PanelHeader { get; init; }
}

/// <summary>
/// A dock panel's header row. The panel skin insets the whole panel so its body clears the painted
/// border, but that also pushes the header below the art's own header band and squeezes its title.
/// This gives the row its own height and its own inset from the panel's outer edge.
/// </summary>
public sealed record WorldThemeSkinPanelHeader
{
    public double Height { get; init; }
    public SkinBox Inset { get; init; }
}

/// <summary>
/// The hairline the window is framed by, drawn rather than supplied as artwork. It runs down the left and
/// right edges and along the bottom; the title bar caps the top, so there is nothing to draw there. One
/// colour and a thickness is the whole of it, which is why it does not need a nine-slice, an asset, or
/// any of the scaling that made a picture of a one pixel line expensive.
/// </summary>
public sealed record WorldThemeSkinEdge
{
    public string Color { get; init; } = "";
    /// <summary>Line width in DIP. Bounded, because this frames the window rather than decorating it.</summary>
    public double Thickness { get; init; } = 1;
    /// <summary>
    /// When set, the edge is a small bevelled frame rather than a line: a band of <see cref="Color"/> held
    /// between two dark outline strokes, with a highlight just inside the outer one. That is the device edge
    /// of a panel seen straight on, and it is still three colours and a width, not a picture.
    /// </summary>
    public string? Outline { get; init; }
    /// <summary>Optional six-digit hex colour for an accent rail along the frame.</summary>
    public string? Accent { get; init; }
}

/// <summary>
/// The nameplate the title sits on. Its shape belongs to the client and its colours to the theme, which
/// is the split that lets a world be configured on a form: nothing here is a file to host or an image to
/// slice, so a world picks a shape and names three colours and is finished.
/// </summary>
/// <summary>A soft drop under a drawn shape. Opacity is separate from the colour because a theme's
/// colours are six-digit hex everywhere else, and one exception would be a trap on an owner's form.</summary>
public sealed record WorldThemeSkinShadow
{
    public string Color { get; init; } = "";
    public double Opacity { get; init; } = 0.35;
    public double Blur { get; init; } = 8;
    /// <summary>Vertical offset. Positive drops the shadow below the shape.</summary>
    public double Y { get; init; } = 2;
}

/// <summary>
/// The bracket behind the plate, reaching past it on both sides. It is what makes a nameplate read as
/// mounted on the bar rather than floating on it, and it is a second shape rather than a wider plate so
/// the two can differ in colour and depth.
/// </summary>
public sealed record WorldThemeSkinWings
{
    /// <summary>How far past the plate the bracket reaches on each side, in DIP.</summary>
    public double Extend { get; init; }
    public string? Fill { get; init; }
    /// <summary>The dark line around the bracket that separates it from the bar it sits on.</summary>
    public string? Edge { get; init; }
}

public sealed record WorldThemeSkinPlaque
{
    /// <summary>"chamfer", "notch", "round", "square" or "fleet".</summary>
    public string Shape { get; init; } = "chamfer";
    /// <summary>Width of the accent stripe inside each end, in DIP. Zero draws none.</summary>
    public double Cap { get; init; } = 6;
    /// <summary>Plate colour; null takes the chrome the palette already gives the title bar.</summary>
    public string? Fill { get; init; }
    /// <summary>Outline colour; null draws no outline.</summary>
    public string? Edge { get; init; }
    /// <summary>Stripe colour; null takes the palette's accent.</summary>
    public string? Accent { get; init; }
    /// <summary>Room inside the plate around the title, so the text clears the stripes.</summary>
    public SkinBox Padding { get; init; }
    public WorldThemeSkinShadow? Shadow { get; init; }
    public WorldThemeSkinWings? Wings { get; init; }
}

public sealed record WorldThemeSkinTitleBar
{
    /// <summary>
    /// How deep the band is, in DIP, or null to leave the client its own. A theme that draws a nameplate
    /// usually wants more room than the default bar allows.
    /// </summary>
    public double? Height { get; init; }
    /// <summary>The nameplate, or null to draw the title straight onto the band.</summary>
    public WorldThemeSkinPlaque? Plaque { get; init; }

    /// <summary>
    /// The toolbar row moves into the painted top band instead of stacking beneath it. Without this
    /// the frame paints a metal band and the app draws its own bar under it, which is the doubled
    /// chrome that makes a skin look pasted on.
    /// </summary>
    public bool HostsToolbar { get; init; }
    /// <summary>Which end of the band the toolbar sits at: left, center or right.</summary>
    public string ToolbarAlign { get; init; } = "right";
    /// <summary>
    /// Where the window title sits on the band: left, center or right. A skin whose art already carries
    /// a nameplate puts the title in that overlay instead; a skin with a clean band, which is the usual
    /// case, simply draws the title on it and needs no overlay at all.
    /// </summary>
    public string TitleAlign { get; init; } = "center";
    /// <summary>Clearance inside the band: traffic lights on the left, the plaque in the middle.</summary>
    public SkinBox Padding { get; init; }
}

public sealed record WorldThemePanelStyles
{
    public WorldThemePanelSkin? Default { get; init; }
}

public sealed record WorldThemePanelSkin
{
    public WorldThemeSkinBorder Border { get; init; } = new();
    public SkinBox Inset { get; init; }
    public double HeaderHeight { get; init; }
}

/// <summary>
/// A shaded surface: the docking chrome vocabulary of the desktop suites this look comes from, which was
/// never photographic. A vertical gradient, a lighter band across the top half, and a hairline bevel is
/// the whole language, and unlike nine-slice art it stays crisp at any size and cannot be squashed by a
/// bad slice. Four values describe a panel header that reads as brushed metal.
/// </summary>
public sealed record WorldThemeSkinSurface
{
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    /// <summary>How pronounced the top-half highlight is, 0 for a plain gradient.</summary>
    public double Gloss { get; init; }
    /// <summary>"raised", "sunken" or "none": which way the hairline edges run.</summary>
    public string Bevel { get; init; } = "none";
    /// <summary>
    /// How hard the bevel reads, 0 to 1, defaulting to full. A bevel is the difference between metal and a
    /// gradient, and also the difference between machined and plastic, so how far it goes has to be the
    /// theme's call rather than one baked figure.
    /// </summary>
    public double BevelStrength { get; init; } = 1;
    /// <summary>
    /// Brushed grain across the surface, 0 for none. Read vertically, brushed metal is horizontal streaks,
    /// and a vertical gradient with many stops is horizontal streaks, so the grain lives in the gradient
    /// rather than in a tiled bitmap: it stays exactly one pixel per streak at any panel size and any
    /// display scale, downloads nothing, and has no seam to hide. A bitmap has to be stretched to whatever
    /// the surface turned out to be, which is what smears a fine grain into a wash.
    /// </summary>
    public double Grain { get; init; }
    /// <summary>
    /// An accent band along the bottom edge, or null for none. It belongs to the surface rather than to a
    /// border so it needs no cooperation from the control the brush lands on, and it scales with the
    /// surface: a thin line under a panel header, a slightly heavier one under the deeper titlebar.
    /// </summary>
    public string? Rule { get; init; }
}

/// <summary>
/// How hard the client's corners are. A theme that paints flat military chrome wants square panels and a
/// theme that paints a consumer shell wants soft ones, and that is a property of the theme rather than of
/// the client, so it is stated here rather than assumed.
/// </summary>
public sealed record WorldThemeSkinRadii
{
    /// <summary>Docked panels and cards. 0 is square.</summary>
    public double? Panel { get; init; }
    /// <summary>Buttons, fields and other controls.</summary>
    public double? Control { get; init; }
    public bool HasContent => Panel is not null || Control is not null;
}

/// <summary>Shaded surfaces by slot. Any slot may be absent, leaving the client's own painting alone.</summary>
public sealed record WorldThemeSkinSurfaces
{
    public WorldThemeSkinSurface? TitleBar { get; init; }
    /// <summary>The row of controls beneath the title band, which a theme may shade apart from it.</summary>
    public WorldThemeSkinSurface? Toolbar { get; init; }
    public WorldThemeSkinSurface? PanelHeader { get; init; }
    public WorldThemeSkinSurface? PanelBody { get; init; }
    public WorldThemeSkinSurface? Footer { get; init; }
    /// <summary>The chassis the panels sit on: the gutters between them and the space behind them.</summary>
    public WorldThemeSkinSurface? Ground { get; init; }
    public bool HasContent => TitleBar is not null || Toolbar is not null || PanelHeader is not null
        || PanelBody is not null || Footer is not null || Ground is not null;
}

public sealed record WorldThemeSkinBorder
{
    public string Url { get; init; } = "";
    /// <summary>
    /// What the directory says this asset currently is, moving whenever its bytes do. The client caches art
    /// under url plus version, so replacing a file at a stable path reaches every client without anyone
    /// remembering to rename it. Empty when the directory does not report one, which caches by url alone.
    /// </summary>
    public string? Version { get; init; }
    public SkinPixelSize SourceSize { get; init; }
    public SkinPixelBox Slice { get; init; }
    public SkinBox Thickness { get; init; }
    /// <summary>
    /// What happens to the middle of each rail when the frame is bigger than its art. "stretch" is the
    /// default and smears anything detailed, which is why a stretched skin has to keep its rails plain.
    /// "tile" repeats the middle at its own scale instead, so a rail can carry a seamless strip of
    /// detail and still fit any window.
    /// </summary>
    public string Repeat { get; init; } = "stretch";
    public bool Tiles => Repeat == "tile";
}

public sealed record WorldThemeSkinOverlay
{
    public string Id { get; init; } = "";
    public string Url { get; init; } = "";
    /// <summary>
    /// What the directory says this asset currently is, moving whenever its bytes do. The client caches art
    /// under url plus version, so replacing a file at a stable path reaches every client without anyone
    /// remembering to rename it. Empty when the directory does not report one, which caches by url alone.
    /// </summary>
    public string? Version { get; init; }
    public string Anchor { get; init; } = "";
    public SkinSize Size { get; init; }
    /// <summary>
    /// The readable window inside the overlay art, as fractions of the overlay box. A plaque's metal
    /// shoulders are part of its bitmap, so a title sized to <see cref="Size"/> spills onto them and
    /// stops being readable; this is the region a title actually fits. Fractions rather than pixels
    /// because an overlay is drawn at whatever size the skin asks for, not at its source size.
    /// Null means the art has no inner window and the whole overlay is fair game.
    /// </summary>
    public SkinRect? TextArea { get; init; }
}

public readonly record struct SkinPixelSize(int Width, int Height);
public readonly record struct SkinPixelBox(int Left, int Top, int Right, int Bottom);
public readonly record struct SkinBox(double Left, double Top, double Right, double Bottom);
public readonly record struct SkinSize(double Width, double Height);
public readonly record struct SkinRect(double X, double Y, double Width, double Height);
public readonly record struct SkinFooterClearance(double Left, double Right, double MinHeight);
