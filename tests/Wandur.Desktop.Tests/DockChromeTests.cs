using System.Net;
using System.Net.Sockets;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Wandur.Core.Settings;

namespace Wandur.Desktop.Tests;

/// <summary>
/// The docking chrome: every dock is square, header and content, and neighbouring panels are separated by
/// the 4 px gap and the splitter alone. The header shows the move cursor on its grip glyph and an arrow elsewhere.
/// </summary>
public sealed class DockChromeTests
{
    private static MainWindow CreateWindow()
    {
        var window = new MainWindow(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), new SettingsStore(Path.Combine(Path.GetTempPath(), "wandur-dock-" + Guid.NewGuid(), "settings.json")), new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        window.Width = 1200; window.Height = 800;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        return window;
    }

    private static Border ChromeBorder<TOwner>(Window window, Func<Control, bool> owner) where TOwner : Control =>
        window.GetVisualDescendants().OfType<Border>().Single(border =>
            border.Name == "PART_Border" && border.TemplatedParent is TOwner parent && owner((Control)parent));

    private static bool AlignedTo(Control control, Alignment alignment, string? id = null) =>
        control.DataContext is IToolDock dock && dock.Alignment == alignment && (id is null || dock.Id == id);

    private static Rect Frame(Border border, Window window) =>
        new(border.TranslatePoint(new Point(0, 0), window) ?? default, border.Bounds.Size);

    [AvaloniaFact]
    public void EveryDockIsSquareAndTheSideDocksMeetTheCentreAcrossTheGap()
    {
        var window = CreateWindow();
        try
        {
            var document = ChromeBorder<DocumentControl>(window, _ => true);
            var leftHeader = ChromeBorder<ToolChromeControl>(window, control => AlignedTo(control, Alignment.Left));
            var rightHeader = ChromeBorder<ToolChromeControl>(window, control => AlignedTo(control, Alignment.Right, "map-dock"));
            var channelsHeader = ChromeBorder<ToolChromeControl>(window, control => AlignedTo(control, Alignment.Right, "channels-dock"));
            var leftContent = ChromeBorder<ToolControl>(window, control => AlignedTo(control, Alignment.Left));
            var rightContent = ChromeBorder<ToolControl>(window, control => AlignedTo(control, Alignment.Right, "map-dock"));
            var channelsContent = ChromeBorder<ToolControl>(window, control => AlignedTo(control, Alignment.Right, "channels-dock"));

            Assert.Equal(new CornerRadius(0), document.CornerRadius);
            Assert.Equal(new Thickness(1), document.BorderThickness);
            foreach (var border in new[] { leftHeader, leftContent, rightHeader, rightContent, channelsHeader, channelsContent })
                Assert.Equal(new CornerRadius(0), border.CornerRadius);

            // The header drags as a whole, but only its grip glyph says so with the cursor; the title shows an arrow.
            var chrome = (ToolChromeControl)leftHeader.TemplatedParent!;
            var grip = chrome.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "PART_Grip");
            var glyph = chrome.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "PART_Grid");
            var title = chrome.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "PART_Title");
            var close = chrome.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PART_CloseButton");
            Assert.Equal(nameof(StandardCursorType.Arrow), grip.Cursor?.ToString());
            Assert.Equal(nameof(StandardCursorType.SizeAll), glyph.Cursor?.ToString());
            Assert.Equal(nameof(StandardCursorType.Arrow), title.Cursor?.ToString());
            Assert.Equal(nameof(StandardCursorType.Hand), close.Cursor?.ToString());

            var centre = Frame(document, window);
            var left = Frame(leftContent, window);
            var right = Frame(rightContent, window);
            Assert.InRange(centre.Left - left.Right, 0, 6);
            Assert.InRange(right.Left - centre.Right, 0, 6);
            Assert.Equal(Frame(leftHeader, window).Right, left.Right);
            Assert.Equal(Frame(rightHeader, window).Left, right.Left);
            var channels = Frame(channelsContent, window);
            Assert.Equal(right.Left, channels.Left);
            Assert.Equal(right.Right, channels.Right);
            Assert.True(channels.Top >= right.Bottom, "the Channels panel sits below the map on the right edge");

            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            var directory = Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR");
            if (!string.IsNullOrEmpty(directory)) { Directory.CreateDirectory(directory); frame.Save(Path.Combine(directory, "dock-after.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions()); }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ScriptPanelDocksAreSquareLikeTheEdgeTheyShare()
    {
        var path = Path.Combine(Path.GetTempPath(), "wandur-dock-panels-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var profile = new ConnectionProfile { Name = "Panel world", Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port };
        var window = new MainWindow(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), new SettingsStore(Path.Combine(path, "settings.json")), new MemoryPasswordVault(),
            new MemoryRoomMapStore(), new InlineScriptFactory(), new MemoryScriptLibraryStore()) { Width = 1200, Height = 800 };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            await window.Sessions.OpenAsync(profile);
            using var server = await listener.AcceptTcpClientAsync(timeout.Token);
            var script = window.Controller.ScriptLibrary.Items[0];
            script.Runtime.Source = "mud.panel('ship', { title: 'Ship' }).label('a', { text: 'Hull 80%' }); mud.panel('nav', { title: 'Nav', dock: 'left' }).label('b', { text: 'Tatooine' });";
            await script.Runtime.RunAsync();
            Assert.True(script.Runtime.IsRunning, script.Runtime.Error);
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);

            var document = ChromeBorder<DocumentControl>(window, _ => true);
            var panelsHeader = ChromeBorder<ToolChromeControl>(window, control => AlignedTo(control, Alignment.Right, "panels-dock"));
            var panelsContent = ChromeBorder<ToolControl>(window, control => AlignedTo(control, Alignment.Right, "panels-dock"));
            var leftPanelsHeader = ChromeBorder<ToolChromeControl>(window, control => AlignedTo(control, Alignment.Left, "panels-left-dock"));
            var leftPanelsContent = ChromeBorder<ToolControl>(window, control => AlignedTo(control, Alignment.Left, "panels-left-dock"));
            var mapContent = ChromeBorder<ToolControl>(window, control => AlignedTo(control, Alignment.Right, "map-dock"));
            var channelsContent = ChromeBorder<ToolControl>(window, control => AlignedTo(control, Alignment.Right, "channels-dock"));
            var libraryContent = ChromeBorder<ToolControl>(window, control => AlignedTo(control, Alignment.Left, "left"));

            // A panels dock is square like its neighbours, header and content alike.
            foreach (var border in new[] { panelsHeader, panelsContent, leftPanelsHeader, leftPanelsContent, document })
                Assert.Equal(new CornerRadius(0), border.CornerRadius);

            var centre = Frame(document, window);
            var map = Frame(mapContent, window);
            var panels = Frame(panelsContent, window);
            var channels = Frame(channelsContent, window);
            Assert.Equal(map.Left, panels.Left);
            Assert.Equal(map.Right, panels.Right);
            Assert.True(panels.Top >= map.Bottom, "the panels sit below the map");
            Assert.True(channels.Top >= panels.Bottom, "the channels sit below the panels");
            Assert.InRange(panels.Left - centre.Right, 0, 6);
            var library = Frame(libraryContent, window);
            var leftPanels = Frame(leftPanelsContent, window);
            Assert.Equal(library.Left, leftPanels.Left);
            Assert.Equal(library.Right, leftPanels.Right);
            Assert.True(leftPanels.Top >= library.Bottom, "the left panels sit below the world library");
            Assert.InRange(centre.Left - leftPanels.Right, 0, 6);
            if (Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR") is { } directory)
            {
                using var frame = window.CaptureRenderedFrame();
                Directory.CreateDirectory(directory);
                frame!.Save(Path.Combine(directory, "square-docks.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
        }
        finally
        {
            await window.Sessions.DisposeAsync(); window.Close();
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }
}
