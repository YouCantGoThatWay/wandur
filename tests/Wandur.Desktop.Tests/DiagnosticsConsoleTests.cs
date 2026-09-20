using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Diagnostics;
using Wandur.Core.Settings;
using Wandur.Desktop;
using Wandur.Desktop.ViewModels;
using Wandur.Desktop.Views;

namespace Wandur.Desktop.Tests;

public sealed class DiagnosticsConsoleTests
{
    private static WorkspaceController Controller() => new(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(),
        new SettingsStore(Path.Combine(Path.GetTempPath(), "wandur-console-" + Guid.NewGuid(), "settings.json")),
        new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());

    private static void Click(Window window, Control target)
    {
        var point = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
    }

    private static (ConsoleView View, DiagnosticsBodyEditor Editor) OpenConsole(TerminalView view, WorkspaceController controller)
    {
        controller.Pages.SelectedIndex = (int)SessionPage.Diagnostics; Dispatcher.UIThread.RunJobs();
        var tabs = view.GetVisualDescendants().OfType<TabControl>().Single(t => t.Name == "ProtocolDiagnosticTabs");
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(t => t.Name == "ProtocolConsoleTab");
        Dispatcher.UIThread.RunJobs();
        var console = view.GetVisualDescendants().OfType<ConsoleView>().Single();
        var editor = console.GetVisualDescendants().OfType<DiagnosticsBodyEditor>().Single(e => e.Name == "ConsoleText");
        return (console, editor);
    }

    [AvaloniaFact]
    public async Task ConsoleShowsTheRawStreamSentCommandsAndPrivateMarkersAndCanPauseAndClear()
    {
        await using var controller = Controller();
        var view = new TerminalView(controller);
        var window = new Window { Content = view, Width = 900, Height = 650 };
        using var server = new TcpListener(IPAddress.Loopback, 0); server.Start();
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            await controller.StartAsync(new ConnectionProfile { Host = "127.0.0.1", Port = ((IPEndPoint)server.LocalEndpoint).Port });
            using var socket = await server.AcceptTcpClientAsync();
            using var reader = new StreamReader(socket.GetStream(), Encoding.UTF8, leaveOpen: true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            async Task Output(byte[] bytes)
            {
                await socket.GetStream().WriteAsync(bytes, timeout.Token);
                for (var i = 0; i < 10; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); controller.FlushOutput(); }
            }
            Task OutputText(string text) => Output(Encoding.UTF8.GetBytes(text));
            var (console, editor) = OpenConsole(view, controller);
            Assert.True(editor.IsEffectivelyVisible);
            Assert.Null(editor.SyntaxHighlighting);

            await OutputText("\u001B[32mA green line\u001B[0m\r\n\u001B[1;31mA bold red line\u001B[0m\r\n");
            Assert.Contains("<< ␛[32mA green line␛[0m␍␊", editor.Document.Text);
            Assert.Contains("␛[1;31mA bold red line␛[0m␍␊", editor.Document.Text);
            Assert.DoesNotContain("\u001B", editor.Document.Text, StringComparison.Ordinal);

            Assert.True(await controller.SendAsync("look"));
            Assert.Equal("look", await reader.ReadLineAsync(timeout.Token));
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(">> look", editor.Document.Text);
            await OutputText("\u001B[36mYou are standing in a lantern-lit hall.\u001B[0m\r\n");

            Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            using (var frame = window.CaptureRenderedFrame())
            {
                Assert.NotNull(frame);
                if (Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR") is { } folder)
                { Directory.CreateDirectory(folder); frame.Save(Path.Combine(folder, "diagnostics-console.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions()); }
            }

            // Private input: received and sent text becomes one marker, and the sent secret is masked afterwards.
            controller.SetManualPrivate(true);
            await OutputText("Password: \r\n");
            Assert.True(await controller.SendAsync("hunter2"));
            Assert.Equal("hunter2", await reader.ReadLineAsync(timeout.Token));
            await OutputText("Checking hunter2...\r\n");
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain("Password", editor.Document.Text);
            Assert.DoesNotContain("hunter2", editor.Document.Text);
            Assert.DoesNotContain("Checking", editor.Document.Text);
            Assert.Equal(1, editor.Document.Text.Split(ConsoleEntry.PrivateMarker).Length - 1);
            controller.SetManualPrivate(false);
            await OutputText("Welcome back, your password hunter2 is weak.\r\n");
            Assert.Contains("Welcome back, your password [redacted] is weak.", editor.Document.Text);
            Assert.DoesNotContain("hunter2", editor.Document.Text);
            Assert.Contains("hunter2", controller.Display.PlainText);

            // Pause holds the view while the log keeps filling; unpausing catches up.
            var pause = console.GetVisualDescendants().OfType<ToggleButton>().Single(t => t.Name == "ConsolePause");
            pause.IsChecked = true; Dispatcher.UIThread.RunJobs();
            var before = controller.ConsoleLog.Count;
            await OutputText("Arrived while paused.\r\n");
            Assert.Equal(before + 1, controller.ConsoleLog.Count);
            Assert.DoesNotContain("Arrived while paused.", editor.Document.Text);
            Assert.True(console.IsStale);
            pause.IsChecked = false; Dispatcher.UIThread.RunJobs();
            Assert.Contains("Arrived while paused.", editor.Document.Text);
            Assert.False(console.IsStale);

            // The protocol message view is unaffected.
            await Output(new byte[] { 255, 251, 201 }.Concat(new byte[] { 255, 250, 201 }).Concat(Encoding.UTF8.GetBytes("Char.Vitals {\"hp\":42}")).Concat(new byte[] { 255, 240 }).ToArray());
            var entry = Assert.Single(controller.Diagnostics.Entries);
            Assert.Equal("GMCP", entry.Protocol); Assert.Contains("42", entry.Content!.Body);
            Assert.DoesNotContain("Char.Vitals", editor.Document.Text);

            Click(window, console.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ConsoleClear"));
            Assert.Equal(0, controller.ConsoleLog.Count);
            Assert.Equal("", editor.Document.Text);
            Assert.Single(controller.Diagnostics.Entries);
            await OutputText("After clear.\r\n");
            Assert.Contains("<< After clear.", editor.Document.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ConsoleTabAppearsOnlyWithALogAndHidesTheMessageBarWhileSelected()
    {
        var model = new ProtocolDiagnosticsViewModel();
        var plainWindow = new Window { Content = new ProtocolDiagnosticsView(model), Width = 900, Height = 620 };
        try
        {
            plainWindow.Show(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, plainWindow.GetVisualDescendants().OfType<TabControl>().Single(t => t.Name == "ProtocolDiagnosticTabs").Items.Count);
        }
        finally { plainWindow.Close(); }
        var log = new ConsoleLog();
        var view = new ProtocolDiagnosticsView(model, log);
        var window = new Window { Content = view, Width = 900, Height = 620 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var tabs = view.GetVisualDescendants().OfType<TabControl>().Single(t => t.Name == "ProtocolDiagnosticTabs");
            Assert.Equal(3, tabs.Items.Count);
            var follow = view.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "FollowDiagnostics");
            Assert.True(follow.IsEffectivelyVisible);
            tabs.SelectedIndex = 2; Dispatcher.UIThread.RunJobs();
            Assert.False(follow.IsEffectivelyVisible);
            var editor = view.GetVisualDescendants().OfType<DiagnosticsBodyEditor>().Single(e => e.Name == "ConsoleText");
            log.Append(ConsoleEntryKind.Sent, "north", false);
            Assert.Contains(">> north", editor.Document.Text);
            tabs.SelectedIndex = 0; Dispatcher.UIThread.RunJobs();
            Assert.True(follow.IsEffectivelyVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ConsoleOnlyNotesChangesWhileTheDiagnosticsPageIsHiddenAndCatchesUpWhenShown()
    {
        await using var controller = Controller();
        var view = new TerminalView(controller);
        var window = new Window { Content = view, Width = 900, Height = 650 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var (console, editor) = OpenConsole(view, controller);
            controller.ConsoleLog.Append(ConsoleEntryKind.Received, "shown\r\n", false);
            Assert.Contains("shown", editor.Document.Text);
            controller.Pages.SelectedIndex = (int)SessionPage.Play; Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.False(console.IsEffectivelyVisible, "console still effectively visible on the Play page");
            controller.ConsoleLog.Append(ConsoleEntryKind.Received, "hidden page\r\n", false);
            Assert.True(console.IsStale);
            Assert.DoesNotContain("hidden page", editor.Document.Text);
            controller.Pages.SelectedIndex = (int)SessionPage.Diagnostics; Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.True(console.IsEffectivelyVisible);
            Assert.False(console.IsStale, "console did not catch up after the page was shown again");
            Assert.Contains("hidden page", editor.Document.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ScriptEchoesAppearAsScriptLines()
    {
        var factory = new RecordingScriptFactory { BlockDispatch = true };
        await using var controller = new WorkspaceController(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(),
            new SettingsStore(Path.Combine(Path.GetTempPath(), "wandur-console-script-" + Guid.NewGuid(), "settings.json")),
            new MemoryPasswordVault(), new MemoryRoomMapStore(), factory, new MemoryScriptLibraryStore());
        using var server = new TcpListener(IPAddress.Loopback, 0); server.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await controller.StartAsync(new ConnectionProfile { Host = "127.0.0.1", Port = ((IPEndPoint)server.LocalEndpoint).Port });
        using var socket = await server.AcceptTcpClientAsync(timeout.Token);
        await controller.ScriptLibrary.Items[0].Runtime.RunAsync();
        await socket.GetStream().WriteAsync(Encoding.UTF8.GetBytes("A line for the script.\r\n"), timeout.Token);
        await ScriptSessionTests.WaitFor(() => factory.Runtime!.Events.Count > 0);
        factory.Runtime!.Release.SetResult(new Wandur.Core.Scripting.ScriptResult(true, [new Wandur.Core.Scripting.ScriptAction("echo", "from a script")]));
        await ScriptSessionTests.WaitFor(() => controller.ConsoleLog.Snapshot().Any(e => e.Kind == ConsoleEntryKind.Script));
        var echo = controller.ConsoleLog.Snapshot().First(e => e.Kind == ConsoleEntryKind.Script);
        Assert.Equal("from a script", echo.Text);
        Assert.EndsWith("[script] from a script", echo.Render());
        Assert.Contains(controller.ConsoleLog.Snapshot(), e => e.Kind == ConsoleEntryKind.Received && e.Text.Contains("A line for the script."));
    }

    [AvaloniaFact]
    public void ViewDropsEvictedEntriesFromTheTopInsteadOfReRenderingEverything()
    {
        var log = new ConsoleLog();
        var view = new ProtocolDiagnosticsView(new ProtocolDiagnosticsViewModel(), log);
        var window = new Window { Content = view, Width = 900, Height = 620 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var tabs = view.GetVisualDescendants().OfType<TabControl>().Single(t => t.Name == "ProtocolDiagnosticTabs");
            tabs.SelectedIndex = 2; Dispatcher.UIThread.RunJobs();
            var editor = view.GetVisualDescendants().OfType<DiagnosticsBodyEditor>().Single(e => e.Name == "ConsoleText");
            for (var i = 1; i <= ConsoleLog.MaximumEntries + 50; i++) log.Append(ConsoleEntryKind.Received, $"entry {i}\r\n", false);
            var lines = editor.Document.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(ConsoleLog.MaximumEntries, lines.Length);
            Assert.EndsWith("<< entry 51␍␊", lines[0]);
            Assert.EndsWith($"<< entry {ConsoleLog.MaximumEntries + 50}␍␊", lines[^1]);
            Assert.Equal(log.Render() + "\n", editor.Document.Text);
            log.Clear();
            Assert.Equal("", editor.Document.Text);
        }
        finally { window.Close(); }
    }
}
