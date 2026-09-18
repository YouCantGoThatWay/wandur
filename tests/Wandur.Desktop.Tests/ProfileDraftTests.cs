using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Agents;
using Wandur.Core.Discovery;
using Wandur.Core.Scripting;
using Wandur.Core.Settings;
using Wandur.Desktop.Services;
using Wandur.Desktop.ViewModels;
using Wandur.Desktop.Views;

namespace Wandur.Desktop.Tests;

public sealed class ProfileDraftTests
{
    private const string Key = "mud.example:4000:False";

    [AvaloniaFact]
    public async Task SaveCommitsEverySectionIncludingUnselectedRules()
    {
        var store = new ProfileStore(); var scripts = new MemoryScriptLibraryStore(); var agents = new Agents();
        var initial = scripts.Load(Key).ToArray();
        using var model = new ProfileEditorViewModel(store, new Directory(), store.Profiles[0], new ProfileAutomationFactory(new InlineScriptFactory(), scripts), agents);
        model.Username = "Pilot";
        var automation = model.Automation!;
        automation.Scripts.Name = "Edited original";
        automation.Scripts.NewCommand.Execute(null);
        automation.Scripts.Name = "Second";
        automation.Scripts.Source = "mud.echo('draft');";
        await automation.Scripts.Selected!.EnableCommand.ExecuteAsync(true);
        automation.Macros.NewCommand.Execute(null);
        automation.Macros.Name = "My macro";
        model.Agent!.Model = "local-model";
        model.Agent.AddGoalTemplateCommand.Execute("explore");
        Assert.Equal(initial, scripts.Load(Key));
        Assert.Equal(0, agents.Saves);
        var closed = false; model.CloseRequested += () => closed = true;
        await model.SaveCommand.ExecuteAsync(null);
        Assert.False(model.HasError, model.Error);
        Assert.True(closed);
        Assert.Equal("Pilot", store.Profiles[0].Username);
        var saved = scripts.Load(Key);
        Assert.Contains(saved, s => s.Name == "Edited original");
        Assert.Contains(saved, s => s.Name == "Second" && s.Enabled && s.Source == "mud.echo('draft');");
        Assert.Contains(saved, s => s.Name == "My macro" && s.Macro is not null);
        Assert.Equal("local-model", agents.Profile.Model);
        Assert.Single(agents.Profile.Goals);
    }

    [AvaloniaFact]
    public async Task CancelDiscardsAddsDeletesEnablesAndCredentialsOnlyAfterConfirmation()
    {
        var store = new ProfileStore(); var scripts = new MemoryScriptLibraryStore(); var agents = new Agents();
        var initial = scripts.Load(Key).ToArray();
        using var model = new ProfileEditorViewModel(store, new Directory(), store.Profiles[0], new ProfileAutomationFactory(new InlineScriptFactory(), scripts), agents);
        var window = new ProfileDialog(model); window.Show();
        model.Automation!.Scripts.RequestDeleteCommand.Execute(null);
        await model.Automation.Scripts.DeleteCommand.ExecuteAsync(null);
        model.Automation.Macros.NewCommand.Execute(null);
        model.Agent!.ApiKey = "unsaved-test-key";
        window.FindControl<Button>("CancelWorld")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsVisible);
        var confirmation = Assert.Single(window.OwnedWindows);
        Capture(confirmation, "discard-warning");
        confirmation.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "KeepEditing").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs(); Assert.True(window.IsVisible);
        window.Close(); Dispatcher.UIThread.RunJobs();
        confirmation = Assert.Single(window.OwnedWindows);
        confirmation.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "DiscardChanges").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.IsVisible);
        Assert.Equal(initial, scripts.Load(Key)); Assert.Equal(0, agents.Saves); Assert.Equal(0, store.Saves);
    }

    [AvaloniaFact]
    public async Task InvalidAgentPreventsOtherSectionsFromSaving()
    {
        var store = new ProfileStore(); var scripts = new MemoryScriptLibraryStore(); var agents = new Agents();
        var initial = scripts.Load(Key).ToArray();
        using var model = new ProfileEditorViewModel(store, new Directory(), store.Profiles[0], new ProfileAutomationFactory(new InlineScriptFactory(), scripts), agents);
        model.WorldName = "New name"; model.Automation!.Scripts.Name = "Changed";
        model.Agent!.Model = "local-model"; model.Agent.MaxDecisions = 0;
        await model.SaveCommand.ExecuteAsync(null);
        Assert.True(model.HasError);
        Assert.Equal(0, store.Saves); Assert.Equal(0, agents.Saves); Assert.Equal(initial, scripts.Load(Key));
    }

    [AvaloniaFact]
    public async Task NewConnectionCanBeConfiguredInEverySectionBeforeItsFirstSave()
    {
        var store = new ProfileStore(); var scripts = new MemoryScriptLibraryStore(); var agents = new Agents();
        using var model = new ProfileEditorViewModel(store, new Directory(), automationFactory: new ProfileAutomationFactory(new InlineScriptFactory(), scripts), agents: agents);
        model.WorldName = "Brand new"; model.Host = "new.example";
        model.Automation!.Scripts.NewCommand.Execute(null); model.Automation.Scripts.Name = "New script";
        model.Agent!.Model = "local-model";
        model.Agent.AddGoalTemplateCommand.Execute("explore");
        model.SectionIndex = 4;
        var window = new ProfileDialog(model); window.Show(); Dispatcher.UIThread.RunJobs();
        window.GetVisualDescendants().OfType<TabControl>().Single(t => t.Name == "AgentEditors").SelectedIndex = 1;
        Capture(window, "connection-agent-goals");
        Assert.True(window.FindControl<Button>("SaveWorld")!.IsEffectivelyVisible);
        Assert.True(window.FindControl<Button>("CancelWorld")!.IsEffectivelyVisible);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), b => b.Name == "SaveAgentSettings" && b.IsEffectivelyVisible);
        Assert.Equal(0, store.Saves); Assert.Equal(0, agents.Loads);
        await model.SaveCommand.ExecuteAsync(null);
        Assert.False(model.HasError, model.Error); Assert.False(window.IsVisible);
        Assert.Contains(store.Profiles, p => p.Name == "Brand new");
        Assert.Contains(scripts.Load("new.example:4000:False"), p => p.Name == "New script");
        Assert.Equal("new.example:4000:False", agents.LastKey);
    }

    [AvaloniaFact]
    public async Task RevertingEditsDoesNotAskToDiscardAnything()
    {
        var store = new ProfileStore(); var agents = new Agents();
        using var model = new ProfileEditorViewModel(store, new Directory(), store.Profiles[0], agents: agents);
        var original = model.Agent!.Model;
        model.Username = "Changed"; model.Agent.Model = "different";
        Assert.True(model.HasUnsavedChanges);
        model.Username = ""; model.Agent.Model = original;
        Assert.False(model.HasUnsavedChanges);
        var asked = false; model.ConfirmDiscardAsync = () => { asked = true; return Task.FromResult(false); };
        Assert.True(await model.CanCloseAsync()); Assert.False(asked);
    }

    [AvaloniaFact]
    public async Task SwitchingWorldsKeepsTheDraftUntilDiscardIsConfirmed()
    {
        var store = new ProfileStore(); var agents = new Agents();
        var original = store.Profiles[0];
        var other = original with { Id = Guid.NewGuid(), Name = "Other", Host = "other.example" };
        await store.SaveWorldAsync(other, "", false);
        using var model = new ProfileEditorViewModel(store, new Directory(), original, agents: agents);
        model.Username = "Unsaved"; model.Agent!.Model = "draft";
        model.ConfirmDiscardAsync = () => Task.FromResult(false);
        Assert.False(await model.SelectProfileAsync(other)); Assert.Equal("Unsaved", model.Username);
        Assert.Equal(original, model.SelectedProfile);
        model.ConfirmDiscardAsync = () => Task.FromResult(true);
        Assert.True(await model.SelectProfileAsync(other)); Assert.Equal("", model.Username);
        Assert.False(model.HasUnsavedChanges); Assert.Equal(0, agents.Saves);
    }

    [AvaloniaFact]
    public async Task StorageFailureLeavesTheDialogAndDraftOpenForRetry()
    {
        var store = new ProfileStore { FailSave = true }; var agents = new Agents();
        using var model = new ProfileEditorViewModel(store, new Directory(), store.Profiles[0], agents: agents);
        model.WorldName = "Changed"; model.Agent!.Model = "local-model";
        var closed = false; model.CloseRequested += () => closed = true;
        await model.SaveCommand.ExecuteAsync(null);
        Assert.True(model.HasError); Assert.False(closed); Assert.True(model.HasUnsavedChanges); Assert.Equal(0, agents.Saves);
        store.FailSave = false;
        await model.SaveCommand.ExecuteAsync(null);
        Assert.False(model.HasError); Assert.True(closed); Assert.Equal("local-model", agents.Profile.Model);
    }

    [AvaloniaFact]
    public async Task WorldPickerRestoresSelectionWhenDiscardIsDeclined()
    {
        var store = new ProfileStore(); var first = store.Profiles[0];
        var second = first with { Id = Guid.NewGuid(), Name = "Second", Host = "second.example" };
        await store.SaveWorldAsync(second, "", false);
        using var model = new ProfileEditorViewModel(store, new Directory(), first);
        var window = new ProfileDialog(model); window.Show();
        var choice = window.FindControl<ComboBox>("WorldProfileChoice")!;
        model.Username = "Unsaved";
        choice.SelectedItem = second; Dispatcher.UIThread.RunJobs();
        var confirmation = Assert.Single(window.OwnedWindows);
        confirmation.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "KeepEditing").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(first, model.SelectedProfile); Assert.Equal(first, choice.SelectedItem); Assert.Equal("Unsaved", model.Username);
        choice.SelectedItem = second; Dispatcher.UIThread.RunJobs();
        confirmation = Assert.Single(window.OwnedWindows);
        confirmation.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "DiscardChanges").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(second, model.SelectedProfile); Assert.Equal(second, choice.SelectedItem); Assert.Equal("", model.Username);
        window.Close();
    }

    private static void Capture(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR") is not { } folder) return;
        System.IO.Directory.CreateDirectory(folder);
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
        frame.Save(Path.Combine(folder, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    private sealed class ProfileStore : IWorldProfileStore
    {
        private readonly List<ConnectionProfile> _profiles = [new() { Name = "Example", Host = "mud.example" }];
        public IReadOnlyList<ConnectionProfile> Profiles => _profiles;
        public string CredentialStoreName => "Test";
        public int Saves; public bool FailSave;
        public Task SaveWorldAsync(ConnectionProfile profile, string password, bool rememberPassword)
        {
            if (FailSave) throw new IOException("Test write failure");
            profile.Validate(); Saves++; _profiles.RemoveAll(p => p.Id == profile.Id); _profiles.Add(profile); return Task.CompletedTask;
        }
        public Task RemoveWorldAsync(ConnectionProfile profile) { _profiles.RemoveAll(p => p.Id == profile.Id); return Task.CompletedTask; }
    }
    private sealed class Directory : IWorldDirectory
    {
        public Task<WorldNameSuggestion?> LookupAsync(string host, int port, CancellationToken cancellationToken = default) => Task.FromResult<WorldNameSuggestion?>(null);
    }
    private sealed class Agents : IAgentClientServices, IAgentProfileStore, IAgentProviderResolver
    {
        public AgentProfile Profile = new(); public int Saves; public int Loads; public string? LastKey;
        public IAgentProfileStore Profiles => this;
        public IAgentProviderResolver Providers => this;
        public event Action<Guid>? Saved;
        public AgentProfile Load(string key) { Loads++; return Profile; }
        public void Save(string key, AgentProfile profile) => Profile = profile;
        public IAgentModelProvider Resolve(string provider) => throw new InvalidOperationException();
        public Task<string?> ReadCredentialAsync(AgentProfile profile) => Task.FromResult<string?>(null);
        public Task<AgentProfile> SaveAsync(string key, AgentProfile profile, string newKey, bool forgetKey)
        { Saves++; LastKey = key; Profile = profile; Saved?.Invoke(profile.Id); return Task.FromResult(profile); }
    }
}
