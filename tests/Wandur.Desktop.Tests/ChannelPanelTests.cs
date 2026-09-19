using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Settings;
using Wandur.Desktop.ViewModels;
using Wandur.Desktop.Views;

namespace Wandur.Desktop.Tests;

/// <summary>
/// The docked Channels panel mirrors channel traffic beside the transcript. Nothing it shows is taken out
/// of the transcript, which is what these tests check first and check again after every other assertion.
/// </summary>
public sealed class ChannelPanelTests
{
    private const string Ooc = "[OOC] Aldric: anyone selling a lantern?\r\n";
    private const string Tell = "Jorunn tells you 'bring the brass key'\r\n";
    private static byte[] Gmcp(string text) => [255, 250, 201, .. Encoding.UTF8.GetBytes(text), 255, 240];

    private static ChannelMessageList Messages(Window window) => Assert.Single(window.GetVisualDescendants().OfType<ChannelMessageList>());
    private static ListBox TabStrip(Window window) => Assert.Single(window.GetVisualDescendants().OfType<ListBox>(), b => b.Name == "ChannelTabs");
    private static TextBox Reply(Window window) => Assert.Single(window.GetVisualDescendants().OfType<TextBox>(), b => b.Name == "ChannelReply");
    private static Color Of(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;
    private static void Show(ChannelsViewModel model, ChannelTabViewModel tab)
    { model.SelectedIndex = model.Tabs.IndexOf(tab); Dispatcher.UIThread.RunJobs(); }

    [AvaloniaFact]
    public async Task ChannelLinesAppearOnTheirOwnTabsAndStayInTheTranscript()
    {
        await using var world = await World.Open();
        var model = new ChannelsViewModel(world.Controller);
        var window = new Window { Width = 380, Height = 520, Content = new ChannelsView(model) };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            Assert.True(window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ChannelsMirrorNote").IsVisible);
            await world.Output("You are standing in a wide green field.\r\n" + Ooc + Tell);
            await world.Output([255, 251, 201]);
            await world.Output(Gmcp("Comm.Channel.Text {\"channel\":\"chat\",\"talker\":\"Brenna\",\"text\":\"[CHAT] Brenna: who wants to group up?\"}"));
            window.UpdateLayout();

            // All, then the private channel, then the open ones in the order they were first heard.
            Assert.Equal(["All", "tell", "ooc", "chat"], model.Tabs.Select(t => t.Title));
            Assert.Equal(3, model.Tabs[0].Messages.Count);
            Assert.Single(model.Tabs[1].Messages);
            Assert.Single(model.Tabs[2].Messages);
            Assert.Single(model.Tabs[3].Messages);
            Assert.Equal("Jorunn", model.Tabs[1].Messages[0].Speaker);
            Assert.Equal("bring the brass key", model.Tabs[1].Messages[0].Text);
            Assert.True(model.Tabs[1].Messages[0].IsPrivate);
            Assert.Equal("Aldric", model.Tabs[2].Messages[0].Speaker);
            Assert.Equal("anyone selling a lantern?", model.Tabs[2].Messages[0].Text);
            Assert.Equal("Brenna", model.Tabs[3].Messages[0].Speaker);
            Assert.Equal("who wants to group up?", model.Tabs[3].Messages[0].Text);

            // The panel is a copy. Every line, channel or not, is still in the transcript.
            var transcript = world.Controller.Terminal.PlainText;
            Assert.Contains("You are standing in a wide green field.", transcript);
            Assert.Contains("[OOC] Aldric: anyone selling a lantern?", transcript);
            Assert.Contains("Jorunn tells you 'bring the brass key'", transcript);

            Assert.False(window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ChannelsMirrorNote").IsVisible);
            Assert.Equal(3, Messages(window).Rows.Count);
            Assert.Contains("Aldric: anyone selling a lantern?", Messages(window).Rows[0]);
            Assert.Contains("Jorunn: bring the brass key", Messages(window).Rows[1]);
            Assert.Contains("Brenna: who wants to group up?", Messages(window).Rows[2]);
            Assert.Equal(4, TabStrip(window).ItemCount);
            if (Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR") is { } folder)
            {
                Directory.CreateDirectory(folder); Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
                Assert.NotNull(frame); frame.Save(Path.Combine(folder, "channels.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ATabCarriesItsUnreadCountUntilTheReaderLooksAtIt()
    {
        await using var world = await World.Open();
        var model = new ChannelsViewModel(world.Controller);
        var window = new Window { Width = 380, Height = 520, Content = new ChannelsView(model) };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            await world.Output(Ooc);
            var ooc = model.Tabs.Single(t => t.Channel == "ooc");
            // The All tab is shown, so nothing it received is unread.
            Assert.Equal(0, model.Tabs[0].Unread);
            Assert.Equal(1, ooc.Unread);
            Show(model, ooc);
            Assert.Equal(0, ooc.Unread);
            await world.Output(Tell);
            var tell = model.Tabs.Single(t => t.Channel == "tell");
            // A private channel takes its place ahead of the open ones without moving what is shown.
            Assert.Same(ooc, model.Selected);
            Assert.Equal(1, model.Tabs[0].Unread);
            Assert.Equal(0, ooc.Unread);
            Assert.Equal(1, tell.Unread);
            window.UpdateLayout();
            var badges = window.GetVisualDescendants().OfType<Border>().Where(b => b.Name == "ChannelUnread").ToList();
            Assert.Equal(3, badges.Count);
            Assert.Equal(2, badges.Count(b => b.IsVisible));
            Show(model, model.Tabs[0]);
            Assert.Equal(0, model.Tabs[0].Unread);
            Show(model, tell);
            Assert.Equal(0, tell.Unread);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task TheReplyBoxSpeaksOnTheChannelItIsShowingAndNeverDuringPrivateInput()
    {
        await using var world = await World.Open();
        var model = new ChannelsViewModel(world.Controller);
        var window = new Window { Width = 380, Height = 520, Content = new ChannelsView(model) };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            // The All tab shows every channel at once, so it has no one channel to answer on.
            Assert.False(model.CanReply);
            Assert.Equal(Wandur.Core.Localization.Strings.ChannelsReplyAllHint, model.ReplyHint);
            Assert.False(Reply(window).IsEnabled);
            await world.Output(Ooc + Tell);

            Show(model, model.Tabs.Single(t => t.Channel == "ooc"));
            Assert.True(model.CanReply);
            Assert.True(Reply(window).IsEnabled);
            model.Draft = "hello";
            Assert.True(model.SendCommand.CanExecute(null));
            await model.SendCommand.ExecuteAsync(null);
            Assert.Equal("ooc hello", await world.Command());
            Assert.Equal("", model.Draft);

            // A private channel answers the person who spoke last.
            Show(model, model.Tabs.Single(t => t.Channel == "tell"));
            model.Draft = "hi";
            await model.SendCommand.ExecuteAsync(null);
            Assert.Equal("tell Jorunn hi", await world.Command());

            world.Controller.SetManualPrivate(true);
            Dispatcher.UIThread.RunJobs();
            Assert.False(model.CanReply);
            Assert.False(Reply(window).IsEnabled);
            model.Draft = "secret";
            Assert.False(model.SendCommand.CanExecute(null));
            await model.SendCommand.ExecuteAsync(null);
            Assert.DoesNotContain("secret", await world.Drain());
            world.Controller.SetManualPrivate(false);
            Dispatcher.UIThread.RunJobs();
            Assert.True(model.CanReply);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task MessagesKeepTheTranscriptsColorsAndFollowAThemeChange()
    {
        await using var world = await World.Open();
        var model = new ChannelsViewModel(world.Controller);
        var window = new Window { Width = 380, Height = 520, Content = new ChannelsView(model) };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            world.Controller.SaveSettings(world.Controller.Settings with { Theme = "Ember" });
            await world.Output("[OOC] Aldric: \u001b[31manyone selling a lantern?\u001b[0m\r\n");
            window.UpdateLayout();
            var list = Messages(window);
            var block = Assert.Single(list.GetVisualDescendants().OfType<TextBlock>());
            var body = block.Inlines!.OfType<Run>().Last();
            Assert.Equal("anyone selling a lantern?", body.Text);
            var before = Of(body.Foreground);
            var backgroundBefore = Of(list.Background);

            world.Controller.SaveSettings(world.Controller.Settings with { Theme = "Paper" });
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var after = Of(Assert.Single(Messages(window).GetVisualDescendants().OfType<TextBlock>()).Inlines!.OfType<Run>().Last().Foreground);
            Assert.NotEqual(before, after);
            Assert.NotEqual(backgroundBefore, Of(Messages(window).Background));
        }
        finally { window.Close(); ThemeService.Apply(new()); }
    }

    /// <summary>A session against a loopback listener, opened as a SMAUG world so the family rules apply.</summary>
    private sealed class World(TcpListener listener, TcpClient socket, WorkspaceController controller) : IAsyncDisposable
    {
        public TcpClient Socket => socket;
        public WorkspaceController Controller => controller;
        public static async Task<World> Open()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var controller = new WorkspaceController(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(),
                new SettingsStore(Path.Combine(Path.GetTempPath(), "wandur-channels-" + Guid.NewGuid(), "settings.json")),
                new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
            await controller.StartAsync(new ConnectionProfile
            { Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, Codebase = "SMAUG 1.4a" });
            return new(listener, await listener.AcceptTcpClientAsync(), controller);
        }
        public Task Output(string text) => Output(Encoding.UTF8.GetBytes(text));
        public async Task Output(byte[] bytes)
        {
            await socket.GetStream().WriteAsync(bytes);
            for (var i = 0; i < 6; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); controller.FlushOutput(); }
            Dispatcher.UIThread.RunJobs();
        }
        /// <summary>Whatever the client has written that is not a command line, so a blocked send can be proven.</summary>
        public async Task<string> Drain()
        {
            for (var i = 0; i < 5; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); controller.FlushOutput(); }
            var available = socket.Available;
            if (available == 0) return "";
            var buffer = new byte[available];
            var read = await socket.GetStream().ReadAsync(buffer);
            return Encoding.UTF8.GetString(buffer, 0, read);
        }
        public async Task<string> Command()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var bytes = new List<byte>();
            while (true)
            {
                var b = new byte[1]; Assert.Equal(1, await socket.GetStream().ReadAsync(b, timeout.Token));
                if (b[0] == 10) return Encoding.UTF8.GetString([.. bytes]).TrimEnd('\r');
                bytes.Add(b[0]);
            }
        }
        public async ValueTask DisposeAsync() { await controller.DisposeAsync(); socket.Dispose(); listener.Dispose(); }
    }
}
