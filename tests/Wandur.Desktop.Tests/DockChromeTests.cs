using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Wandur.Core.Settings;

namespace Wandur.Desktop.Tests;

/// <summary>
/// The docking chrome: the centre document is squared off, each side dock is rounded only on its
/// outer edge, and neighbouring panels are separated by the splitter alone.
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
    public void SideDocksAreRoundedOnlyOnTheirOuterEdgeAndMeetTheSquaredCentre()
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
}
