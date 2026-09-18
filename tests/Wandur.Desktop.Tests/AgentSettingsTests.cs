using Wandur.Core.Agents;
using Wandur.Desktop.Services;
using Wandur.Desktop.ViewModels;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Desktop.Views;

namespace Wandur.Desktop.Tests;

public sealed class AgentSettingsTests
{
    [AvaloniaFact]
    public async Task TemplatesAreEditableCopiesAndMarkdownEditorsFollowTheSelectedGoal()
    {
        var services = new Services();
        using var model = new AgentProfileViewModel("world", services) { Model = "gemma" };
        model.AddGoalTemplateCommand.Execute("explore"); var first = model.SelectedGoal!;
        model.AddGoalTemplateCommand.Execute("explore"); var second = model.SelectedGoal!;
        Assert.NotEqual(first.Id, second.Id); Assert.Contains("##", first.Text); Assert.Contains("- ", first.Rules);
        first.Enabled = true; second.Enabled = true;
        Assert.False(first.Enabled); Assert.True(second.Enabled);
        var view = new AgentGoalsEditor(model);
        var window = new Window { Width = 850, Height = 440, Content = view };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var tabs = view.GetVisualDescendants().OfType<TabControl>().Single();
            var description = Assert.IsType<MarkdownCodeEditor>(Assert.IsType<TabItem>(tabs.Items[0]).Content);
            Assert.Equal("Markdown", description.SyntaxHighlighting.Name);
            description.Text = "# Custom objective\nFind **a room** with `look`.";
            Assert.Equal(description.Text, second.Text); Assert.NotEqual(first.Text, second.Text);
            using (var highlighter = new AvaloniaEdit.Highlighting.DocumentHighlighter(description.Document, description.SyntaxHighlighting))
                Assert.NotEmpty(highlighter.HighlightLine(1).Sections);
            tabs.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
            var rules = Assert.IsType<MarkdownCodeEditor>(Assert.IsType<TabItem>(tabs.Items[1]).Content);
            rules.Text = "- Do not attack.\n- Stop after one observation.";
            Assert.Equal(rules.Text, second.Rules);
            await model.SaveCommand.ExecuteAsync(null);
            Assert.Null(model.Error); Assert.Equal(second.Rules, services.Profile.Goals[1].Rules);
            model.SelectedGoal = first; Dispatcher.UIThread.RunJobs();
            Assert.Equal(first.Rules, rules.Text); Assert.Equal(first.Text, description.Text);
            if (Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR") is { } folder)
            {
                Directory.CreateDirectory(folder); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
                frame.Save(Path.Combine(folder, "goal-markdown-rules.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
        }
        finally { window.Close(); }
    }

    [Fact]
    public async Task GoalsMigrateFromLegacySettingsAndSaveIndependentDefaults()
    {
        var services = new Services(); services.Profile = services.Profile with { DefaultGoal = "Explore", Model = "gemma" };
        using var model = new AgentProfileViewModel("world", services);
        Assert.Equal("Explore", Assert.Single(model.Goals).Text);
        model.AddGoalCommand.Execute(null);
        model.SelectedGoal!.Text = "Find food";
        model.SelectedGoal.Enabled = false;
        await model.SaveCommand.ExecuteAsync(null);
        Assert.Null(model.Error); Assert.Empty(services.Profile.DefaultGoal);
        Assert.Equal(2, services.Profile.Goals.Count); Assert.False(services.Profile.Goals[1].Enabled);
        using var reopened = new AgentProfileViewModel("world", services);
        Assert.Equal("Find food", reopened.Goals[1].Text);
        reopened.SelectedGoal = reopened.Goals[0]; reopened.DeleteGoalCommand.Execute(null);
        await reopened.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Find food", Assert.Single(services.Profile.Goals).Text);
    }

    [AvaloniaTheory]
    [InlineData(900, 620, false)]
    [InlineData(600, 390, true)]
    public void SettingsRemainScrollableAtSmallSizes(int width, int height, bool expanded)
    {
        Wandur.Desktop.ThemeService.Apply(new Wandur.Core.Settings.ClientSettings());
        using var model = new AgentProfileViewModel("world", new Services());
        model.AddGoalTemplateCommand.Execute("explore");
        var view = new AgentSettingsView(model);
        var window = new Window { Width = width, Height = height, Content = view };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            if (expanded)
                foreach (var expander in view.GetVisualDescendants().OfType<Expander>()) expander.IsExpanded = true;
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            var editor = view.GetVisualDescendants().OfType<TabControl>().Single(t => t.Name == "AgentEditors");
            Assert.True(editor.Bounds.Height >= 140);
            editor.SelectedIndex = 1; window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Assert.True(view.GetVisualDescendants().OfType<Button>().Single(t => t.Name == "SaveAgentSettings").IsEffectivelyVisible);
            if (Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR") is { } folder)
            {
                Directory.CreateDirectory(folder);
                using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
                frame.Save(Path.Combine(folder, expanded ? "agent-settings-small.png" : "agent-settings.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void SettingsOfferBothIntegrationsAndMaskTheOptionalKey()
    {
        using var model = new AgentProfileViewModel("world", new Services());
        var view = new AgentSettingsView(model);
        var window = new Window { Width = 780, Height = 560, Content = view };
        try
        {
            window.Show(); window.UpdateLayout();
            Assert.Equal(2, model.Providers.Count);
            view.GetVisualDescendants().OfType<Expander>().Single(t => t.Name == "AgentCredentials").IsExpanded = true;
            window.UpdateLayout();
            var key = view.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "AgentApiKey");
            Assert.NotEqual(default, key.PasswordChar);
            Assert.True(view.GetVisualDescendants().OfType<TabControl>().Single(t => t.Name == "AgentEditors").Bounds.Height >= 140);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task HostAndProviderDebounceAutomaticDiscoveryAndIgnoreIncompleteCommandEdits()
    {
        var services = new Services();
        using var model = new AgentProfileViewModel("world", services) { ProviderIndex = 1, ServerAddress = "host-one:5555", Commands = "unfinished row" };
        model.ServerAddress = "host-two:9999";
        await ScriptSessionTests.WaitFor(() => model.Models.Count > 0);
        Assert.Equal("http://host-two:9999/api/v1", services.Provider.CapturedEndpoint);
        Assert.Equal(1, services.Provider.Calls);
        Assert.Equal("gemma-12b", model.Model);
        Assert.Equal(0, services.CredentialReads);
    }

    [Theory]
    [InlineData("auth")]
    [InlineData("address")]
    [InlineData("format")]
    [InlineData("timeout")]
    [InlineData("server")]
    public async Task DiscoveryExplainsFailureAndClearsBusyState(string failure)
    {
        var services = new Services();
        services.Provider.Results = Task.FromException<IReadOnlyList<string>>(failure switch
        {
            "auth" => new System.Net.Http.HttpRequestException("private upstream body", null, System.Net.HttpStatusCode.Unauthorized),
            "address" => new System.Net.Http.HttpRequestException("private upstream body"),
            "server" => new System.Net.Http.HttpRequestException("private upstream body", null, System.Net.HttpStatusCode.InternalServerError),
            "format" => new InvalidDataException("private upstream body"),
            _ => new TimeoutException("private upstream body")
        });
        using var model = new AgentProfileViewModel("world", services);
        await model.DiscoverModelsCommand.ExecuteAsync(null);
        var expected = failure switch
        {
            "auth" => Wandur.Core.Localization.Strings.AgentDiscoveryUnauthorized,
            "address" => Wandur.Core.Localization.Strings.AgentDiscoveryUnreachable,
            "format" => Wandur.Core.Localization.Strings.AgentDiscoveryWrongApi,
            "server" => Wandur.Core.Localization.Strings.Format(Wandur.Core.Localization.Strings.AgentDiscoveryServerError, 500),
            _ => Wandur.Core.Localization.Strings.AgentDiscoveryTimedOut
        };
        Assert.Equal(expected, model.Error); Assert.False(model.IsDiscovering);
    }

    [Fact]
    public async Task DisposingBeforeDebouncePreventsNetworkRequests()
    {
        var services = new Services(); var model = new AgentProfileViewModel("world", services);
        model.Dispose(); await Task.Delay(600, TestContext.Current.CancellationToken);
        Assert.Equal(0, services.Provider.Calls);
    }

    [AvaloniaFact]
    public async Task ModelsLoadAutomaticallyWhenSettingsOpen()
    {
        var services = new Services();
        using var model = new AgentProfileViewModel("world", services);
        await ScriptSessionTests.WaitFor(() => model.Models.Count > 0);
        Assert.Contains("gemma-12b", model.Models);
    }

    [Fact]
    public async Task NativeSelectionSwitchesStandardPathAndPersistsProvider()
    {
        var services = new Services();
        using var model = new AgentProfileViewModel("world", services) { Model = "gemma", JsonMode = true };
        model.ProviderIndex = 1;
        Assert.Equal("http://localhost:1234/api/v1", model.Endpoint);
        await model.SaveCommand.ExecuteAsync(null);
        Assert.Null(model.Error); Assert.Equal("lmstudio-native", services.Profile.Provider);
        Assert.False(services.Profile.JsonMode);
        using var reopened = new AgentProfileViewModel("world", services);
        Assert.Equal(1, reopened.ProviderIndex);
        reopened.ProviderIndex = 0;
        Assert.Equal("http://localhost:1234/v1", reopened.Endpoint);
        reopened.Endpoint = "http://localhost:4567/custom/prefix";
        reopened.ProviderIndex = 1;
        Assert.Equal("http://localhost:4567/custom/prefix/api/v1", reopened.Endpoint);
    }

    [Fact]
    public async Task ForgettingKeyDisablesStoredCredentialDiscovery()
    {
        var services = new Services();
        using var model = new AgentProfileViewModel("world", services) { ApiKey = "new-secret", ForgetKey = true };
        Assert.Empty(model.ApiKey);
        await model.DiscoverModelsCommand.ExecuteAsync(null);
        Assert.Null(services.Provider.CapturedKey);
        Assert.Equal(0, services.CredentialReads);
    }

    [Fact]
    public async Task DisposedViewModelDiscardsPendingDiscovery()
    {
        var services = new Services();
        var pending = new TaskCompletionSource<IReadOnlyList<string>>();
        services.Provider.Results = pending.Task;
        var model = new AgentProfileViewModel("world", services) { ApiKey = "secret" };
        var discovery = model.DiscoverModelsCommand.ExecuteAsync(null);
        model.Dispose();
        pending.SetResult(new[] { "stale-model" });
        await discovery;
        Assert.Empty(model.Models);
        Assert.Empty(model.ApiKey);
    }

    [Fact]
    public async Task SavingPassesCredentialSeparatelyAndClearsDraftSecret()
    {
        var services = new Services();
        using var model = new AgentProfileViewModel("world", services) { Model = "gemma-12b", ApiKey = "new-secret" };
        await model.SaveCommand.ExecuteAsync(null);
        Assert.Null(model.Error);
        Assert.Equal("new-secret", services.NewKey);
        Assert.Equal("gemma-12b", services.Profile.Model);
        Assert.Empty(model.ApiKey);
        Assert.False(model.HasUnsavedChanges);
    }

    [Fact]
    public async Task DiscoveryNeverSendsSavedCredentialToEditedEndpoint()
    {
        var services = new Services();
        using var model = new AgentProfileViewModel("world", services);
        await model.DiscoverModelsCommand.ExecuteAsync(null);
        Assert.Equal("saved-secret", services.Provider.CapturedKey);
        model.Endpoint = "http://localhost:4567/v1";
        await model.DiscoverModelsCommand.ExecuteAsync(null);
        Assert.Null(services.Provider.CapturedKey);
        Assert.Equal(1, services.CredentialReads);
    }

    [Fact]
    public async Task EndpointChangeDiscardsAnOldDiscoveryEvenWhenProviderIgnoresCancellation()
    {
        var services = new Services();
        var pending = new TaskCompletionSource<IReadOnlyList<string>>();
        services.Provider.Results = pending.Task;
        using var model = new AgentProfileViewModel("world", services);
        var discovery = model.DiscoverModelsCommand.ExecuteAsync(null);
        model.Endpoint = "http://localhost:4567/v1";
        pending.SetResult(new[] { "old-model" });
        await discovery;
        Assert.Empty(model.Models);
    }

    [Fact]
    public async Task InvalidCatalogDoesNotSaveAndDisposalClearsSecrets()
    {
        var services = new Services();
        var model = new AgentProfileViewModel("world", services) { Model = "gemma-12b", Commands = "look | look;quit | Invalid", ApiKey = "secret" };
        await model.SaveCommand.ExecuteAsync(null);
        Assert.True(model.HasError);
        Assert.Equal(0, services.Saves);
        model.Dispose();
        Assert.Empty(model.ApiKey);
        Assert.False(model.SaveCommand.CanExecute(null));
    }

    private sealed class Services : IAgentClientServices, IAgentProfileStore, IAgentProviderResolver
    {
        public AgentProfile Profile = new() { CredentialId = Guid.NewGuid() };
        public Provider Provider { get; } = new();
        public string? NewKey;
        public int Saves;
        public int CredentialReads;
        public IAgentProfileStore Profiles => this;
        public IAgentProviderResolver Providers => this;
        public event Action<Guid>? Saved;
        public AgentProfile Load(string worldKey) => Profile;
        public void Save(string worldKey, AgentProfile profile) { Profile = profile; Saved?.Invoke(profile.Id); }
        public IAgentModelProvider Resolve(string provider) => Provider;
        public Task<string?> ReadCredentialAsync(AgentProfile profile) { CredentialReads++; return Task.FromResult<string?>("saved-secret"); }
        public Task<AgentProfile> SaveAsync(string worldKey, AgentProfile profile, string newKey, bool forgetKey)
        {
            Saves++; NewKey = newKey; Save(worldKey, profile);
            return Task.FromResult(profile);
        }
    }

    private sealed class Provider : IAgentModelProvider
    {
        public string Key => "openai-compatible";
        public string? CapturedKey;
        public string? CapturedEndpoint;
        public int Calls;
        public Task<IReadOnlyList<string>> Results = Task.FromResult<IReadOnlyList<string>>(new[] { "gemma-12b" });
        public Task<IReadOnlyList<string>> ListModelsAsync(AgentProfile profile, string? key, CancellationToken token) { Calls++; CapturedKey = key; CapturedEndpoint = profile.Endpoint; return Results; }
        public Task<AgentDecision> DecideAsync(AgentRequest request, string? key, CancellationToken token) => throw new NotSupportedException();
    }
}
