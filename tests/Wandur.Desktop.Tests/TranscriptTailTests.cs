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

/// <summary>The split screen: while the transcript is scrolled back, the newest lines keep running below it.</summary>
public sealed class TranscriptTailTests
{
    private static WorkspaceController NewController() => new(
        new TranscriptDisplayFactory(), new SettingsStore(Path.Combine(Path.GetTempPath(), "wandur-tail-" + Guid.NewGuid() + ".json")),
        new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());

    private static TranscriptTailPane Tail(Window window) => Assert.Single(window.GetVisualDescendants().OfType<TranscriptTailPane>());

    private static void Write(WorkspaceController controller, int first, int count)
    {
        for (var i = first; i < first + count; i++) controller.Terminal.Append($"Line {i}\r\n");
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Settle the view the way a session does, so the welcome panel gives way to the transcript.</summary>
    private static void Settle(WorkspaceController controller, int lines = 8)
    {
        controller.ApplySettings(controller.Settings with { ScrollTailLines = lines });
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task ScrollingBackShowsTheNewestLinesAndReturningToTheBottomHidesThem()
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

            surface.ViewportY = 5;
            Dispatcher.UIThread.RunJobs();
            Assert.True(tail.IsVisible);
            Assert.Equal(8, tail.Rows.Count);
            Assert.Equal("Line 192", tail.Rows[0]);
            Assert.Equal("Line 199", tail.Rows[^1]);
            Assert.True(tail.Bounds.Height > 0, "The pane is as tall as the lines it shows.");
            Capture(window, "transcript-tail.png");

            controller.Display.FollowTail();
            Dispatcher.UIThread.RunJobs();
            Assert.False(tail.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task NewOutputReachesTheTailWithoutMovingTheTranscript()
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

            Write(controller, 200, 5);
            Assert.Equal(5, surface.ViewportY);
            Assert.False(controller.Display.IsFollowingTail);
            Assert.Equal("Line 204", tail.Rows[^1]);
            Assert.Equal("Line 197", tail.Rows[0]);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ClickingTheTailReturnsTheTranscriptToTheLatestOutput()
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
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task TheLineCountSettingSizesThePaneAndZeroTurnsItOff()
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

            Settle(controller, 4);
            Assert.True(tail.IsVisible);
            Assert.Equal(4, tail.Rows.Count);
            Assert.Equal("Line 196", tail.Rows[0]);
            Assert.Equal("Line 199", tail.Rows[^1]);
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
    public async Task PrivateInputStaysOutOfTheTailAsItStaysOutOfTheTranscript()
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
