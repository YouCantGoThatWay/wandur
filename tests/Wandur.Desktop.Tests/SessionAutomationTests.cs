using Avalonia.Headless.XUnit;
using Wandur.Desktop.Services;
using Wandur.Desktop.ViewModels;

namespace Wandur.Desktop.Tests;

public sealed class SessionAutomationTests
{
    [AvaloniaFact]
    public async Task MacroMasterSwitchPreservesDefaultsAndDoesNotStopScripts()
    {
        var store = new MemoryScriptLibraryStore();
        await using var library = new WorldScriptLibrary(new InlineScriptFactory(), store, () => true, () => false, _ => Task.FromResult(true), _ => { });
        library.Configure("world", "World");
        var script = library.Items[0];
        var macro = library.AddMacro(); var disabled = library.AddMacro();
        await library.SetEnabledAsync(script, true); await library.SetEnabledAsync(macro, true);
        using var model = new SessionAutomationViewModel(library);
        Assert.Single(model.Scripts); Assert.True(model.HasMacros);
        await library.SetMacrosEnabledAsync(false);
        library.RefreshState(); await library.ReloadAsync();
        await ScriptSessionTests.WaitFor(() => library.Items.Single(e => e.Id == script.Id).Runtime.IsRunning);
        Assert.True(library.Items.Single(e => e.Id == script.Id).Runtime.IsRunning);
        Assert.False(library.Items.Single(e => e.Id == macro.Id).Runtime.IsRunning);
        Assert.True(store.Load("world").Single(e => e.Id == macro.Id).Enabled);
        await library.SetMacrosEnabledAsync(true);
        Assert.True(library.Items.Single(e => e.Id == macro.Id).Runtime.IsRunning);
        Assert.False(library.Items.Single(e => e.Id == disabled.Id).Runtime.IsRunning);
    }

    [AvaloniaFact]
    public async Task ReleasingAgentControlDoesNotReplayTopLevelScriptCommands()
    {
        var store = new MemoryScriptLibraryStore();
        store.Upsert("world", store.Load("world")[0] with { Source = "mud.send('look');", Enabled = true });
        var commands = new List<string>();
        await using var library = new WorldScriptLibrary(new InlineScriptFactory(), store, () => true, () => false,
            command => { commands.Add(command); return Task.FromResult(true); }, _ => { });
        library.Configure("world", "World"); library.RefreshState();
        await ScriptSessionTests.WaitFor(() => commands.Count == 1);
        library.SetSuspended(true); library.SetSuspended(false); library.RefreshState();
        await Task.Delay(50); Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Single(commands); Assert.True(library.Items[0].Enabled); Assert.False(library.Items[0].Runtime.IsRunning);
        await library.ReloadAsync(); await ScriptSessionTests.WaitFor(() => commands.Count == 2);
    }

    [AvaloniaFact]
    public async Task ToolbarMenuShowsLiveSwitchesAndOpensConfiguration()
    {
        var store = new MemoryScriptLibraryStore();
        await using var library = new WorldScriptLibrary(new InlineScriptFactory(), store, () => false, () => false, _ => Task.FromResult(true), _ => { });
        library.Configure("world", "World");
        using var model = new SessionAutomationViewModel(library);
        int? selectedSection = null;
        var button = new Wandur.Desktop.Views.SessionScriptsButton(model, section => selectedSection = section);
        var window = new Avalonia.Controls.Window { Width = 500, Height = 500, Content = button };
        try
        {
            window.Show(); button.Flyout!.ShowAt(button); Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var content = Assert.IsType<Avalonia.Controls.StackPanel>(Assert.IsType<Avalonia.Controls.ScrollViewer>(Assert.IsType<Avalonia.Controls.Flyout>(button.Flyout).Content).Content);
            var toggle = Assert.Single(Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(content).OfType<Avalonia.Controls.CheckBox>());
            toggle.IsChecked = true;
            await ScriptSessionTests.WaitFor(() => library.Items[0].Enabled);
            Assert.False(store.Load("world")[0].Enabled);
            var edit = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(content).OfType<Avalonia.Controls.Button>().Single(b => b.Name == "EditSessionAutomation");
            edit.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            Assert.Equal(2, selectedSection);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task LiveSwitchesAreSessionLocalAndReloadAdoptsSavedDefinitions()
    {
        var store = new MemoryScriptLibraryStore();
        var saved = store.Load("world")[0] with { Source = "mud.alias(/^h$/, () => mud.send('look'));", Enabled = true };
        store.Upsert("world", saved);
        var sentFirst = new List<string>(); var sentSecond = new List<string>();
        await using var first = new WorldScriptLibrary(new InlineScriptFactory(), store, () => true, () => false,
            command => { sentFirst.Add(command); return Task.FromResult(true); }, _ => { });
        await using var second = new WorldScriptLibrary(new InlineScriptFactory(), store, () => true, () => false,
            command => { sentSecond.Add(command); return Task.FromResult(true); }, _ => { });
        first.Configure("world", "World"); second.Configure("world", "World");
        first.RefreshState(); second.RefreshState();
        await ScriptSessionTests.WaitFor(() => first.Items[0].Runtime.IsRunning && second.Items[0].Runtime.IsRunning);
        using var controls = new SessionAutomationViewModel(first);
        await controls.Items[0].EnableCommand.ExecuteAsync(false);
        Assert.True(store.Load("world")[0].Enabled);
        Assert.False(first.Items[0].Runtime.IsRunning); Assert.True(second.Items[0].Runtime.IsRunning);
        store.Upsert("world", saved with { Source = "mud.alias(/^h$/, () => mud.send('score'));" });
        await controls.ReloadCommand.ExecuteAsync(null);
        await ScriptSessionTests.WaitFor(() => first.Items[0].Runtime.IsRunning);
        Assert.True(await first.HandleCommandAsync("h")); Assert.True(await second.HandleCommandAsync("h"));
        Assert.Equal("score", Assert.Single(sentFirst)); Assert.Equal("look", Assert.Single(sentSecond));
        await controls.StopAllCommand.ExecuteAsync(null);
        Assert.False(first.Items[0].Runtime.IsRunning); Assert.True(store.Load("world")[0].Enabled);
    }
}
