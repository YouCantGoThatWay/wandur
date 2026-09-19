using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia.Headless.XUnit;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Wandur.Desktop.Views;
using Wandur.Desktop.ViewModels;
using Avalonia.Threading;
using Wandur.Core.Agents;
using Wandur.Core.Settings;
using Wandur.Desktop.Services;

namespace Wandur.Desktop.Tests;

public sealed class AgentSessionTests
{
    [AvaloniaTheory]
    [InlineData(520)]
    [InlineData(900)]
    public async Task FooterFitsAlongsideOutputTabsAndShowsLiveStatus(int width)
    {
        await using var world = await World.Open();
        var window = new Window { Width = width, Height = 500, Content = new TerminalView(world.Controller) };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var footer = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "OutputFooter");
            var status = footer.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "SessionAgentStatus");
            var scripts = footer.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SessionScripts");
            var toggle = footer.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "PrivateInputToggle");
            // The private toggle group is fixed width and takes priority over the agent status ellipsis text,
            // so status is only guaranteed room once the bar is wide enough (the toggle must never be clipped).
            Assert.Equal(28, footer.Bounds.Height); Assert.True(scripts.Bounds.Width > 50); Assert.True(toggle.Bounds.Width > 10);
            if (width >= 900) Assert.True(status.Bounds.Width > 10);
            Assert.Equal(world.Controller.Agent!.Status, status.Text);
            Assert.Equal(2, footer.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.TabStripItem>().Count());
            if (Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR") is { } folder)
            {
                Directory.CreateDirectory(folder); Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
                Assert.NotNull(frame); frame.Save(Path.Combine(folder, $"terminal-footer-{width}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ChoosingAnotherGoalReplacesSelectionAndSendsItsDescriptionAndRules()
    {
        await using var world = await World.Open();
        world.Services.Save("world", world.Services.Profile with { DefaultGoal = "", Goals = [
            new(Guid.NewGuid(), "Explore the academy", true),
            new AgentGoal(Guid.NewGuid(), "Review the room", false) { Name = "Observe", Rules = "- Do not move\n- Stop after looking" }] });
        var model = world.Controller.Agent!;
        model.Goals[1].Enabled = true;
        Assert.False(model.Goals[0].Enabled); Assert.Single(model.Goals, g => g.Enabled);
        await model.PreviewCommand.ExecuteAsync(null);
        Assert.Contains("Review the room", world.Services.Request!.Goal);
        Assert.Contains("Do not move", world.Services.Request.Goal);
        Assert.DoesNotContain("Explore the academy", world.Services.Request.Goal);
        Assert.True(world.Services.Profile.Goals[0].Enabled);
    }

    [AvaloniaFact]
    public async Task CheckedGoalsDriveRequestsAndChangingSelectionStopsPendingWork()
    {
        await using var world = await World.Open();
        var profile = world.Services.Profile with { DefaultGoal = "", Goals = [new(Guid.NewGuid(), "Explore", true), new(Guid.NewGuid(), "Find food", false)] };
        world.Services.Save("world", profile);
        var model = world.Controller.Agent!;
        Assert.Equal(2, model.Goals.Count);
        await model.PreviewCommand.ExecuteAsync(null);
        Assert.Contains("Explore", world.Services.Request!.Goal); Assert.DoesNotContain("Find food", world.Services.Request.Goal);
        model.Goals[0].Enabled = false;
        Assert.False(model.CanStart);
        model.Goals[1].Enabled = true;
        world.Services.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = model.RunCommand.ExecuteAsync(null);
        await ScriptSessionTests.WaitFor(() => model.IsBusy);
        model.Goals[1].Enabled = false;
        world.Services.Pending.SetResult(new("look", "", ""));
        await run;
        Assert.False(model.IsBusy); Assert.Equal(0, world.Socket.Available);
        Assert.False(world.Services.Profile.Goals[1].Enabled);
    }

    [AvaloniaFact]
    public async Task AgentSectionAndLiveMenuBindToTheOriginatingWorld()
    {
        await using var world = await World.Open();
        var profile = new ConnectionProfile { Name = "Test world", Host = "localhost" };
        world.Controller.SaveSettings(world.Controller.Settings with { Profiles = [profile] });
        using var model = new ProfileEditorViewModel(world.Controller, new EmptyDirectory(), profile, agents: world.Services) { SectionIndex = 4 };
        var dialog = new ProfileDialog(model);
        try
        {
            dialog.Show(); Dispatcher.UIThread.RunJobs(); dialog.UpdateLayout();
            Assert.True(model.IsAgent); Assert.True(model.CanEditAutomation);
            Assert.Equal("test", dialog.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "AgentModel").Text);
            model.SectionIndex = 0; model.SectionIndex = 4; Dispatcher.UIThread.RunJobs();
            Assert.NotNull(model.Agent);
        }
        finally { dialog.Close(); }
        int? section = null;
        var toolbar = new SessionAutomationToolbar(world.Controller.Pages.Automation, index => section = index, world.Controller.Agent);
        var window = new Window { Width = 700, Height = 750, Content = toolbar };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var button = toolbar.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SessionAgent");
            button.Flyout!.ShowAt(button); Dispatcher.UIThread.RunJobs();
            var host = Assert.IsType<ScrollViewer>(Assert.IsType<Flyout>(button.Flyout).Content);
            var view = host.GetVisualDescendants().OfType<AgentSessionView>().Single();
            var goal = view.GetVisualDescendants().OfType<RadioButton>().Single();
            Assert.True(goal.IsChecked); Assert.Equal("Explore", Assert.IsType<TextBlock>(goal.Content).Text);
            Assert.NotNull(toolbar.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "SessionAgentStatus"));
            if (Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR") is { } folder)
            {
                Directory.CreateDirectory(folder); window.UpdateLayout(); Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
                Assert.NotNull(frame); frame.Save(Path.Combine(folder, "agent-goals-popup.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
            var configure = view.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == Wandur.Core.Localization.Strings.AgentConfigure);
            configure.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(4, section);
        }
        finally { window.Close(); }
    }

    private sealed class EmptyDirectory : Wandur.Core.Discovery.IWorldDirectory
    {
        public Task<Wandur.Core.Discovery.WorldNameSuggestion?> LookupAsync(string host, int port, CancellationToken cancellationToken = default)
            => Task.FromResult<Wandur.Core.Discovery.WorldNameSuggestion?>(null);
    }

    [AvaloniaFact]
    public async Task PreviewUsesPublicRoomContextWithoutSendingAndStepWaitsForOutput()
    {
        await using var world = await World.Open();
        await world.Output("A quiet room.\n");
        await world.Controller.Agent!.PreviewCommand.ExecuteAsync(null);
        Assert.Contains("A quiet room", world.Services.Request!.Observation);
        Assert.Equal(0, world.Socket.Available);
        var step = world.Controller.Agent.StepCommand.ExecuteAsync(null);
        Assert.Equal("look", await world.Command());
        await world.Output("A doorway leads north.\n");
        await step;
        Assert.Equal("remember room", world.Controller.Agent.Memory);
        Assert.False(world.Controller.Agent.IsBusy);
        Assert.Equal(0, world.Socket.Available);
    }

    [AvaloniaFact]
    public async Task ManualInputCancelsAnUncooperativeProviderAndStillSendsTheManualCommand()
    {
        await using var world = await World.Open();
        world.Services.Pending = new TaskCompletionSource<AgentDecision>();
        var run = world.Controller.Agent!.RunCommand.ExecuteAsync(null);
        Assert.True(world.Controller.Agent.IsBusy);
        Assert.True(await world.Controller.SendAsync("score"));
        Assert.Equal("score", await world.Command());
        await run.WaitAsync(TimeSpan.FromSeconds(3));
        world.Services.Pending.SetResult(new("look", "stale", "stale"));
        await Task.Delay(30); Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, world.Socket.Available); Assert.Empty(world.Controller.Agent.Memory);
    }

    [AvaloniaFact]
    public async Task PrivateInputAndTransientEchoBurstsNeverEnterAgentContext()
    {
        await using var world = await World.Open();
        world.Services.Pending = new TaskCompletionSource<AgentDecision>();
        var run = world.Controller.Agent!.RunCommand.ExecuteAsync(null);
        await world.Output(new byte[] { 255, 251, 1 }.Concat(Encoding.UTF8.GetBytes("private-secret\n")).Concat(new byte[] { 255, 252, 1 }).ToArray());
        await run.WaitAsync(TimeSpan.FromSeconds(3));
        world.Services.Pending.SetResult(new("look", "stale", "stale"));
        world.Services.Pending = null;
        await world.Output("A public room.\n");
        await world.Controller.Agent.PreviewCommand.ExecuteAsync(null);
        Assert.DoesNotContain("private-secret", world.Services.Request!.Observation);
        Assert.Contains("A public room", world.Services.Request.Observation);
        world.Controller.SetManualPrivate(true);
        await world.Output("another-secret\n");
        world.Controller.SetManualPrivate(false);
        await world.Output("public-again\n");
        await world.Controller.Agent.PreviewCommand.ExecuteAsync(null);
        Assert.DoesNotContain("another-secret", world.Services.Request.Observation);
    }

    [AvaloniaFact]
    public async Task SeparateConnectionsDoNotShareMemoryOrGoalAndSavingSettingsStopsTheRun()
    {
        await using var first = await World.Open(); await using var second = await World.Open();
        first.Controller.Agent!.Goals[0].Text = "first goal"; second.Controller.Agent!.Goals[0].Text = "second goal";
        first.Services.Pending = new TaskCompletionSource<AgentDecision>();
        var run = first.Controller.Agent.RunCommand.ExecuteAsync(null);
        first.Services.Save("world", first.Services.Profile);
        await run.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("second goal", second.Controller.Agent.Goal); Assert.Empty(second.Controller.Agent.Memory);
        Assert.Equal(0, first.Socket.Available); Assert.Equal(0, second.Socket.Available);
    }

    private sealed class Services : IAgentClientServices, IAgentProfileStore, IAgentProviderResolver, IAgentModelProvider
    {
        public AgentProfile Profile = new() { Model = "test", DefaultGoal = "Explore", ResponseTimeoutSeconds = 2 };
        public TaskCompletionSource<AgentDecision>? Pending;
        public AgentRequest? Request;
        public string Key => "openai-compatible";
        public IAgentProfileStore Profiles => this; public IAgentProviderResolver Providers => this;
        public event Action<Guid>? Saved;
        public AgentProfile Load(string worldKey) => Profile;
        public void Save(string worldKey, AgentProfile profile) { Profile = profile; Saved?.Invoke(profile.Id); }
        public IAgentModelProvider Resolve(string key) => this;
        public Task<string?> ReadCredentialAsync(AgentProfile profile) => Task.FromResult<string?>(null);
        public Task<AgentProfile> SaveAsync(string worldKey, AgentProfile profile, string key, bool forget)
        { Save(worldKey, profile); return Task.FromResult(profile); }
        public Task<AgentDecision> DecideAsync(AgentRequest request, string? key, CancellationToken token)
        { Request = request; return Pending?.Task ?? Task.FromResult(new AgentDecision("look", "Observe", "remember room")); }
        public Task<IReadOnlyList<string>> ListModelsAsync(AgentProfile profile, string? key, CancellationToken token) => Task.FromResult<IReadOnlyList<string>>([]);
    }
    private sealed class World(TcpListener listener, TcpClient socket, WorkspaceController controller, Services services) : IAsyncDisposable
    {
        public TcpClient Socket => socket;
        public WorkspaceController Controller => controller;
        public Services Services => services;
        public static async Task<World> Open()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var services = new Services();
            var controller = new WorkspaceController(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(),
                new SettingsStore(Path.Combine(Path.GetTempPath(), "wandur-agent-" + Guid.NewGuid(), "settings.json")),
                new MemoryPasswordVault(), new MemoryRoomMapStore(), new InlineScriptFactory(), new MemoryScriptLibraryStore(), agents: services);
            await controller.StartAsync(new ConnectionProfile { Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port });
            return new(listener, await listener.AcceptTcpClientAsync(), controller, services);
        }
        public Task Output(string text) => Output(Encoding.UTF8.GetBytes(text));
        public async Task Output(byte[] bytes)
        {
            await socket.GetStream().WriteAsync(bytes);
            for (var i = 0; i < 5; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); controller.FlushOutput(); }
        }
        public async Task<string> Command()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var bytes = new List<byte>();
            while (true)
            {
                var b = new byte[1]; Assert.Equal(1, await socket.GetStream().ReadAsync(b, timeout.Token));
                if (b[0] == 10) return Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\r');
                bytes.Add(b[0]);
            }
        }
        public async ValueTask DisposeAsync() { await controller.DisposeAsync(); socket.Dispose(); listener.Dispose(); }
    }
}
