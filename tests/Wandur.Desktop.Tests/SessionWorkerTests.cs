using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Wandur.Core.Scripting;
using Wandur.Desktop.Services;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.Tests;

public sealed class SessionWorkerTests
{
    private static MemoryScriptLibraryStore ThreeEnabledScripts(out Guid[] ids)
    {
        var store = new MemoryScriptLibraryStore();
        var first = Assert.Single(store.Load("world"));
        var definitions = new[]
        {
            first with { Name = "One", Source = "mud.on(Events.Msdp, e => mud.echo('one:' + e.variable)); mud.trigger(/^go$/, () => mud.echo('one-line'));", Enabled = true },
            new WorldScriptDefinition(Guid.NewGuid(), "Two", "mud.on(Events.Msdp, e => mud.echo('two:' + e.variable)); mud.trigger(/^go$/, () => mud.echo('two-line'));", true),
            new WorldScriptDefinition(Guid.NewGuid(), "Three", "mud.on(Events.Msdp, e => mud.echo('three:' + e.variable)); mud.trigger(/^go$/, () => mud.echo('three-line'));", true)
        };
        foreach (var definition in definitions) store.Upsert("world", definition);
        ids = definitions.Select(definition => definition.Id).ToArray();
        return store;
    }

    [AvaloniaFact]
    public async Task ALibraryWithThreeEnabledScriptsStartsOneWorkerAndFansEachEventOutOnce()
    {
        var factory = new InlineScriptFactory();
        var store = ThreeEnabledScripts(out _);
        var echoes = new List<string>();
        await using var library = new WorldScriptLibrary(factory, store, () => true, () => false, _ => Task.FromResult(true), echoes.Add,
            seedState: () => """{"gmcp":{},"msdp":{"HEALTH":"1"}}""");
        library.Configure("world", "World");
        library.RefreshState();
        await ScriptSessionTests.WaitFor(() => library.Items.All(entry => entry.Runtime.IsRunning));
        Assert.Equal(1, factory.Created);
        Assert.Equal(3, factory.Host!.Loaded);
        Assert.Equal(1, library.Worker.Starts);
        Assert.True(library.Worker.IsRunning);
        // The engines are keyed by the entries' ids.
        Assert.All(library.Items, entry => Assert.Equal(entry.Id.ToString(), entry.Runtime.Id));

        library.Publish(new("msdp", """{"variable":"HEALTH","value":"2"}"""));
        library.Feed("go\r\n");
        await ScriptSessionTests.WaitFor(() => echoes.Count == 6);
        // Every script saw both events, in the order they were published, in library order within an event.
        Assert.Equal(["one:HEALTH", "two:HEALTH", "three:HEALTH", "one-line", "two-line", "three-line"], echoes);

        // Stopping the library ends the process; the next run starts a fresh one.
        library.Stop();
        Assert.False(library.Worker.IsRunning);
        Assert.All(library.Items, entry => Assert.False(entry.Runtime.IsRunning));
        await library.ReloadAsync();
        await ScriptSessionTests.WaitFor(() => library.Items.All(entry => entry.Runtime.IsRunning));
        Assert.Equal(2, factory.Created);
    }

    [AvaloniaFact]
    public async Task ARunawayCallbackInOneScriptStopsThatScriptOnlyInsideTheSharedWorker()
    {
        var factory = new InlineScriptFactory();
        var store = new MemoryScriptLibraryStore();
        var first = Assert.Single(store.Load("world"));
        store.Upsert("world", first with { Name = "Loop", Source = "mud.trigger(/^go$/, () => { mud.echo('discard'); while (true) {} });", Enabled = true });
        store.Upsert("world", new WorldScriptDefinition(Guid.NewGuid(), "Fine", "mud.trigger(/^go$/, () => mud.echo('fine'));", true));
        var echoes = new List<string>();
        await using var library = new WorldScriptLibrary(factory, store, () => true, () => false, _ => Task.FromResult(true), echoes.Add);
        library.Configure("world", "World");
        library.RefreshState();
        await ScriptSessionTests.WaitFor(() => library.Items.All(entry => entry.Runtime.IsRunning));
        library.Feed("go\n");
        await ScriptSessionTests.WaitFor(() => echoes.Count == 1 && library.Items[0].Runtime.Error is not null);
        Assert.Equal(["fine"], echoes);
        Assert.False(library.Items[0].Runtime.IsRunning);
        Assert.True(library.Items[1].Runtime.IsRunning);
        Assert.True(library.Worker.IsRunning);
        Assert.Equal(1, factory.Created);
        library.Feed("go\n");
        await ScriptSessionTests.WaitFor(() => echoes.Count == 2);
        Assert.True(library.Items[1].Runtime.IsRunning);
    }

    [AvaloniaFact]
    public async Task AWorkerCrashMarksScriptsFailedRestartsThemAndGivesUpAfterThreeRestartsInFiveMinutes()
    {
        var factory = new InlineScriptFactory();
        var store = ThreeEnabledScripts(out _);
        var echoes = new List<string>();
        await using var library = new WorldScriptLibrary(factory, store, () => true, () => false, _ => Task.FromResult(true), echoes.Add);
        library.Configure("world", "World");
        library.RefreshState();
        await ScriptSessionTests.WaitFor(() => library.Items.All(entry => entry.Runtime.IsRunning));
        var errors = new List<string?>();
        library.Items[0].Runtime.Changed += () => { if (library.Items[0].Runtime.Error is { } error && errors.LastOrDefault() != error) errors.Add(error); };
        for (var crash = 1; crash <= 3; crash++)
        {
            var host = factory.Host!;
            host.Crash();
            await ScriptSessionTests.WaitFor(() => factory.Created == crash + 1 && library.Items.All(entry => entry.Runtime.IsRunning));
            Assert.Contains(L.Format(L.ScriptFailed, L.ScriptWorkerFailed), errors);
            Assert.False(host.IsRunning);
            Assert.Equal(3, factory.Host!.Loaded);
        }
        // The fourth failure inside the window is not recovered from; the scripts stay stopped and say why.
        factory.Host!.Crash();
        await ScriptSessionTests.WaitFor(() => library.Items.All(entry => !entry.Runtime.IsRunning && entry.Runtime.Error is not null));
        await Task.Delay(100); Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(4, factory.Created);
        Assert.All(library.Items, entry => Assert.Equal(L.Format(L.ScriptFailed, L.ScriptWorkerRestartLimit), entry.Runtime.Error));
        Assert.False(library.Worker.IsRunning);
        // The user can still start one by hand; that is a new worker, and the budget only counts failures.
        await library.SetEnabledAsync(library.Items[0], false);
        await library.SetEnabledAsync(library.Items[0], true);
        Assert.True(library.Items[0].Runtime.IsRunning, library.Items[0].Runtime.Error);
        Assert.Equal(5, factory.Created);
    }

    [AvaloniaFact]
    public async Task AScriptThatStartsLaterIsSeededWithWhatTheHostCacheHoldsNow()
    {
        var factory = new InlineScriptFactory();
        var store = new MemoryScriptLibraryStore();
        var first = Assert.Single(store.Load("world"));
        store.Upsert("world", first with { Name = "Early", Source = "mud.alias(/^read$/, () => mud.echo('early:' + mud.state.get('msdp.HEALTH')));", Enabled = true });
        var later = new WorldScriptDefinition(Guid.NewGuid(), "Later", "mud.echo('later:' + mud.state.get('msdp.HEALTH'));");
        store.Upsert("world", later);
        var seed = """{"gmcp":{},"msdp":{"HEALTH":"1"}}""";
        var echoes = new List<string>();
        await using var library = new WorldScriptLibrary(factory, store, () => true, () => false, _ => Task.FromResult(true), echoes.Add, seedState: () => seed);
        library.Configure("world", "World");
        library.RefreshState();
        await ScriptSessionTests.WaitFor(() => library.Items[0].Runtime.IsRunning);
        // The cache moves on (a value arrived while no script was loading), then a second script is enabled:
        // the new seed goes to the worker ahead of its load and reaches the running engine too.
        seed = """{"gmcp":{},"msdp":{"HEALTH":"2"}}""";
        await library.SetEnabledAsync(library.Items[1], true);
        Assert.True(library.Items[1].Runtime.IsRunning, library.Items[1].Runtime.Error);
        Assert.True(await library.HandleCommandAsync("read"));
        await ScriptSessionTests.WaitFor(() => echoes.Count == 2);
        Assert.Equal(["later:2", "early:2"], echoes);
        Assert.Equal(1, factory.Created);
    }

    /// <summary>The app's own worker path: the Wandur assembly started with --script-worker by the process factory,
    /// hosting two scripts in one process. The process is watched through the host and killed from outside to
    /// prove recovery on the real transport.</summary>
    [AvaloniaFact]
    public async Task TheRealWorkerProcessHostsEveryScriptOfTheLibraryAndIsRestartedWhenKilled()
    {
        var assembly = Path.Combine(AppContext.BaseDirectory, "Wandur.dll");
        Assert.True(File.Exists(assembly), $"Missing {assembly}");
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var factory = new ProcessScriptRuntimeFactory(dotnet, assembly);
        var store = new MemoryScriptLibraryStore();
        var first = Assert.Single(store.Load("world"));
        store.Upsert("world", first with { Name = "One", Source = "mud.trigger(/^go$/, () => mud.echo('one'));", Enabled = true });
        store.Upsert("world", new WorldScriptDefinition(Guid.NewGuid(), "Two", "mud.trigger(/^go$/, () => mud.echo('two'));", true));
        var echoes = new List<string>();
        await using var library = new WorldScriptLibrary(factory, store, () => true, () => false, _ => Task.FromResult(true), echoes.Add,
            seedState: () => """{"gmcp":{},"msdp":{}}""");
        library.Configure("world", "World");
        library.RefreshState();
        await ScriptSessionTests.WaitFor(() => library.Items.All(entry => entry.Runtime.IsRunning));
        var host = Assert.IsType<ProcessSessionScriptHost>(library.Worker.Host);
        var processId = host.ProcessId;
        Assert.NotNull(processId);
        Assert.Equal(1, library.Worker.Starts);
        library.Feed("go\n");
        await ScriptSessionTests.WaitFor(() => echoes.Count == 2);
        Assert.Equal(["one", "two"], echoes);
        if (Environment.GetEnvironmentVariable("WANDUR_HOLD_WORKER_MS") is { } hold && int.TryParse(hold, out var milliseconds))
            await Task.Delay(milliseconds);

        Process.GetProcessById(processId.Value).Kill();
        await ScriptSessionTests.WaitFor(() => library.Worker.Starts == 2 && library.Items.All(entry => entry.Runtime.IsRunning));
        var restarted = Assert.IsType<ProcessSessionScriptHost>(library.Worker.Host);
        Assert.NotEqual(processId, restarted.ProcessId);
        library.Feed("go\n");
        await ScriptSessionTests.WaitFor(() => echoes.Count == 4);
        var lastProcess = restarted.ProcessId!.Value;
        await library.DisposeAsync();
        Assert.True(SpinWait.SpinUntil(() =>
        {
            try { return Process.GetProcessById(lastProcess).HasExited; }
            catch (ArgumentException) { return true; }
        }, TimeSpan.FromSeconds(5)));
    }
}
