using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Discovery;
using Wandur.Core.Settings;
using Wandur.Desktop.Terminal;

namespace Wandur.Desktop.Tests;

/// <summary>
/// The client's own skin: a title band with a nameplate, and the device edge, on every theme without any
/// world having to supply them.
/// </summary>
public sealed class DefaultSkinTests
{
    private static MainWindow Open(string theme)
    {
        var path = Path.Combine(Path.GetTempPath(), "wandur-default-skin-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        var store = new SettingsStore(Path.Combine(path, "settings.json"));
        store.Save(new ClientSettings { Theme = theme });
        var window = new MainWindow(new TranscriptDisplayFactory(), store, new MemoryPasswordVault(),
            new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    /// <summary>
    /// The theme is applied before a window exists to hear about it, so the window has to pick it up when it
    /// opens. It used not to: a freshly launched window showed no band and no edge, and they only appeared
    /// after the first theme change, which is exactly what a person opening the app sees.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("Paper")]
    [InlineData("Ember")]
    public void AFreshlyOpenedWindowHasTheBandTheEdgeAndTheNameplate(string theme)
    {
        var window = Open(theme);
        try
        {
            var host = window.GetVisualDescendants().OfType<ThemeWindowSkinHost>().First();
            Assert.Equal(50, host.BandHeight);
            Assert.Equal(6, host.EdgeThickness);
            Assert.NotNull(host.EdgeOutline);
            Assert.True(host.Child!.Bounds.Top >= host.BandHeight, "content sits under the band instead of below it");

            var plaque = window.GetVisualDescendants().OfType<ThemePlaque>().Single();
            Assert.True(plaque.IsEffectivelyVisible);
            Assert.NotNull(plaque.Fill);
            Assert.True(plaque.WingExtend > 0);
        }
        finally { window.Close(); }
    }

    /// <summary>A world that states a colour palette gets the default's structure painted in its colours.</summary>
    [AvaloniaFact]
    public void TheDefaultTakesItsColoursFromThePalette()
    {
        var light = DefaultSkin.For("#D1D3D1", "#22282B", "#0E181F", "#CBD6E2", "#115C73");
        var dark = DefaultSkin.For("#2A2F36", "#DCE3EA", "#0E181F", "#CBD6E2", "#3FC1DE");
        Assert.NotEqual(light.Surfaces!.TitleBar!.From, dark.Surfaces!.TitleBar!.From);
        // The plate's stripes keep the preset's accent hue but are lifted until they read on the plate.
        foreach (var skin in new[] { light, dark })
        {
            var plaque = skin.Layout!.TitleBar!.Plaque!;
            Assert.True(ContrastProbe.Contrast(Avalonia.Media.Color.Parse(plaque.Accent!), Avalonia.Media.Color.Parse(plaque.Fill!)) >= 3.5,
                $"accent {plaque.Accent} does not read on plate {plaque.Fill}");
        }
    }

    /// <summary>World paint wins while Fleet dimensions and shape remain fixed.</summary>
    [AvaloniaFact]
    public void WorldPaintOverridesPreserveFleetGeometryAndUnspecifiedSurfaces()
    {
        var fallback = DefaultSkin.For("#D1D3D1", "#22282B", "#0E181F", "#CBD6E2", "#115C73");
        var world = new WorldThemeSkin
        {
            Edge = new() { Color = "#FF0000", Outline = "#112233", Accent = "#00FFFF", Thickness = 2 },
            Radii = new() { Panel = 14, Control = 12 },
            Layout = new()
            {
                TitleBar = new()
                {
                    Height = 96, HostsToolbar = true, Padding = new(20, 10, 20, 10),
                    Plaque = new()
                    {
                        Shape = "chamfer", Cap = 12, Padding = new(4, 4, 4, 4),
                        Fill = "#223344", Edge = "#445566", Accent = "#66CCFF",
                        Wings = new() { Extend = 80, Fill = "#778899", Edge = "#334455" },
                    },
                },
                PanelHeader = new() { Height = 64, Inset = new(20, 12, 18, 6) },
            },
            Surfaces = new() { TitleBar = new() { From = "#112233", To = "#445566" } },
        };
        var merged = DefaultSkin.Merge(world, fallback);
        Assert.Equal("#FF0000", merged.Edge!.Color);
        Assert.Equal("#112233", merged.Edge.Outline);
        Assert.Equal("#00FFFF", merged.Edge.Accent);
        Assert.Equal(6, merged.Edge.Thickness);
        Assert.Equal(2, merged.Radii!.Panel);
        Assert.Equal(3, merged.Radii.Control);
        var bar = merged.Layout!.TitleBar!;
        Assert.Equal(50, bar.Height);
        Assert.False(bar.HostsToolbar);
        Assert.Equal(default, bar.Padding);
        Assert.Equal(38, merged.Layout.PanelHeader!.Height);
        Assert.Equal(new SkinBox(8, 0, 8, 0), merged.Layout.PanelHeader.Inset);
        var plaque = bar.Plaque!;
        Assert.Equal("fleet", plaque.Shape);
        Assert.Equal(3, plaque.Cap);
        Assert.Equal(new SkinBox(64, 0, 64, 0), plaque.Padding);
        Assert.Equal("#223344", plaque.Fill);
        Assert.Equal("#445566", plaque.Edge);
        Assert.Equal("#66CCFF", plaque.Accent);
        Assert.Equal(32, plaque.Wings!.Extend);
        Assert.Equal("#778899", plaque.Wings.Fill);
        Assert.Equal("#334455", plaque.Wings.Edge);
        Assert.Same(world.Surfaces.TitleBar, merged.Surfaces!.TitleBar);
        Assert.Equal(fallback.Surfaces! with { TitleBar = world.Surfaces.TitleBar }, merged.Surfaces);
    }
}
