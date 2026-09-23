using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
            Assert.True(host.BandHeight > 0, "the title band was not applied on open");
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

    /// <summary>A world's own sections win; what it leaves out comes from the default.</summary>
    [AvaloniaFact]
    public void AWorldSkinWinsSectionBySection()
    {
        var fallback = DefaultSkin.For("#D1D3D1", "#22282B", "#0E181F", "#CBD6E2", "#115C73");
        var world = new Wandur.Core.Discovery.WorldThemeSkin
        {
            Edge = new Wandur.Core.Discovery.WorldThemeSkinEdge { Color = "#FF0000", Thickness = 2 },
        };
        var merged = DefaultSkin.Merge(world, fallback);
        Assert.Equal("#FF0000", merged.Edge!.Color);
        Assert.Same(fallback.Layout, merged.Layout);
        Assert.Same(fallback.Surfaces, merged.Surfaces);
    }
}
