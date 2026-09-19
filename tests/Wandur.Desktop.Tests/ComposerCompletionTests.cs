using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Localization;
using Wandur.Core.Settings;
using Wandur.Desktop.Views;

namespace Wandur.Desktop.Tests;

/// <summary>
/// Inline completion in the composer: a muted ghost after the typed text, learned from public server lines
/// and from the command history, taken with Tab or Right, put away with Escape.
/// </summary>
public sealed class ComposerCompletionTests
{
    private static T Find<T>(Control root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static void Press(Window window, PhysicalKey key)
    {
        window.KeyPressQwerty(key, RawInputModifiers.None);
        window.KeyReleaseQwerty(key, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Type(Window window, TextBox input, string text)
    {
        input.Focus();
        window.KeyTextInput(text);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task GhostFollowsSeenWordsAndHistoryAndTheKeysAcceptOrDismissIt()
    {
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), "wandur-composer-complete-" + Guid.NewGuid(), "settings.json"));
        await using var controller = new WorkspaceController(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), store, new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var window = new Window { Width = 900, Height = 550, Content = new TerminalView(controller) };
        window.Show();
        try
        {
            await controller.StartAsync(new ConnectionProfile { Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, Name = "Completion test" });
            using var server = await listener.AcceptTcpClientAsync(timeout.Token);
            await ScriptSessionTests.WaitFor(() => controller.IsConnected);
            Dispatcher.UIThread.RunJobs();
            var input = Find<TextBox>(window, "CommandInput");
            var ghost = Find<TextBlock>(window, "CompletionGhost");
            var hint = Find<TextBlock>(window, "CommandHint");
            Assert.False(ghost.IsVisible);

            // A public line teaches the words in it.
            await server.GetStream().WriteAsync(Encoding.UTF8.GetBytes("A Vicious Womprat scurries past.\r\n"), timeout.Token);
            await ScriptSessionTests.WaitFor(() => { controller.FlushOutput(); return controller.Completions.Words.Suggest("wom").Count > 0; });
            Type(window, input, "wom");
            Assert.True(ghost.IsVisible);
            Assert.Equal("prat", ghost.Text);
            Assert.Equal(Strings.CommandHintTabComplete, hint.Text);
            // The ghost sits inside the box, after the typed text, at the box's font size.
            var origin = ghost.TranslatePoint(new Point(0, 0), input);
            Assert.NotNull(origin);
            Assert.True(origin.Value.X > input.Padding.Left, $"ghost x {origin.Value.X}");
            Assert.True(origin.Value.X + ghost.Bounds.Width <= input.Bounds.Width, "ghost fits inside the box");
            Assert.InRange(origin.Value.Y, 0, input.Bounds.Height);
            Assert.Equal(input.FontSize, ghost.FontSize);
            Capture(window, "composer-complete.png");

            // Tab takes the whole ghost, keeps focus in the composer and sends nothing.
            var sent = controller.CommandsSent;
            Press(window, PhysicalKey.Tab);
            Assert.Equal("womprat", input.Text);
            Assert.Equal(7, input.CaretIndex);
            Assert.True(input.IsFocused);
            Assert.False(ghost.IsVisible);
            Assert.Equal(sent, controller.CommandsSent);
            Assert.Equal(Strings.CommandHistoryEnterSend, hint.Text);

            // A sent command becomes a line to complete, and the line wins over the word.
            input.Text = "";
            Assert.True(await controller.SendAsync("look womprat"));
            Dispatcher.UIThread.RunJobs();
            input.Text = "";
            Type(window, input, "look");
            Assert.True(ghost.IsVisible);
            Assert.Equal(" womprat", ghost.Text);
            input.Text = "";
            Type(window, input, "loo");
            Assert.Equal("k womprat", ghost.Text);

            // Escape puts it away until the draft changes; Right at the end takes it.
            Press(window, PhysicalKey.Escape);
            Assert.False(ghost.IsVisible);
            Assert.Equal("loo", input.Text);
            Assert.Equal(Strings.CommandHistoryEnterSend, hint.Text);
            Type(window, input, "k");
            Assert.True(ghost.IsVisible);
            Assert.Equal(" womprat", ghost.Text);
            Press(window, PhysicalKey.ArrowRight);
            Assert.Equal("look womprat", input.Text);
            Assert.False(ghost.IsVisible);

            // Away from the end of the text there is nothing to offer.
            input.Text = "";
            Type(window, input, "loo");
            Assert.True(ghost.IsVisible);
            input.CaretIndex = 1;
            Dispatcher.UIThread.RunJobs();
            Assert.False(ghost.IsVisible);

            // Nothing is offered while private, and a line received while private is never learned.
            controller.SetManualPrivate(true);
            Dispatcher.UIThread.RunJobs();
            input.Text = "";
            Type(window, input, "wom");
            Assert.False(ghost.IsVisible);
            await server.GetStream().WriteAsync(Encoding.UTF8.GetBytes("A Sneaky Bantha wanders by.\r\n"), timeout.Token);
            await ScriptSessionTests.WaitFor(() => { controller.FlushOutput(); return controller.Terminal.PlainText.Contains("Bantha"); });
            controller.SetManualPrivate(false);
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(controller.Completions.Words.Suggest("ban"));
            input.Text = "";
            Type(window, input, "ban");
            Assert.False(ghost.IsVisible);
            Type(window, input, "tha wom");
            Assert.True(ghost.IsVisible);
            Assert.Equal("prat", ghost.Text);

            // The setting turns the ghost off at once.
            controller.ApplySettings(controller.Settings with { ComposerSuggestions = false });
            Dispatcher.UIThread.RunJobs();
            Assert.False(ghost.IsVisible);
            input.Text = "";
            Type(window, input, "wom");
            Assert.False(ghost.IsVisible);
            Press(window, PhysicalKey.Tab);
            Assert.Equal("wom", input.Text);
            controller.ApplySettings(controller.Settings with { ComposerSuggestions = true });
            Dispatcher.UIThread.RunJobs();
            Assert.True(ghost.IsVisible);
            Assert.Equal("prat", ghost.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ANewWorldStartsWithAnEmptyVocabulary()
    {
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), "wandur-composer-reset-" + Guid.NewGuid(), "settings.json"));
        await using var controller = new WorkspaceController(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), store, new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        await controller.StartAsync();
        await ScriptSessionTests.WaitFor(() => controller.IsConnected);
        Assert.True(await controller.SendAsync("look zyxxyz"));
        Assert.Contains("zyxxyz", controller.Completions.Words.Words());
        Assert.Contains("look zyxxyz", controller.History.Entries);
        await controller.DisconnectAsync();
        await controller.StartAsync();
        await ScriptSessionTests.WaitFor(() => controller.IsConnected);
        Assert.Empty(controller.History.Entries);
        Assert.DoesNotContain("zyxxyz", controller.Completions.Words.Words());
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
