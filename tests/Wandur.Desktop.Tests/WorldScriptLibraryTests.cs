using Avalonia.Headless.XUnit;
using Wandur.Core.Scripting;
using Wandur.Desktop.Services;

namespace Wandur.Desktop.Tests;

public sealed class WorldScriptLibraryTests
{
    [AvaloniaFact]
    public async Task SendLimitAppliesAcrossAllScriptsInTheConnection()
    {
        var sent = new List<string>();
        await using var library = new WorldScriptLibrary(new InlineScriptFactory(), new MemoryScriptLibraryStore(), () => true, () => false,
            command => { sent.Add(command); return Task.FromResult(true); }, _ => { });
        library.Configure("world", "World");
        var first = library.Items[0]; var second = library.Add();
        first.Source = second.Source = "for (let i = 0; i < 11; i++) mud.send('look');";
        await library.SaveAsync(first); await library.SaveAsync(second);
        await library.SetEnabledAsync(first, true); await library.SetEnabledAsync(second, true);
        Assert.Equal(20, sent.Count);
        Assert.True(first.Runtime.IsRunning);
        Assert.False(second.Runtime.IsRunning);
        Assert.NotNull(second.Runtime.Error);
    }

    [AvaloniaFact]
    public async Task EnabledScriptsWaitForPublicConnectionAndErrorsDoNotRetryOnRefresh()
    {
        var store = new MemoryScriptLibraryStore();
        var first = Assert.Single(store.Load("world"));
        store.Upsert("world", first with { Source = "throw new Error('failed');", Enabled = true });
        var other = new WorldScriptDefinition(Guid.NewGuid(), "Other", "mud.echo('started');", true);
        store.Upsert("world", other);
        var connected = false; var privacy = true; var echoes = new List<string>();
        await using var library = new WorldScriptLibrary(new InlineScriptFactory(), store, () => connected, () => privacy,
            _ => Task.FromResult(true), echoes.Add);
        library.Configure("world", "World");
        library.RefreshState();
        Assert.All(library.Items, e => Assert.False(e.Runtime.IsRunning));
        connected = true; library.RefreshState();
        Assert.All(library.Items, e => Assert.False(e.Runtime.IsRunning));
        privacy = false; library.RefreshState();
        await ScriptSessionTests.WaitFor(() => library.Items[0].Runtime.Error is not null && library.Items[1].Runtime.IsRunning);
        Assert.Contains("failed", library.Items[0].Runtime.Error);
        Assert.Single(echoes);
        for (var i = 0; i < 5; i++) library.RefreshState();
        Assert.False(library.Items[0].Runtime.IsBusy);
        Assert.Single(echoes);
        library.Stop(); connected = false;
        library.Configure("world", "World");
        connected = true; library.RefreshState();
        await ScriptSessionTests.WaitFor(() => echoes.Count == 2);
    }

    [AvaloniaFact]
    public async Task SavesReloadEnabledCodeWhileDraftEditsAndEnableTogglesDoNotSaveDrafts()
    {
        var store = new MemoryScriptLibraryStore(); var echoes = new List<string>();
        await using var library = new WorldScriptLibrary(new InlineScriptFactory(), store, () => true, () => false,
            _ => Task.FromResult(true), echoes.Add);
        library.Configure("world", "World");
        var entry = library.Items[0];
        Assert.False(entry.Enabled);
        entry.Source = "mud.echo('saved');";
        await library.SaveAsync(entry);
        entry.Source = "mud.echo('draft');";
        await library.SetEnabledAsync(entry, true);
        Assert.Equal(new[] { "saved" }, echoes);
        Assert.Equal("mud.echo('saved');", store.Load("world")[0].Source);
        await library.SaveAsync(entry);
        Assert.Equal(new[] { "saved", "draft" }, echoes);
        var added = library.Add();
        Assert.False(added.Enabled);
        await library.DeleteAsync(entry);
        Assert.False(entry.Runtime.IsRunning);
        Assert.Single(library.Items);
        Assert.Single(store.Load("world"));
    }

    [AvaloniaFact]
    public async Task AliasesUseLibraryOrderAndEveryRunningScriptReceivesPublicLines()
    {
        var store = new MemoryScriptLibraryStore(); var echoes = new List<string>();
        await using var library = new WorldScriptLibrary(new InlineScriptFactory(), store, () => true, () => false,
            _ => Task.FromResult(true), echoes.Add);
        library.Configure("world", "World");
        var first = library.Items[0]; var second = library.Add();
        first.Source = "mud.alias(/^hello$/, () => mud.echo('first')); mud.trigger(/^line$/, () => mud.echo('line1'));";
        second.Source = "mud.alias(/^hello$/, () => mud.echo('second')); mud.trigger(/^line$/, () => mud.echo('line2'));";
        await library.SaveAsync(first); await library.SaveAsync(second);
        await library.SetEnabledAsync(first, true); await library.SetEnabledAsync(second, true);
        Assert.True(await library.HandleCommandAsync("hello"));
        Assert.Equal(new[] { "first" }, echoes);
        library.Feed("line\n");
        await ScriptSessionTests.WaitFor(() => echoes.Count == 3);
        Assert.Contains("line1", echoes); Assert.Contains("line2", echoes);
    }
}

internal sealed class MemoryScriptLibraryStore : IWorldScriptLibraryStore
{
    private readonly Dictionary<string, List<WorldScriptDefinition>> _worlds = [];
    public IReadOnlyList<WorldScriptDefinition> Load(string key)
    {
        if (!_worlds.TryGetValue(key, out var scripts)) _worlds[key] = scripts = [new(Guid.NewGuid(), "Script", ScriptExamples.Starter)];
        return scripts.ToArray();
    }
    public void Upsert(string key, WorldScriptDefinition script)
    {
        var scripts = Load(key).ToList(); var index = scripts.FindIndex(s => s.Id == script.Id);
        if (index < 0) scripts.Add(script); else scripts[index] = script;
        _worlds[key] = scripts;
    }
    public void Delete(string key, Guid id) => _worlds[key] = Load(key).Where(s => s.Id != id).ToList();
}
