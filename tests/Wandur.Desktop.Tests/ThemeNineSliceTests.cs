using Avalonia;
using Wandur.Core.Discovery;
using Wandur.Desktop;

namespace Wandur.Desktop.Tests;

public sealed class ThemeNineSliceTests
{
    [Fact]
    public void HighResolutionCornersUseDeclaredDipThickness()
    {
        var patches = ThemeNineSlice.Build(
            new PixelSize(512, 384),
            new SkinPixelBox(48, 128, 48, 80),
            new SkinBox(24, 64, 24, 40),
            new Size(1380, 900));
        Assert.Equal(8, patches.Count);
        Assert.Contains(patches, p =>
            p.SourcePixels == new Rect(0, 0, 48, 128) &&
            p.Destination == new Rect(0, 0, 24, 64));
    }

    [Fact]
    public void MeasuredIndustrialWindowUsesDipThicknessNotSlicePixels()
    {
        var patches = ThemeNineSlice.Build(
            new PixelSize(512, 384),
            new SkinPixelBox(31, 75, 31, 48),
            new SkinBox(16, 38, 16, 24),
            new Size(1380, 900));
        Assert.Equal(8, patches.Count);
        Assert.Contains(patches, p =>
            p.SourcePixels == new Rect(0, 0, 31, 75) &&
            p.Destination == new Rect(0, 0, 16, 38));
        Assert.DoesNotContain(patches, p => p.Destination.Width == 31 && p.Destination.X == 0 && p.Destination.Y == 0);
    }

    [Fact]
    public void OmitsCenterAndRejectsTinyBounds()
    {
        var patches = ThemeNineSlice.Build(
            new PixelSize(100, 100),
            new SkinPixelBox(10, 10, 10, 10),
            new SkinBox(10, 10, 10, 10),
            new Size(15, 15));
        Assert.Empty(patches);

        var ok = ThemeNineSlice.Build(
            new PixelSize(100, 100),
            new SkinPixelBox(10, 10, 10, 10),
            new SkinBox(10, 10, 10, 10),
            new Size(40, 40));
        Assert.Equal(8, ok.Count);
        Assert.DoesNotContain(ok, p =>
            p.SourcePixels.X == 10 && p.SourcePixels.Y == 10 &&
            p.SourcePixels.Width == 80 && p.SourcePixels.Height == 80);
    }

    [Fact]
    public void ZeroSliceMarginsStretchEdgesOnly()
    {
        var patches = ThemeNineSlice.Build(
            new PixelSize(100, 80),
            new SkinPixelBox(0, 0, 0, 0),
            new SkinBox(0, 0, 0, 0),
            new Size(200, 160));
        // All-zero thickness with zero slice: destination mid fills the whole host; corners/edges are zero-area and dropped.
        Assert.Empty(patches);
    }

    [Fact]
    public void FractionalDestinationKeepsEightPatches()
    {
        var patches = ThemeNineSlice.Build(
            new PixelSize(200, 200),
            new SkinPixelBox(20, 20, 20, 20),
            new SkinBox(10.5, 10.5, 10.5, 10.5),
            new Size(300.5, 240.25));
        Assert.Equal(8, patches.Count);
    }

    [Fact]
    public void LegacySizingMatchesSourceSliceWidths()
    {
        // Slice that fits without the whole-bitmap clamp.
        var patches = ThemeNineSlice.BuildLegacy(
            new PixelSize(200, 200),
            new WorldThemeFrameInsets { Left = 40, Top = 50, Right = 40, Bottom = 50 },
            new Size(400, 300));
        Assert.Equal(8, patches.Count);
        Assert.Contains(patches, p =>
            p.SourcePixels == new Rect(0, 0, 40, 50) &&
            p.Destination == new Rect(0, 0, 40, 50));
    }

    [Fact]
    public void MarkerBitmapCornersDrawDistinctColors()
    {
        var patches = ThemeNineSlice.Build(
            new PixelSize(30, 30),
            new SkinPixelBox(10, 10, 10, 10),
            new SkinBox(5, 5, 5, 5),
            new Size(40, 40));
        Assert.Equal(8, patches.Count);
        Assert.Contains(patches, p => p.SourcePixels == new Rect(0, 0, 10, 10) && p.Destination == new Rect(0, 0, 5, 5));
        Assert.Contains(patches, p => p.SourcePixels == new Rect(20, 0, 10, 10) && p.Destination == new Rect(35, 0, 5, 5));
        Assert.Contains(patches, p => p.SourcePixels == new Rect(0, 20, 10, 10) && p.Destination == new Rect(0, 35, 5, 5));
        Assert.Contains(patches, p => p.SourcePixels == new Rect(20, 20, 10, 10) && p.Destination == new Rect(35, 35, 5, 5));
        // Horizontal top edge stretches between corners.
        Assert.Contains(patches, p => p.SourcePixels == new Rect(10, 0, 10, 10) && p.Destination == new Rect(5, 0, 30, 5));
    }
}
