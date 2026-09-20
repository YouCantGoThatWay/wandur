using System.Threading.Channels;
using Avalonia.Threading;
using Wandur.Core.Scripting;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.Services;

/// <summary>The session's one script worker: a host process started when the first script loads, the single
/// ordered queue every script request goes through, and the restart policy when the process dies.
/// Loads, stops and events share the queue, so an event published after a script asked to run is dispatched
/// after that script's engine exists and reaches it; there is no boot window to buffer for. Each event is
/// sent to the worker once, with the ids of every script that should see it, and fanned out there.
/// Mutable state belongs to the UI thread; only the pump runs in the background and it touches the scripts
/// through the dispatcher.</summary>
public sealed class SessionScriptWorker : IAsyncDisposable
{
    /// <summary>Events waiting for the worker before every running script is stopped with an overflow error.</summary>
    public const int MaximumQueuedEvents = 1024;
    public const int MaximumRestarts = 3;
    public static readonly TimeSpan RestartWindow = TimeSpan.FromMinutes(5);

    private enum WorkKind { Load, Stop, Event }
    private sealed record Work(WorkKind Kind, SessionScripts? Script, long Generation, long Privacy, ScriptEvent? Input = null,
        string? Source = null, bool Restricted = false, TaskCompletionSource<bool>? Completion = null);

    private readonly IScriptRuntimeFactory _factory;
    private readonly Func<string?>? _seedState;
    private readonly List<SessionScripts> _scripts = [];
    private readonly ScriptLineBuffer _lines = new();
    private readonly Channel<Work> _queue = Channel.CreateUnbounded<Work>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Queue<long> _failures = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _hostLock = new();
    private ISessionScriptHost? _host;
    private string? _seed;
    private int _pendingEvents;
    private bool _pumping;
    private int _starts;
    private bool _wasPrivate;
    private bool _disposed;

    public SessionScriptWorker(IScriptRuntimeFactory factory, Func<string?>? seedState = null)
    {
        _factory = factory; _seedState = seedState;
    }

    /// <summary>Moves whenever the session's privacy flips; work queued under an older epoch is not delivered.</summary>
    public long PrivacyEpoch { get; private set; }
    /// <summary>How many host processes this session has started, including restarts after a failure.</summary>
    public int Starts => Volatile.Read(ref _starts);
    /// <summary>The worker process is alive.</summary>
    public bool IsRunning { get { lock (_hostLock) return _host?.IsRunning == true; } }
    /// <summary>The current host, for tests that need to reach the process.</summary>
    public ISessionScriptHost? Host { get { lock (_hostLock) return _host; } }
    /// <summary>The worker died: the scripts it was running, already marked failed, and whether they are re-run.</summary>
    public event Action<IReadOnlyList<SessionScripts>, bool>? Crashed;

    internal void Attach(SessionScripts script) { if (!_scripts.Contains(script)) _scripts.Add(script); }
    internal void Detach(SessionScripts script) => _scripts.Remove(script);

    public void RefreshPrivacy(bool isPrivate)
    {
        if (_wasPrivate == isPrivate) return;
        _wasPrivate = isPrivate;
        PrivacyEpoch++;
        _lines.Clear();
    }

    public void DiscardPartialLine() => _lines.Clear();

    /// <summary>Completed server lines, one event each, to every running script.</summary>
    public void Feed(string text)
    {
        foreach (var line in _lines.Feed(text))
            if (!Publish(new("line", line))) break;
    }

    /// <summary>One event to every script that is running, or loading, when the worker reaches it.</summary>
    public bool Publish(ScriptEvent input) => Enqueue(new(WorkKind.Event, null, 0, PrivacyEpoch, input));

    internal bool Publish(SessionScripts script, long generation, ScriptEvent input, TaskCompletionSource<bool>? completion = null)
        => Enqueue(new(WorkKind.Event, script, generation, PrivacyEpoch, input, Completion: completion));

    internal void Load(SessionScripts script, long generation, string source, bool restricted, TaskCompletionSource<bool> completion)
    {
        if (!Enqueue(new(WorkKind.Load, script, generation, PrivacyEpoch, Source: source, Restricted: restricted, Completion: completion)))
            completion.TrySetResult(true);
    }

    internal void Stop(SessionScripts script) => Enqueue(new(WorkKind.Stop, script, 0, PrivacyEpoch));

    private bool Enqueue(Work work)
    {
        if (_disposed) { work.Completion?.TrySetResult(true); return false; }
        if (work.Kind == WorkKind.Event)
        {
            if (Volatile.Read(ref _pendingEvents) >= MaximumQueuedEvents)
            {
                work.Completion?.TrySetResult(true);
                foreach (var script in _scripts.ToArray()) if (script.IsRunning || script.IsBusy) script.Fail(L.ScriptQueueOverflow);
                return false;
            }
            Interlocked.Increment(ref _pendingEvents);
        }
        if (!_pumping) { _pumping = true; _ = Task.Run(PumpAsync); }
        if (_queue.Writer.TryWrite(work)) return true;
        if (work.Kind == WorkKind.Event) Interlocked.Decrement(ref _pendingEvents);
        work.Completion?.TrySetResult(true);
        return false;
    }

    /// <summary>Ends the worker process. Scripts are stopped by their owner; the queue and the pump stay for the next start.</summary>
    public void Shutdown()
    {
        ISessionScriptHost? host;
        lock (_hostLock) { host = _host; _host = null; _seed = null; }
        if (host is null) return;
        host.Stop();
        _ = DisposeHostAsync(host);
    }

    private static async Task DisposeHostAsync(ISessionScriptHost host)
    {
        try { await host.DisposeAsync(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* Stop already invalidated every pending effect. */ }
    }

    private ISessionScriptHost EnsureHost()
    {
        lock (_hostLock)
        {
            if (_host is not null) return _host;
            var host = _factory.Create();
            host.Failed += reason => Dispatcher.UIThread.Post(() => OnHostFailed(host, reason));
            _host = host; _seed = null;
            Interlocked.Increment(ref _starts);
            return host;
        }
    }

    private async Task PumpAsync()
    {
        var token = _lifetime.Token;
        try
        {
            await foreach (var work in _queue.Reader.ReadAllAsync(token))
            {
                if (work.Kind == WorkKind.Event) Interlocked.Decrement(ref _pendingEvents);
                try
                {
                    switch (work.Kind)
                    {
                        case WorkKind.Load: await LoadAsync(work, token); break;
                        case WorkKind.Stop: if (Host is { } host && work.Script is not null) await host.StopAsync(work.Script.Id, token); break;
                        case WorkKind.Event: await DispatchAsync(work, token); break;
                    }
                    work.Completion?.TrySetResult(true);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { work.Completion?.TrySetResult(true); throw; }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    work.Completion?.TrySetResult(true);
                    // The host in use is the current one: a deliberate shutdown drops it first, and only the pump
                    // creates one. Nothing in hand means the failure happened before a worker was started.
                    var failed = Host;
                    var reason = ex is OperationCanceledException ? L.ScriptWorkerFailed : ex.Message;
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (failed is not null) OnHostFailed(failed, reason);
                        else if (work.Kind == WorkKind.Load && work.Script is { } script && script.IsLoading(work.Generation)) script.Fail(reason);
                    });
                }
                finally
                {
                    if (work.Input?.Kind == "tick" && work.Script is { } ticked)
                        await Dispatcher.UIThread.InvokeAsync(() => ticked.TickDone(work.Generation));
                }
            }
        }
        catch (OperationCanceledException) { }
        while (_queue.Reader.TryRead(out var pending)) pending.Completion?.TrySetResult(true);
    }

    private async Task LoadAsync(Work work, CancellationToken token)
    {
        var script = work.Script!;
        var (valid, seed) = await Dispatcher.UIThread.InvokeAsync(() => (script.IsLoading(work.Generation), _seedState?.Invoke()));
        if (!valid) return;
        var host = EnsureHost();
        // The seed goes out once per worker and again only when the host cache has moved since, so a script that
        // starts later gets the values the world sent in between; a running engine takes it through the same
        // no-callback path and keeps what it has.
        if (seed is not null && seed != _seed)
        {
            var refused = await host.SeedAsync(seed, token);
            _seed = seed;
            if (refused.Count > 0)
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    foreach (var result in refused)
                        _scripts.FirstOrDefault(s => s.Id == result.Id)?.Fail(result.Error ?? L.ScriptWorkerFailed);
                });
        }
        var result = await host.LoadAsync(script.Id, work.Source!, work.Restricted, token);
        await Dispatcher.UIThread.InvokeAsync(() => script.CompleteLoadAsync(result, work.Generation, work.Privacy));
    }

    private async Task DispatchAsync(Work work, CancellationToken token)
    {
        var targets = await Dispatcher.UIThread.InvokeAsync(() => Targets(work));
        if (targets.Count == 0) return;
        var host = Host;
        if (host is null) return;
        var ids = targets.Select(target => target.Script.Id).ToArray();
        var results = await host.DispatchAsync(ids, work.Input!, token);
        var handled = await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var any = false;
            for (var i = 0; i < targets.Count && i < results.Count; i++)
            {
                var (script, generation) = targets[i];
                var applied = await script.ApplyAsync(results[i], generation, work.Privacy);
                // A command that could not be applied any more is still consumed, never sent as literal text.
                if (!applied || results[i].Handled) any = true;
            }
            return any;
        });
        work.Completion?.TrySetResult(handled);
    }

    private List<(SessionScripts Script, long Generation)> Targets(Work work)
    {
        var targets = new List<(SessionScripts, long)>();
        if (work.Privacy != PrivacyEpoch) return targets;
        if (work.Script is { } script)
        {
            if (script.Valid(work.Generation, work.Privacy)) targets.Add((script, work.Generation));
            return targets;
        }
        foreach (var candidate in _scripts)
            if (candidate.Valid(candidate.Generation, work.Privacy)) targets.Add((candidate, candidate.Generation));
        return targets;
    }

    /// <summary>The process is gone. Every script it hosted is marked failed; unless the worker has already been
    /// restarted three times in five minutes the owner is asked to run them again, on a fresh process.</summary>
    private void OnHostFailed(ISessionScriptHost host, string reason)
    {
        lock (_hostLock)
        {
            if (!ReferenceEquals(host, _host)) return;
            _host = null; _seed = null;
        }
        host.Stop();
        _ = DisposeHostAsync(host);
        if (_disposed) return;
        var now = Environment.TickCount64;
        while (_failures.TryPeek(out var first) && now - first >= RestartWindow.TotalMilliseconds) _failures.Dequeue();
        _failures.Enqueue(now);
        var restart = _failures.Count <= MaximumRestarts;
        var failed = _scripts.Where(script => script.IsRunning || script.IsBusy).ToArray();
        foreach (var script in failed) script.Fail(restart ? L.ScriptWorkerFailed : L.ScriptWorkerRestartLimit);
        Crashed?.Invoke(failed, restart);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _queue.Writer.TryComplete();
        while (_queue.Reader.TryRead(out var pending)) pending.Completion?.TrySetResult(true);
        ISessionScriptHost? host;
        lock (_hostLock) { host = _host; _host = null; }
        if (host is null) return;
        host.Stop();
        await DisposeHostAsync(host);
    }
}
