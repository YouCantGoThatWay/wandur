using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Settings;
using Wandur.Desktop.Terminal;
using Wandur.Desktop.Views;
using Surface = Iciclecreek.Terminal.TerminalView;

namespace Wandur.Desktop.Tests;

/// <summary>The split screen: while the transcript is scrolled back, the newest lines keep running below a divider.</summary>
public sealed class TranscriptTailTests
{
    private static WorkspaceController NewController() => new(
        new TranscriptDisplayFactory(), new SettingsStore(Path.Combine(Path.GetTempPath(), "wandur-tail-" + Guid.NewGuid() + ".json")),
        new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());

    private static TranscriptTailPane Tail(Window window) => Assert.Single(window.GetVisualDescendants().OfType<TranscriptTailPane>());
    private static GridSplitter Divider(Window window) => Assert.Single(window.GetVisualDescendants().OfType<GridSplitter>(), s => s.Name == "TranscriptDivider");
    private static Grid Output(Window window) => (Grid)Tail(window).Parent!;
    private static double TranscriptHeight(Window window) => Output(window).RowDefinitions[0].ActualHeight;
    private static double LiveHeight(Window window) => Output(window).RowDefinitions[2].ActualHeight;
    private static double Share(Window window) => LiveHeight(window) / (TranscriptHeight(window) + LiveHeight(window));

    private static void Write(WorkspaceController controller, int first, int count)
    {
        for (var i = first; i < first + count; i++) controller.Terminal.Append($"Line {i}\r\n");
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Settle the view the way a session does, so the welcome panel gives way to the transcript.</summary>
    private static void Settle(WorkspaceController controller, double share = 0.25)
    {
        controller.ApplySettings(controller.Settings with { ScrollTailShare = share });
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task ScrollingBackSplitsTheOutputAndReturningToTheBottomCollapsesIt()
    {
        await using var controller = NewController();
        var window = new Window { Width = 900, Height = 550, Content = new TerminalView(controller) };
        window.Show();
        try
        {
            var surface = Assert.Single(window.GetVisualDescendants().OfType<Surface>());
            var tail = Tail(window);
            Write(controller, 0, 200);
            Settle(controller);
            Assert.False(tail.IsVisible);
            Assert.False(Divider(window).IsVisible);
            Assert.Equal(0, LiveHeight(window));
            var whole = TranscriptHeight(window);

            surface.ViewportY = 5;
            Dispatcher.UIThread.RunJobs();
            Assert.True(tail.IsVisible);
            Assert.True(Divider(window).IsVisible);
            // A quarter of the output area, the divider aside, is the share the reader starts with.
            Assert.InRange(Share(window), 0.24, 0.26);
            Assert.InRange(LiveHeight(window), 0.25 * (whole - 4) - 3, 0.25 * (whole - 4) + 3);
            Assert.InRange(TranscriptHeight(window), 0.75 * (whole - 4) - 3, 0.75 * (whole - 4) + 3);

            var rows = tail.Rows;
            Assert.True(rows.Count >= 3, $"the live view fills its row, and it showed {rows.Count} lines");
            Assert.Equal("Line 199", rows[^1]);
            Assert.Equal($"Line {200 - rows.Count}", rows[0]);
            Assert.True(tail.Child!.Bounds.Height <= tail.Bounds.Height, "the lines it shows are the lines that fit");
            Capture(window, "tail-split.png");

            controller.Display.FollowTail();
            Dispatcher.UIThread.RunJobs();
            Assert.False(tail.IsVisible);
            Assert.False(Divider(window).IsVisible);
            Assert.Equal(0, LiveHeight(window));
            Assert.Equal(whole, TranscriptHeight(window), 1);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task NewOutputReachesTheLiveViewWithoutMovingTheTranscript()
    {
        await using var controller = NewController();
        var window = new Window { Width = 900, Height = 550, Content = new TerminalView(controller) };
        window.Show();
        try
        {
            var surface = Assert.Single(window.GetVisualDescendants().OfType<Surface>());
            var tail = Tail(window);
            Write(controller, 0, 200);
            Settle(controller);
            surface.ViewportY = 5;
            Dispatcher.UIThread.RunJobs();
            Assert.True(tail.IsVisible);
            var top = controller.Display.TopVisibleText;
            var shown = tail.Rows.Count;

            Write(controller, 200, 5);
            Assert.False(controller.Display.IsFollowingTail);
            Assert.Equal(top, controller.Display.TopVisibleText);
            Assert.Equal(shown, tail.Rows.Count);
            Assert.Equal("Line 204", tail.Rows[^1]);
            Assert.Equal($"Line {205 - shown}", tail.Rows[0]);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task DraggingTheDividerResizesBothRowsAndKeepsTheReadersPlace()
    {
        await using var controller = NewController();
        var window = new Window { Width = 900, Height = 550, Content = new TerminalView(controller) };
        window.Show();
        try
        {
            var surface = Assert.Single(window.GetVisualDescendants().OfType<Surface>());
            var tail = Tail(window);
            Write(controller, 0, 200);
            Settle(controller);
            surface.ViewportY = 5;
            Dispatcher.UIThread.RunJobs();
            var reading = controller.Display.TopVisibleText;
            Assert.Equal("Line 5", reading);
            var quarter = LiveHeight(window);
            var above = TranscriptHeight(window);
            var shown = tail.Rows.Count;

            // The divider dragged down to four tenths: both rows move, and the reader stays on their line.
            Settle(controller, 0.4);
            Assert.InRange(Share(window), 0.39, 0.41);
            Assert.True(LiveHeight(window) > quarter);
            Assert.True(TranscriptHeight(window) < above);
            Assert.True(tail.Rows.Count > shown);
            Assert.Equal(reading, controller.Display.TopVisibleText);

            // And the transcript keeps it again when the split closes under it.
            Settle(controller, 0);
            Assert.False(tail.IsVisible);
            Assert.Equal(reading, controller.Display.TopVisibleText);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task TheShareSettingSizesTheSplitAndZeroTurnsItOff()
    {
        await using var controller = NewController();
        var window = new Window { Width = 900, Height = 550, Content = new TerminalView(controller) };
        window.Show();
        try
        {
            var surface = Assert.Single(window.GetVisualDescendants().OfType<Surface>());
            var tail = Tail(window);
            Write(controller, 0, 200);
            Settle(controller, 0);
            surface.ViewportY = 5;
            Dispatcher.UIThread.RunJobs();
            Assert.False(tail.IsVisible);
            Assert.False(Divider(window).IsVisible);
            Assert.Equal(0, LiveHeight(window));

            Settle(controller, 0.5);
            Assert.True(tail.IsVisible);
            Assert.InRange(Share(window), 0.49, 0.51);
            Assert.Equal("Line 199", tail.Rows[^1]);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ClickingTheLiveViewReturnsTheTranscriptToTheLatestOutput()
    {
        await using var controller = NewController();
        var window = new Window { Width = 900, Height = 550, Content = new TerminalView(controller) };
        window.Show();
        try
        {
            var surface = Assert.Single(window.GetVisualDescendants().OfType<Surface>());
            var tail = Tail(window);
            Write(controller, 0, 200);
            Settle(controller);
            surface.ViewportY = 5;
            Dispatcher.UIThread.RunJobs();
            Assert.True(tail.IsVisible);

            var point = tail.TranslatePoint(new Point(40, tail.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.True(controller.Display.IsFollowingTail);
            Assert.False(tail.IsVisible);
            Assert.Equal(0, LiveHeight(window));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ThePaneFollowsTheAppliedTheme()
    {
        await using var controller = NewController();
        var window = new Window { Width = 900, Height = 550, Content = new TerminalView(controller) };
        window.Show();
        try
        {
            var surface = Assert.Single(window.GetVisualDescendants().OfType<Surface>());
            var tail = Tail(window);
            controller.Terminal.Append("\x1b[31mAn ember lantern\x1b[0m\r\n");
            Write(controller, 0, 200);
            Settle(controller);
            surface.ViewportY = 5;
            Dispatcher.UIThread.RunJobs();
            Assert.True(tail.IsVisible);
            var background = ((ISolidColorBrush)tail.Background!).Color;
            var foreground = ((ISolidColorBrush)TextElement.GetForeground(tail)!).Color;

            controller.ApplySettings(controller.Settings with { Theme = "Paper" });
            Dispatcher.UIThread.RunJobs();
            Assert.NotEqual(background, ((ISolidColorBrush)tail.Background!).Color);
            Assert.NotEqual(foreground, ((ISolidColorBrush)TextElement.GetForeground(tail)!).Color);
        }
        finally { window.Close(); ThemeService.Apply(new()); }
    }

    [AvaloniaFact]
    public async Task PrivateInputStaysOutOfTheLiveViewAsItStaysOutOfTheTranscript()
    {
        await using var controller = NewController();
        var window = new Window { Width = 900, Height = 550, Content = new TerminalView(controller) };
        window.Show();
        try
        {
            await controller.StartAsync();
            Dispatcher.UIThread.RunJobs();
            controller.FlushOutput();
            var surface = Assert.Single(window.GetVisualDescendants().OfType<Surface>());
            var tail = Tail(window);
            Write(controller, 0, 200);
            Settle(controller);
            surface.ViewportY = 5;
            Dispatcher.UIThread.RunJobs();
            Assert.True(tail.IsVisible);

            controller.SetManualPrivate(true);
            await controller.SendAsync("test-secret-129");
            controller.FlushOutput();
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain("test-secret-129", controller.Terminal.PlainText);
            Assert.DoesNotContain("test-secret-129", controller.Display.PlainText);
            Assert.DoesNotContain(tail.Rows, row => row.Contains("test-secret-129", StringComparison.Ordinal));
        }
        finally { window.Close(); }
    }

    private static void Capture(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR") is not { } directory) return;
        Directory.CreateDirectory(directory);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        using var frame = window.CaptureRenderedFrame();
        frame!.Save(Path.Combine(directory, name), new PngBitmapEncoderOptions());
    }
}
