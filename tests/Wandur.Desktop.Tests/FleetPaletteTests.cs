using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Discovery;
using Wandur.Core.Settings;
using Wandur.Desktop.Terminal;

namespace Wandur.Desktop.Tests;

public sealed class FleetPaletteTests
{
    public static IEnumerable<object[]> Palettes => UserTheme.PresetNames.Select(name => new object[] { name });

    [AvaloniaTheory]
    [MemberData(nameof(Palettes))]
    public async Task ChangingPaletteKeepsTheRecessedFrameAndDockingChrome(string palette)
    {
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), "wandur-fleet-palette-" + Guid.NewGuid(), "settings.json"));
        store.Save(new ClientSettings { Theme = "Hull", UseWorldThemes = false });
        var window = new MainWindow(new TranscriptDisplayFactory(), store, new MemoryPasswordVault(),
            new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            await window.Controller.StartAsync(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var plaque = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PlaqueTitleHost");
            var original = plaque.Bounds;
            window.Sessions.PreviewAppearanceSettings(new ClientSettings { Theme = palette, UseWorldThemes = false });
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Equal(original, plaque.Bounds);
            Assert.Equal("fleet", window.GetVisualDescendants().OfType<ThemePlaque>().Single().Shape);
            Assert.True(window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "FleetDocumentHeader").IsEffectivelyVisible);
            Assert.True(window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "FleetSettings").IsEffectivelyVisible);
            Assert.IsType<DrawingBrush>(window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "MainToolbar").Background);
            Assert.Equal(Color.Parse(UserTheme.FromPreset(palette).Colors["Terminal"]),
                Assert.IsAssignableFrom<ISolidColorBrush>(Application.Current!.Resources["TerminalBrush"]).Color);
            if (Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR") is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
                using var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);
                frame.Save(Path.Combine(directory, $"fleet-palette-{palette}.png"), new PngBitmapEncoderOptions());
            }
        }
        finally { await window.Sessions.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public void PlaquePaintDoesNotRecolorTerminalInstrumentSurfaces()
    {
        var world = UserTheme.FromPreset("Hull").ToWorldTheme() with
        {
            Skin = new() { Layout = new() { TitleBar = new() { Plaque = new() { Fill = "#FFFFFF" } } } }
        };
        ThemeService.Apply(new ClientSettings(), world);
        var instrument = Assert.IsType<LinearGradientBrush>(FleetSkin.Instrument);
        Assert.True(instrument.GradientStops[1].Color.R < 100,
            "A white title plaque must not put terminal-colored light text on white map/channel toolbars.");
        ThemeService.Apply(new ClientSettings());
    }

    [AvaloniaFact]
    public void ChromeTextureChangesTheMaterialNotTheFrame()
    {
        var world = UserTheme.FromPreset("Hull").ToWorldTheme() with
        {
            Images = new() { Chrome = new() { Url = "theme/chrome.png", Opacity = .12 } }
        };
        using var stream = new MemoryStream(TestPng.Rgba(32, 32));
        using var bitmap = new Bitmap(stream);
        ThemeService.Apply(new ClientSettings(), world, new Dictionary<string, Bitmap> { ["chrome"] = bitmap });
        Assert.True(FleetSkin.IsActive);
        Assert.IsType<DrawingBrush>(FleetSkin.Metal);
        Assert.Same(FleetSkin.Metal, FleetSkin.Wings);
        Assert.Same(FleetSkin.Metal, FleetSkin.Toolbar);
        Assert.Equal("fleet", ThemeService.AppliedSkin!.Layout!.TitleBar!.Plaque!.Shape);
        Assert.Equal(50, ThemeService.AppliedSkin.Layout.TitleBar.Height);
        Assert.Null(ThemeSkinResources.FromApplied());
        ThemeService.Apply(new ClientSettings());
    }

    [AvaloniaFact]
    public void PersonalChromeAndAccentColorsChangeMaterialsWithoutDisablingFleet()
    {
        var custom = UserTheme.FromPreset("Hull") with { Name = "Personal hull" };
        custom.Colors["Chrome"] = "#354759";
        custom.Colors["Accent"] = "#DDAA44";
        ThemeService.Apply(new ClientSettings { Theme = "Hull" });
        var before = FleetSkin.Metal;
        ThemeService.Apply(new ClientSettings { Theme = custom.Id, CustomThemes = [custom] });
        Assert.True(FleetSkin.IsActive);
        Assert.Equal("fleet", ThemeService.AppliedSkin!.Layout!.TitleBar!.Plaque!.Shape);
        Assert.NotSame(before, FleetSkin.Metal);
        Assert.Equal(Color.Parse("#DDAA44"), Assert.IsAssignableFrom<ISolidColorBrush>(Application.Current!.Resources["AccentBrush"]).Color);
    }

    [AvaloniaFact]
    public void WorldPaintOverridesCannotReplaceGeometryEvenWithReadyLegacyArtwork()
    {
        var world = UserTheme.FromPreset("Forest").ToWorldTheme() with
        {
            Skin = new WorldThemeSkin
            {
                Layout = new() { TitleBar = new() { Height = 120, HostsToolbar = true,
                    Plaque = new() { Shape = "square", Fill = "#304050", Accent = "#FFCC55" } },
                    PanelHeader = new() { Height = 90, Inset = new(40, 40, 40, 40) } },
                Radii = new() { Panel = 14, Control = 14 },
                Edge = new() { Color = "#225566", Thickness = 1 },
                Window = new() { Border = new() { Url = "theme/frame.png" }, Inset = new(80, 90, 80, 80) },
                Panels = new() { Default = new() { Border = new() { Url = "theme/panel.png" }, HeaderHeight = 90 } }
            }
        };
        using var stream = new MemoryStream(TestPng.Rgba(32, 32));
        using var bitmap = new Bitmap(stream);
        ThemeService.Apply(new ClientSettings(), world, new Dictionary<string, Bitmap>
        { [ThemeSkinResources.WindowBorderKey] = bitmap, [ThemeSkinResources.PanelDefaultKey] = bitmap, ["frame-border"] = bitmap });
        var skin = ThemeService.AppliedSkin!;
        Assert.Equal("fleet", skin.Layout!.TitleBar!.Plaque!.Shape);
        Assert.Equal(50, skin.Layout.TitleBar.Height);
        Assert.False(skin.Layout.TitleBar.HostsToolbar);
        Assert.Equal(38, skin.Layout.PanelHeader!.Height);
        Assert.Equal(2, skin.Radii!.Panel);
        Assert.Equal(3, skin.Radii.Control);
        Assert.Equal(6, skin.Edge!.Thickness);
        Assert.Equal("#225566", skin.Edge.Color);
        Assert.Equal("#304050", skin.Layout.TitleBar.Plaque.Fill);
        Assert.Equal("#FFCC55", skin.Layout.TitleBar.Plaque.Accent);
        Assert.Null(skin.Window);
        Assert.Null(skin.Panels);
        Assert.Null(ThemeSkinResources.FromApplied());
        var bezel = new ThemeBezelHost(); bezel.ApplyFromTheme();
        Assert.Null(bezel.BorderBitmap);
        ThemeService.Apply(new ClientSettings());
    }
}
