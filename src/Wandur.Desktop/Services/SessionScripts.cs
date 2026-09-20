using System.Diagnostics;
using System.Text;
using Wandur.Core.Scripting;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.Services;

/// <summary>One script of a session: its source, its status and log, and the effects of its results. The engine
/// lives in the session's worker under this script's id; every request reaches it through the worker's ordered
/// queue. Mutable state and effects belong to the UI dispatcher.</summary>
public sealed class SessionScripts : IAsyncDisposable
{
    private readonly SessionScriptWorker _worker;
    private readonly bool _ownsWorker;
    private readonly IWorldScriptStore _store;
    private readonly Func<bool> _canRun;
    private readonly Func<bool> _isPrivate;
    private readonly Func<string, Task<bool>> _send;
    private readonly Action<string> _echo;
    private readonly Action<ScriptPanelAction>? _panels;
    private readonly Func<bool>? _restrictedSend;
    private readonly Action<string>? _report;
    private readonly Stopwatch _clock = new();
    private readonly Queue<long> _sentAt = new();
    private long _generation;
    private bool _loaded;
    private bool _tickPending;
    private long _lastTick;
    private bool _disposed;
    private string? _worldKey;
    private string _source = "";
    public event Action? Changed;
    public string Source { get => _source; set { value ??= ""; if (_source == value) return; _source = value; Changed?.Invoke(); } }
    public string WorldName { get; private set; } = "";
    public string Log { get; private set; } = "";
    public string? Error { get; private set; }
    public bool IsRunning { get; private set; }
    public bool IsBusy { get; private set; }
    public bool IsPaused => IsRunning && _isPrivate();
    public bool CanRun => !_disposed && !IsBusy && _canRun() && !_isPrivate();
    /// <summary>The id the engine is known by inside the session's worker.</summary>
    public string Id { get; }
    internal long Generation => _generation;

    /// <summary>A script with a worker of its own, for a standalone editor or a test.</summary>
    public SessionScripts(
        IScriptRuntimeFactory runtimeFactory,
        IWorldScriptStore store,
        Func<bool> canRun,
        Func<bool> isPrivate,
        Func<string, Task<bool>> send,
        Action<string> echo,
        Action<ScriptPanelAction>? panels = null,
        Func<bool>? restrictedSend = null,
        Func<string?>? seedState = null,
        Action<string>? report = null)
        : this(new SessionScriptWorker(runtimeFactory, seedState), Guid.NewGuid().ToString(), store, canRun, isPrivate, send, echo, panels, restrictedSend, report)
    {
        _ownsWorker = true;
    }

    /// <summary>A script sharing the session's worker, which is how a library creates its entries.</summary>
    internal SessionScripts(
        SessionScriptWorker worker,
        string id,
        IWorldScriptStore store,
        Func<bool> canRun,
        Func<bool> isPrivate,
        Func<string, Task<bool>> send,
        Action<string> echo,
        Action<ScriptPanelAction>? panels = null,
        Func<bool>? restrictedSend = null,
        Action<string>? report = null)
    {
        _worker = worker; Id = id; _store = store; _canRun = canRun; _isPrivate = isPrivate; _send = send; _echo = echo;
        _panels = panels; _restrictedSend = restrictedSend; _report = report;
        _worker.Attach(this);
    }

    public void Configure(string worldKey, string worldName)
    {
        Stop();
        WorldName = worldName;
        if (_worldKey != worldKey)
        {
            _worldKey = worldKey;
            Error = null;
            try { Source = _store.Load(worldKey) ?? ScriptExamples.Starter; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Source = ""; Error = L.Format(L.ScriptLoadFailed, ex.Message); }
        }
        Changed?.Invoke();
    }

    public void Save()
    {
        if (_worldKey is null || _disposed) return;
        try { _store.Save(_worldKey, Source); Error = null; AppendLog(L.ScriptSaved); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { Error = L.Format(L.ScriptSaveFailed, ex.Message); Changed?.Invoke(); }
    }

    /// <summary>Queues the load behind everything published so far and returns once the engine has run the
    /// source, or failed. Events published meanwhile sit behind the load in the same queue and reach the engine
    /// once it exists, and the seed is taken when the load is dispatched, so nothing falls between the two.</summary>
    public async Task RunAsync()
    {
        if (!CanRun) { Error = _isPrivate() ? L.ScriptPrivatePause : L.ScriptNeedConnection; Changed?.Invoke(); return; }
        Stop();
        Error = null; Log = "";
        if (Encoding.UTF8.GetByteCount(Source) > WorldScriptStore.MaximumBytes)
        { Error = L.ScriptSourceTooLarge; Changed?.Invoke(); return; }
        var generation = _generation;
        _worker.RefreshPrivacy(_isPrivate());
        IsBusy = true; _loaded = true; Changed?.Invoke();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _worker.Load(this, generation, Source, _restrictedSend?.Invoke() == true, completion);
        await completion.Task;
        if (generation == _generation && IsBusy) { IsBusy = false; if (!IsRunning && Error is null) Error = L.Format(L.ScriptFailed, L.ScriptWorkerFailed); Changed?.Invoke(); }
    }

    public void Stop()
    {
        _generation++;
        IsRunning = IsBusy = false;
        _clock.Stop(); _tickPending = false;
        if (_loaded) { _loaded = false; _worker.Stop(this); }
        Changed?.Invoke();
    }

    public void RefreshState()
    {
        _worker.RefreshPrivacy(_isPrivate());
        Changed?.Invoke();
    }

    public void DiscardPartialLine() => _worker.DiscardPartialLine();

    /// <summary>Server text for this script's session. Lines go to every running script of the worker; a script
    /// that is not running, or a private interval, discards the unfinished line.</summary>
    public void Feed(string text)
    {
        if (!IsRunning || _isPrivate()) { _worker.DiscardPartialLine(); return; }
        _worker.Feed(text);
    }

    public void Publish(ScriptEvent input)
    {
        if (_isPrivate() || (!IsRunning && !IsBusy)) return;
        _worker.Publish(this, _generation, input);
    }

    public Task<bool> HandleCommandAsync(string command)
    {
        if (!IsRunning || _isPrivate()) return Task.FromResult(false);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _worker.Publish(this, _generation, new("command", command), completion);
        return completion.Task;
    }

    public void Tick()
    {
        if (!IsRunning || _isPrivate() || _tickPending || _clock.ElapsedMilliseconds - _lastTick < 250) return;
        _lastTick = _clock.ElapsedMilliseconds;
        _tickPending = true;
        _worker.Publish(this, _generation, new("tick", ElapsedMilliseconds: _lastTick));
    }

    internal void TickDone(long generation) { if (generation == _generation) _tickPending = false; }

    internal bool IsLoading(long generation) => generation == _generation && IsBusy && !_disposed;

    internal bool Valid(long generation, long privacy)
        => generation == _generation && privacy == _worker.PrivacyEpoch && IsRunning && _canRun() && !_isPrivate();

    internal async Task CompleteLoadAsync(ScriptResult result, long generation, long privacy)
    {
        if (generation != _generation || _disposed) return;
        if (result.Error is not null) { Fail(result.Error); return; }
        IsBusy = false; IsRunning = true;
        _clock.Restart(); _sentAt.Clear(); _lastTick = 0;
        Changed?.Invoke();
        await ApplyAsync(result, generation, privacy);
    }

    internal async Task<bool> ApplyAsync(ScriptResult result, long generation, long privacy)
    {
        if (generation != _generation || _disposed) return false;
        // Privacy invalidates actions, not a fatal failure of the engine.
        if (result.Error is not null) { Fail(result.Error); return false; }
        if (!Valid(generation, privacy)) return false;
        try { return await ApplyActionsAsync(result, generation, privacy); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A refused effect (the session's send limit, for one) stops this script, never the worker.
            if (generation == _generation) Fail(ex.Message);
            return false;
        }
    }

    private async Task<bool> ApplyActionsAsync(ScriptResult result, long generation, long privacy)
    {
        foreach (var action in result.Actions)
        {
            if (!Valid(generation, privacy)) return false;
            if (action.Kind == "send")
            {
                var now = _clock.ElapsedMilliseconds;
                while (_sentAt.TryPeek(out var first) && now - first >= 60_000) _sentAt.Dequeue();
                if (_sentAt.Count >= 200 || _sentAt.Count(t => now - t < 1000) >= 20)
                { Fail(L.ScriptRateExceeded); return false; }
                if (string.IsNullOrWhiteSpace(action.Text) || action.Text.Length > 4096 || action.Text.Any(char.IsControl))
                { Fail(L.ScriptWorkerFailed); return false; }
                _sentAt.Enqueue(now);
                if (!await _send(action.Text)) { if (generation == _generation) Stop(); return false; }
            }
            else if (action.Kind == "echo")
            {
                AppendLog(action.Text);
                _echo(action.Text);
            }
            else if (action.Kind == "panel")
            {
                ScriptPanelAction panel;
                // The worker is isolated; its instructions are re-validated before anything is rendered.
                try { panel = ScriptPanelAction.Parse(action.Text); }
                catch (FormatException error) { Fail(L.Format(L.ScriptPanelRejected, error.Message)); return false; }
                _panels?.Invoke(panel);
            }
            else if (action.Kind == "report")
            {
                // The script read an MSDP variable the world has not sent; the session asks for it once.
                if (JavaScriptEngine.IsValidMsdpName(action.Text)) _report?.Invoke(action.Text);
            }
        }
        return true;
    }

    internal void Fail(string error)
    {
        // The worker cannot know the user's language; its send-policy refusal is named here instead.
        if (error.Contains(PackSendPolicy, StringComparison.Ordinal)) error = L.ScriptPackSendRefused;
        Stop(); Error = L.Format(L.ScriptFailed, error); AppendLog(Error);
    }

    internal const string PackSendPolicy = "Pack send policy";

    private void AppendLog(string text)
    {
        var combined = Log + (Log.Length == 0 ? "" : "\n") + text;
        Log = combined.Length > 16_384 ? combined[^16_384..] : combined;
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _worker.Detach(this);
        if (_ownsWorker) await _worker.DisposeAsync();
    }
}
