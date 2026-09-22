namespace Wandur.Core.Discovery;

/// <summary>Optional modular skin decoration. Missing or malformed skins never invalidate the palette.</summary>
public sealed record WorldThemeSkin
{
    public int Version { get; init; } = 1;
    public WorldThemeWindowSkin? Window { get; init; }
    public WorldThemePanelStyles? Panels { get; init; }
    /// <summary>True when at least one understood section survived sanitizing.</summary>
    public bool HasContent => Window is not null || Panels?.Default is not null;
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

public sealed record WorldThemeSkinBorder
{
    public string Url { get; init; } = "";
    public SkinPixelSize SourceSize { get; init; }
    public SkinPixelBox Slice { get; init; }
    public SkinBox Thickness { get; init; }
}

public sealed record WorldThemeSkinOverlay
{
    public string Id { get; init; } = "";
    public string Url { get; init; } = "";
    public string Anchor { get; init; } = "";
    public SkinSize Size { get; init; }
}

public readonly record struct SkinPixelSize(int Width, int Height);
public readonly record struct SkinPixelBox(int Left, int Top, int Right, int Bottom);
public readonly record struct SkinBox(double Left, double Top, double Right, double Bottom);
public readonly record struct SkinSize(double Width, double Height);
public readonly record struct SkinFooterClearance(double Left, double Right, double MinHeight);
