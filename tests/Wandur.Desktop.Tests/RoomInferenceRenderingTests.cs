using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Wandur.Core.Mapping;
using Wandur.Desktop.ViewModels;
using Wandur.Desktop.Views;

namespace Wandur.Desktop.Tests;

public sealed class RoomInferenceRenderingTests
{
    private static MapRoom Room(string id, double x, string? environment, string? inferred) =>
        new(id, id, "", null, x, 0, 0, false) { Environment = environment, InferredEnvironment = inferred, InferredConfidence = inferred is null ? null : 0.87 };

    [Fact]
    public void ResolvePrefersServerTerrainAndFallsBackToInference()
    {
        Assert.Equal("cave", MapEnvironmentPalette.Resolve(Room("a", 0, "cave", "forest")).Key);
        Assert.Equal("forest", MapEnvironmentPalette.Resolve(Room("b", 0, null, "forest")).Key);
        Assert.Equal("city", MapEnvironmentPalette.Resolve(Room("c", 0, null, "urban")).Key);
        Assert.Equal("unknown", MapEnvironmentPalette.Resolve(Room("d", 0, null, null)).Key);
        Assert.Equal("#123456", MapEnvironmentPalette.Resolve(Room("e", 0, null, "forest") with { Color = "#123456" }).Color);
    }

    [Fact]
    public void DescribeMarksInferredTerrainOnly()
    {
        Assert.DoesNotContain("inferred", MapEnvironmentPalette.Describe(Room("a", 0, "cave", "forest")));
        Assert.Contains("87%", MapEnvironmentPalette.Describe(Room("b", 0, null, "forest")));
        Assert.True(MapEnvironmentPalette.UsesInference(Room("b", 0, null, "forest")));
        Assert.False(MapEnvironmentPalette.UsesInference(Room("a", 0, "cave", "forest")));
    }

    [AvaloniaFact]
    public void InferredRoomPaintsPaletteColorWhileServerTerrainWins()
    {
        var tracker = new RoomMapTracker(new MapSnapshot([Room("inferred", -2, null, "forest"), Room("server", 2, "water", "forest")], [], [], null, MapTrackingState.Unknown, RoomDataSource.Gmcp, 0));
        var model = new MapViewModel(tracker); model.Attach(); model.IsGridMode = true;
        var canvas = new RoomMapControl { Model = model };
        var window = new Window { Content = canvas, Width = 500, Height = 400 };
        try
        {
            window.Show(); model.Zoom = 1; Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
            using var stream = new MemoryStream(); frame.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            using var pixels = SkiaSharp.SKBitmap.Decode(stream.ToArray());
            var viewport = model.CreateViewport(canvas.Bounds.Width, canvas.Bounds.Height);
            var forest = viewport.Project(-2, 0); var water = viewport.Project(2, 0);
            var forestPixel = pixels.GetPixel((int)forest.X, (int)forest.Y); var waterPixel = pixels.GetPixel((int)water.X, (int)water.Y);
            Assert.True(forestPixel.Green > forestPixel.Red && forestPixel.Green > forestPixel.Blue, $"forest pixel {forestPixel}");
            Assert.True(waterPixel.Blue > waterPixel.Red && waterPixel.Blue > waterPixel.Green, $"water pixel {waterPixel}");
        }
        finally { window.Close(); model.Detach(); }
    }
}
