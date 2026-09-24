using System.Net;
using System.Net.Sockets;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Mapping;
using Wandur.Core.Settings;
using Wandur.Desktop.Terminal;

namespace Wandur.Desktop.Tests;

public sealed class FleetReferenceCaptureTests
{
    [AvaloniaTheory]
    [InlineData(1380, 900, "Legends of the Jedi")]
    [InlineData(1040, 680, "Legends of the Jedi")]
    [InlineData(1536, 1024, "Legends of the Jedi")]
    [InlineData(1040, 680, "Étoiles du Nord: Legends of the Outer Reaches and the Very Distant Stars")]
    public async Task LiveSessionHasACenteredTitleAndDarkWorkAreaWithoutARedundantHeading(int width, int height, string worldName)
    {
        var path = Path.Combine(Path.GetTempPath(), "wandur-fleet-reference-" + Guid.NewGuid());
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var profile = new ConnectionProfile { Name = worldName, Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port };
        var store = new SettingsStore(Path.Combine(path, "settings.json"));
        store.Save(new ClientSettings { Theme = "Hull", UseWorldThemes = false, Profiles = [profile] });
        var window = new MainWindow(new TranscriptDisplayFactory(), store, new MemoryPasswordVault(),
            new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore()) { Width = width, Height = height };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var opening = window.Sessions.OpenAsync(profile);
            using var peer = await listener.AcceptTcpClientAsync();
            await opening;
            window.Controller.ClearTranscript();
            window.Controller.Terminal.Append("Welcome to Legends of the Jedi.\r\nType 'help' for assistance.\r\n\r\n\x1b[36mDOCKING CONTROL\r\n---------------\x1b[0m\r\n\x1b[33mDocking Control\x1b[0m\r\n\r\nThe observation deck overlooks a quiet field of stars.\r\nAmber markers guide a freighter toward the lower berths.\r\n\r\nExits: \x1b[32mnorth\x1b[0m, \x1b[32meast\x1b[0m, \x1b[32mdown\x1b[0m\r\n\r\n> look\r\n");
            var map = window.Controller.Map;
            map.Observe(new RoomObservation("dock", "Docking Control", "", new Dictionary<string,string?> { ["north"] = "observation", ["east"] = "cargo", ["west"] = "command" }, Source: RoomDataSource.Gmcp));
            map.Observe(new RoomObservation("observation", "Observation Deck", "", new Dictionary<string,string?> { ["south"] = "dock" }, Source: RoomDataSource.Gmcp), "north");
            map.Observe(new RoomObservation("dock", "Docking Control", "", new Dictionary<string,string?> { ["north"] = "observation", ["east"] = "cargo", ["west"] = "command" }, Source: RoomDataSource.Gmcp), "south");
            map.Observe(new RoomObservation("cargo", "Cargo Berths", "", new Dictionary<string,string?> { ["west"] = "dock" }, Source: RoomDataSource.Gmcp), "east");
            map.Observe(new RoomObservation("dock", "Docking Control", "", new Dictionary<string,string?> { ["north"] = "observation", ["east"] = "cargo", ["west"] = "command" }, Source: RoomDataSource.Gmcp), "west");
            window.Controller.FlushOutput();
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var title = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "AppTitle");
            Assert.Equal("WANDUR - " + worldName.ToUpperInvariant(), title.Text);
            var plaque = window.GetVisualDescendants().OfType<ThemePlaque>().Single();
            Assert.Equal(width / 2d, plaque.TranslatePoint(new Point(plaque.Bounds.Width / 2, 0), window)!.Value.X, 1);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Border>(), b => b.Name == "FleetDocumentHeader");
            var document = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "FleetDocumentFrame");
            var terminal = document.GetVisualDescendants().OfType<Wandur.Desktop.Views.TerminalView>().Single();
            Assert.InRange(terminal.TranslatePoint(default, document)!.Value.Y, 0, 2);
            var mapToolbar = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "MapToolbar");
            Assert.True(mapToolbar.Bounds.Height <= 40, "Map controls should fit a single compact toolbar row at supported dock widths.");
            var mapTools = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "MapToolsToggle");
            var mapInk = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(mapTools.Foreground);
            Assert.True(ContrastProbe.Contrast(mapInk.Color, ContrastProbe.Surface(mapTools, Avalonia.Media.Colors.Transparent)) >= 4.5,
                "Map toolbar ink should contrast with the shared shell metal behind its glyph.");
            var mapHost = mapToolbar.GetVisualAncestors().OfType<ThemeDockSkinHost>().First();
            var channelHost = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "ChannelsToolbar")
                .GetVisualAncestors().OfType<ThemeDockSkinHost>().First();
            var gap = channelHost.TranslatePoint(default, window)!.Value.Y -
                (mapHost.TranslatePoint(default, window)!.Value.Y + mapHost.Bounds.Height);
            Assert.InRange(gap, 3, 8);
            var channels = window.GetVisualDescendants().OfType<Wandur.Desktop.Views.ChannelsView>().Single();
            var channelBody = Assert.IsType<Grid>(channels.Content);
            var bodyInk = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(channelBody.Background);
            Assert.Equal(Avalonia.Media.Color.Parse("#11171B"), bodyInk.Color);
            var slider = window.GetVisualDescendants().OfType<Slider>().Single(s => s.Name == "MapZoomSlider");
            var thumb = slider.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Thumb>().Single();
            var thumbInk = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(thumb.Background);
            Assert.True(thumbInk.Color.R >= 80 && thumbInk.Color.G > thumbInk.Color.R,
                "Zoom thumb should use the skin's muted cyan, not the default saturated Fluent blue.");
            var look = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "LookButton");
            var glyph = Assert.IsType<Avalonia.Controls.Shapes.Path>(look.Content);
            var ink = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(glyph.Stroke);
            Assert.True(ink.Color.R >= 180, "Composer icons must remain legible against the dark input strip.");
            Assert.True(title.Bounds.Width <= plaque.Bounds.Width - FleetTitleLayout.TextInset * 2);
            Assert.Equal(Avalonia.Media.TextTrimming.CharacterEllipsis, title.TextTrimming);
            // The toolbar must actually receive the title's shaped socket. A straight metal rectangle
            // painted over it makes an otherwise correct title merely float in front of the toolbar.
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            using (var frame = window.CaptureRenderedFrame())
            {
                Assert.NotNull(frame);
                using var stream = new MemoryStream();
                frame.Save(stream, new PngBitmapEncoderOptions());
                using var pixels = SkiaSharp.SKBitmap.Decode(stream.ToArray());
                var origin = plaque.TranslatePoint(default, window)!.Value;
                var centerX = (int)Math.Round(origin.X + plaque.Bounds.Width / 2);
                var bottomY = (int)Math.Round(origin.Y + plaque.Bounds.Height);
                var underLip = pixels.GetPixel(centerX, bottomY + 2);
                var besideShoulder = pixels.GetPixel((int)Math.Round(origin.X - 6), 53);
                var toolbarFace = pixels.GetPixel(centerX, bottomY + 14);
                Assert.True(underLip.Red < 150, $"Missing dark recessed channel beneath plaque: {underLip}");
                Assert.True(besideShoulder.Red < 150, $"Missing shoulder socket: {besideShoulder}");
                Assert.True(toolbarFace.Red > 170, $"Recess must not darken the toolbar's usable face: {toolbarFace}");
            }
            Capture(window, worldName.Length > 25 ? "fleet-long-title.png" : $"fleet-{width}x{height}.png");

            // Each side is optional; hidden tools must return their width to the real session content.
            var withBoth = document.Bounds.Width;
            window.ToggleMap(); window.ToggleChannels();
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.True(document.Bounds.Width > withBoth + 100);
            var withLeft = document.Bounds.Width;
            window.TogglePanel();
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.True(document.Bounds.Width > withLeft + 100);
            if (worldName.Length <= 25) Capture(window, $"fleet-no-docks-{width}x{height}.png");
            window.ToggleMap(); window.ToggleChannels(); window.TogglePanel();
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.True(window.IsMapVisible && window.IsChannelsVisible && window.IsPanelVisible());
            window.Sessions.PreviewAppearanceSettings(new ClientSettings { Theme = "Paper" });
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.InRange(terminal.TranslatePoint(default, document)!.Value.Y, 0, 2);
            window.Sessions.EndAppearanceSettingsPreview();
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.InRange(terminal.TranslatePoint(default, document)!.Value.Y, 0, 2);
        }
        finally { await window.Sessions.DisposeAsync(); window.Close(); }
    }

    private static void Capture(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(4);
        using var image = window.CaptureRenderedFrame();
        Assert.NotNull(image);
        image.Save(Path.Combine(dir, name), new PngBitmapEncoderOptions());
        if (name == "fleet-1536x1024.png")
        {
            // Render the actual visual into a short viewport for inspecting the header joint.
            using var detail = new RenderTargetBitmap(new PixelSize(1536, 112));
            detail.Render(window);
            detail.Save(Path.Combine(dir, "fleet-header-detail.png"), new PngBitmapEncoderOptions());
        }
    }
}
