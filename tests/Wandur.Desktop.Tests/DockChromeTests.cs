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
using Wandur.Desktop.Views;

namespace Wandur.Desktop.Tests;

/// <summary>
/// The docking chrome: the centre document is squared off, each side dock is rounded only on its outer
/// edge (left docks on the left, right docks on the right), header and content alike, and neighbouring
/// panels are separated by the 4 px gap and the splitter alone. The header shows the move cursor on its
/// grip glyph and an arrow elsewhere.
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
    public void SideDocksAreRoundedOnTheirOuterEdgeOnlyAndMeetTheSquareCentreAcrossTheGap()
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
            Assert.Equal(new CornerRadius(10, 0, 0, 0), leftHeader.CornerRadius);
            Assert.Equal(new CornerRadius(0, 0, 0, 10), leftContent.CornerRadius);
            Assert.Equal(new CornerRadius(0, 10, 0, 0), rightHeader.CornerRadius);
            Assert.Equal(new CornerRadius(0, 0, 10, 0), rightContent.CornerRadius);
            // The Channels panel shares the right edge below the map, rounded on that edge the same way.
            Assert.Equal(new CornerRadius(0, 10, 0, 0), channelsHeader.CornerRadius);
            Assert.Equal(new CornerRadius(0, 0, 10, 0), channelsContent.CornerRadius);

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
    public async Task ScriptPanelDocksAreRoundedLikeTheEdgeTheyShare()
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
            var mapContent = ChromeBorder<ToolControl>(window, control => AlignedTo(control, Alignment.Right, "map-dock"));
            var channelsContent = ChromeBorder<ToolControl>(window, control => AlignedTo(control, Alignment.Right, "channels-dock"));
            var libraryHeader = ChromeBorder<ToolChromeControl>(window, control => AlignedTo(control, Alignment.Left, "left"));
            var libraryContent = ChromeBorder<ToolControl>(window, control => AlignedTo(control, Alignment.Left, "left"));
            var mapHeader = ChromeBorder<ToolChromeControl>(window, control => AlignedTo(control, Alignment.Right, "map-dock"));
            var channelsHeader = ChromeBorder<ToolChromeControl>(window, control => AlignedTo(control, Alignment.Right, "channels-dock"));

            // Script panels no longer get a dock: they live in the session rail beside the transcript.
            var rail = Assert.Single(window.GetVisualDescendants().OfType<ScriptPanelRailView>());
            Assert.True(rail.IsVisible);
            Assert.Equal(2, window.Controller.ScriptLibrary.Panels.Panels.Count(panel => !panel.IsBars));
            Assert.Null(window.Workspace.RightPanelsDock);
            Assert.Null(window.Workspace.LeftPanelsDock);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<ToolControl>(), control => AlignedTo(control, Alignment.Right, "panels-dock"));
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<ToolControl>(), control => AlignedTo(control, Alignment.Left, "panels-left-dock"));

            Assert.Equal(new CornerRadius(0), document.CornerRadius);
            Assert.Equal(new CornerRadius(10, 0, 0, 0), libraryHeader.CornerRadius);
            Assert.Equal(new CornerRadius(0, 0, 0, 10), libraryContent.CornerRadius);
            Assert.Equal(new CornerRadius(0, 10, 0, 0), mapHeader.CornerRadius);
            Assert.Equal(new CornerRadius(0, 0, 10, 0), mapContent.CornerRadius);
            Assert.Equal(new CornerRadius(0, 10, 0, 0), channelsHeader.CornerRadius);
            Assert.Equal(new CornerRadius(0, 0, 10, 0), channelsContent.CornerRadius);

            var centre = Frame(document, window);
            var map = Frame(mapContent, window);
            var channels = Frame(channelsContent, window);
            Assert.True(channels.Top >= map.Bottom, "the channels sit below the map on the right edge");
            Assert.InRange(map.Left - centre.Right, 0, 6);
            var library = Frame(libraryContent, window);
            Assert.InRange(centre.Left - library.Right, 0, 6);
            if (Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR") is { } directory)
            {
                using var frame = window.CaptureRenderedFrame();
                Directory.CreateDirectory(directory);
                frame!.Save(Path.Combine(directory, "rounded-docks.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
        }
        finally
        {
            await window.Sessions.DisposeAsync(); window.Close();
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    private static string[] LayoutShape(IDock dock) =>
        dock.VisibleDockables!.Select(item => item is IProportionalDockSplitter ? "splitter" : item.Id ?? "?").ToArray();

    /// <summary>
    /// Pinning or hiding both right-edge tools must collapse the right column so the session document
    /// expands into that width. Leaving <c>IsCollapsable=false</c> on the right proportional dock left a
    /// grey empty strip beside the transcript.
    /// </summary>
    [AvaloniaFact]
    public void HidingMapAndChannelsCollapsesTheRightColumn()
    {
        var window = CreateWindow();
        try
        {
            var library = Assert.IsAssignableFrom<IToolDock>(window.Workspace.WorldsTool!.Owner);
            var layout = Assert.IsAssignableFrom<IProportionalDock>(library.Owner);
            Assert.Equal(new[] { "left", "splitter", "documents", "splitter", "right" }, LayoutShape(layout));
            Assert.True(window.IsMapVisible);
            Assert.True(window.IsChannelsVisible);

            window.ToggleMap();
            window.ToggleChannels();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);

            Assert.False(window.IsMapVisible);
            Assert.False(window.IsChannelsVisible);
            Assert.Equal(new[] { "left", "splitter", "documents" }, LayoutShape(layout));
            Assert.DoesNotContain(layout.VisibleDockables!, item => item.Id == "right");

            // Restoring either tool brings the right column back.
            window.ToggleMap();
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.IsMapVisible);
            Assert.Contains(layout.VisibleDockables!, item => item.Id == "right");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void PinningMapAndChannelsCollapsesTheRightColumn()
    {
        var window = CreateWindow();
        try
        {
            var map = window.Workspace.MapTool!;
            var channels = window.Workspace.ChannelsTool!;
            var library = Assert.IsAssignableFrom<IToolDock>(window.Workspace.WorldsTool!.Owner);
            var layout = Assert.IsAssignableFrom<IProportionalDock>(library.Owner);
            Assert.Contains(layout.VisibleDockables!, item => item.Id == "right");

            window.Workspace.PinDockable(map);
            window.Workspace.PinDockable(channels);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);

            Assert.True(window.Workspace.IsDockablePinned(map));
            Assert.True(window.Workspace.IsDockablePinned(channels));
            Assert.Equal(new[] { "left", "splitter", "documents" }, LayoutShape(layout));
            Assert.DoesNotContain(layout.VisibleDockables!, item => item.Id == "right");
        }
        finally { window.Close(); }
    }
}
