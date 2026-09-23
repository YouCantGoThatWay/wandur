using Avalonia;
using Wandur.Core.Discovery;

namespace Wandur.Desktop.Tests;

/// <summary>
/// A stretched rail smears anything detailed across the whole width, which is why a stretch-mode skin
/// has to keep its rails plain. Tile mode repeats the middle at its own scale instead, so a rail can
/// carry a strip of detail and still fit any window.
/// </summary>
public sealed class ThemeNineSliceTileTests
{
    private static readonly PixelSize Source = new(200, 100);
    private static readonly SkinPixelBox Slice = new(40, 30, 40, 30);
    private static readonly SkinBox Thickness = new(40, 30, 40, 30);

    [Fact]
    public void StretchDrawsOnePatchPerEdge()
    {
        var patches = ThemeNineSlice.Build(Source, Slice, Thickness, new Size(1000, 600));
        Assert.Equal(8, patches.Count);
        Assert.All(patches, p => Assert.False(p.TileX || p.TileY));
    }

    [Fact]
    public void TileMarksOnlyTheRailMiddles()
    {
        var patches = ThemeNineSlice.Build(Source, Slice, Thickness, new Size(1000, 600), tile: true);
        Assert.Equal(8, patches.Count);
        // Four corners stay fixed art; the four rail middles repeat along their own axis.
        Assert.Equal(4, patches.Count(p => !p.TileX && !p.TileY));
        Assert.Equal(2, patches.Count(p => p.TileX));
        Assert.Equal(2, patches.Count(p => p.TileY));
        Assert.DoesNotContain(patches, p => p.TileX && p.TileY);
    }

    [Fact]
    public void ARailWiderThanItsArtStillRepeatsAtTheArtsOwnScale()
    {
        // 120 source pixels of top rail middle, drawn 30 DIP tall from 30 source pixels: scale 1, so a
        // 920 DIP rail is eight repeats, not one image stretched nearly eight times over.
        var patches = ThemeNineSlice.Build(Source, Slice, Thickness, new Size(1000, 600), tile: true);
        var topMiddle = patches.Single(p => p.TileX && p.Destination.Y == 0);
        Assert.Equal(120, topMiddle.SourcePixels.Width);
        Assert.Equal(920, topMiddle.Destination.Width);
        var scale = topMiddle.Destination.Height / topMiddle.SourcePixels.Height;
        Assert.Equal(1, scale);
        Assert.True(topMiddle.Destination.Width / (topMiddle.SourcePixels.Width * scale) > 7,
            "a rail this wide should repeat many times rather than stretch once");
    }

    [Fact]
    public void TileIsOptInSoExistingSkinsAreUnaffected()
    {
        var border = new WorldThemeSkinBorder { Url = "themes/x/y.png", SourceSize = new SkinPixelSize(200, 100) };
        Assert.Equal("stretch", border.Repeat);
        Assert.False(border.Tiles);
        Assert.True(border with { Repeat = "tile" } is { Tiles: true });
    }
}
