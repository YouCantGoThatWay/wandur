using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Wandur.Core.Scripting;
using Wandur.Desktop.Services;

namespace Wandur.Desktop.Tests;

public sealed class ScriptSessionTests
{
    /// <summary>A pack script boots a worker process while the world is still sending its first values;
    /// what is published during that boot must reach the script once it runs, not vanish.</summary>
    [AvaloniaFact]
    public async Task EventsPublishedWhileTheWorkerBootsAreDeliveredOnceTheScriptRuns()
    {
        var factory = new RecordingScriptFactory { BlockLoad = true };
        var privateInput = false;
        await using var scripts = new SessionScripts(factory, new MemoryScriptStore(), () => true, () => privateInput,
            _ => Task.FromResult(true), _ => { }, seedState: () => "{\"gmcp\":{},\"msdp\":{}}");
        scripts.Configure("world", "Test world");
        var run = scripts.RunAsync();
        await WaitFor(() => factory.Runtime is { } runtime && runtime.Events.Count == 1);
        Assert.True(scripts.IsBusy);
        Assert.False(scripts.IsRunning);
        scripts.Publish(new("msdp", "{\"variable\":\"LEVELCOMBAT\",\"value\":\"4\"}"));
        scripts.Publish(new("msdp", "{\"variable\":\"HEALTH\",\"value\":\"1000\"}"));
        // A long login replay, well past the 128 slots of the live queue, must not count as an overflow.
        for (var i = 0; i < 300; i++) scripts.Publish(new("msdp", "{\"variable\":\"VAR" + i + "\",\"value\":\"" + i + "\"}"));
        scripts.Publish(new("msdp", "{\"variable\":\"LEVELCOMBAT\",\"value\":\"5\"}"));
        factory.Runtime!.Loaded.SetResult(true);
        await run;
        Assert.True(scripts.IsRunning);
        await WaitFor(() => factory.Runtime.Events.Count(e => e.Kind == "msdp") == 302);
        Assert.True(scripts.IsRunning, scripts.Error);
        Assert.Equal("state", factory.Runtime.Events[0].Kind);
        // The repeated variable is delivered once, in its first position, with its latest value.
        var combat = Assert.Single(factory.Runtime.Events, e => e.Kind == "msdp" && e.Text.Contains("LEVELCOMBAT", StringComparison.Ordinal));
        Assert.Contains("\"5\"", combat.Text, StringComparison.Ordinal);
        Assert.Equal(combat, factory.Runtime.Events.First(e => e.Kind == "msdp"));

        // A privacy change during the boot discards what was buffered: the host replays the cache once play is public.
        scripts.Stop();
        factory = new RecordingScriptFactory { BlockLoad = true };
        await using var second = new SessionScripts(factory, new MemoryScriptStore(), () => true, () => privateInput,
            _ => Task.FromResult(true), _ => { });
        second.Configure("world", "Test world");
        var secondRun = second.RunAsync();
        await WaitFor(() => factory.Runtime is not null && second.IsBusy);
        second.Publish(new("msdp", "{\"variable\":\"MANA\",\"value\":\"1\"}"));
        privateInput = true; second.RefreshState(); privateInput = false; second.RefreshState();
        factory.Runtime!.Loaded.SetResult(true);
        await secondRun;
        await Task.Delay(100);
        Assert.DoesNotContain(factory.Runtime.Events, e => e.Kind == "msdp");
    }

    [AvaloniaFact]
    public async Task FragmentedServerLinesAndPrivateInputAreIsolatedFromScripts()
    {
        var factory = new RecordingScriptFactory();
        var privateInput = false;
        var sends = new List<string>();
        await using var scripts = new SessionScripts(factory, new MemoryScriptStore(), () => true, () => privateInput,
            text => { sends.Add(text); return Task.FromResult(true); }, _ => { });
        scripts.Configure("world", "Test world");
        await scripts.RunAsync();
        scripts.Feed("\u001b[32mHel"); scripts.Feed("lo\u001b[0m\n");
        await WaitFor(() => factory.Runtime!.Events.Count > 0);
        Assert.Equal("Hello", factory.Runtime!.Events[0].Text);
        privateInput = true; scripts.RefreshState();
        scripts.Feed("private text\n");
        Assert.False(await scripts.HandleCommandAsync("secret"));
        privateInput = false; scripts.RefreshState();
        scripts.Feed("Public\n");
        await WaitFor(() => factory.Runtime.Events.Count == 2);
        Assert.DoesNotContain(factory.Runtime.Events, e => e.Text.Contains("private") || e.Text == "secret");
        scripts.Stop(); scripts.Feed("stopped\n");
        Assert.False(scripts.IsRunning);
        Assert.Empty(sends);
    }

    [AvaloniaFact]
    public async Task StopDiscardsAnInFlightSendAndSavedSourceDoesNotAutoRun()
    {
        var factory = new RecordingScriptFactory { BlockDispatch = true };
        var store = new MemoryScriptStore(); var sends = new List<string>();
        await using var scripts = new SessionScripts(factory, store, () => true, () => false,
            text => { sends.Add(text); return Task.FromResult(true); }, _ => { });
        scripts.Configure("world", "World");
        scripts.Source = "custom source"; scripts.Save();
        Assert.Equal("custom source", store.Load("world"));
        Assert.False(scripts.IsRunning);
        await scripts.RunAsync();
        var command = scripts.HandleCommandAsync("heal");
        await WaitFor(() => factory.Runtime!.Events.Count > 0);
        scripts.Stop();
        factory.Runtime!.Release.TrySetResult(new(true, [new("send", "cast heal")]));
        Assert.True(await command); // Never send an unresolved alias as a literal command after Stop.
        Assert.Empty(sends);
        scripts.Configure("other", "Other");
        Assert.False(scripts.IsRunning);
    }

    [AvaloniaFact]
    public async Task EnteringPrivateModeInvalidatesEffectsEvenIfPrivacyEndsBeforeTheWorkerReplies()
    {
        var factory = new RecordingScriptFactory { BlockDispatch = true };
        var privateInput = false;
        var sends = new List<string>();
        await using var scripts = new SessionScripts(factory, new MemoryScriptStore(), () => true, () => privateInput,
            text => { sends.Add(text); return Task.FromResult(true); }, _ => { });
        scripts.Configure("world", "World");
        await scripts.RunAsync();
        var command = scripts.HandleCommandAsync("heal");
        await WaitFor(() => factory.Runtime!.Events.Count > 0);
        privateInput = true; scripts.RefreshState();
        privateInput = false; scripts.RefreshState();
        factory.Runtime!.Release.TrySetResult(new(true, [new("send", "cast heal")]));
        Assert.True(await command);
        Assert.Empty(sends);
        Assert.True(scripts.IsRunning);
    }

    [AvaloniaFact]
    public async Task WorkerErrorsRemainVisibleAcrossAPrivacyTransition()
    {
        var factory = new RecordingScriptFactory { BlockDispatch = true };
        var privateInput = false;
        await using var scripts = new SessionScripts(factory, new MemoryScriptStore(), () => true, () => privateInput,
            _ => Task.FromResult(true), _ => { });
        scripts.Configure("world", "World");
        await scripts.RunAsync();
        var command = scripts.HandleCommandAsync("heal");
        await WaitFor(() => factory.Runtime!.Events.Count > 0);
        privateInput = true; scripts.RefreshState();
        privateInput = false; scripts.RefreshState();
        factory.Runtime!.Release.TrySetResult(new(false, [], "Script failed deliberately"));
        Assert.True(await command);
        Assert.False(scripts.IsRunning);
        Assert.Contains("Script failed deliberately", scripts.Error);
    }

    internal static async Task WaitFor(Func<bool> predicate)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!predicate() && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.True(predicate());
    }
}

internal sealed class MemoryScriptStore : IWorldScriptStore
{
    private readonly Dictionary<string,string> _sources = [];
    public string? Load(string key) => _sources.GetValueOrDefault(key);
    public void Save(string key, string source) => _sources[key] = source;
}

internal sealed class RecordingScriptFactory : IScriptRuntimeFactory
{
    public bool BlockDispatch { get; init; }
    public bool BlockLoad { get; init; }
    public RecordingScriptRuntime? Runtime { get; private set; }
    public IScriptRuntime Create() => Runtime = new(BlockDispatch, BlockLoad);
}

internal sealed class RecordingScriptRuntime(bool blockDispatch, bool blockLoad = false) : IScriptRuntime
{
    public List<ScriptEvent> Events { get; } = [];
    public TaskCompletionSource<ScriptResult> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool> Loaded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool IsRunning { get; private set; }
    public bool RestrictedSend { get; private set; }
    public async Task<ScriptResult> LoadAsync(string source, CancellationToken cancellationToken = default, bool restrictedSend = false)
    {
        if (blockLoad) await Loaded.Task;
        IsRunning = true; RestrictedSend = restrictedSend; return new ScriptResult(false, []);
    }
    public Task<ScriptResult> DispatchAsync(ScriptEvent input, CancellationToken cancellationToken = default)
    {
        lock (Events) Events.Add(input);
        return blockDispatch ? Release.Task : Task.FromResult(new ScriptResult(false, []));
    }
    public void Stop() => IsRunning = false;
    public ValueTask DisposeAsync() { Stop(); return ValueTask.CompletedTask; }
}
