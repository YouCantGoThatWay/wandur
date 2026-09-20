using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Avalonia.Threading;
using Wandur.Core.Scripting;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.Services;

/// <summary>Session-scoped automation. Mutable state and effects belong to the UI dispatcher;
/// only the sequential request pump runs in the background.</summary>
public sealed class SessionScripts(
    IScriptRuntimeFactory runtimeFactory,
    IWorldScriptStore store,
    Func<bool> canRun,
    Func<bool> isPrivate,
    Func<string, Task<bool>> send,
    Action<string> echo,
    Action<ScriptPanelAction>? panels = null,
    Func<bool>? restrictedSend = null,
    Func<string?>? seedState = null,
    Action<string>? report = null) : IAsyncDisposable
{
    private sealed record Work(ScriptEvent Input, long PrivacyEpoch, TaskCompletionSource<bool>? Completion = null);
    private readonly ScriptLineBuffer _lines = new();
    // Events published while the worker boots, delivered once the script runs. A pack script starts at
    // connect and boots a process; the values the world sends during login would otherwise fall between
    // the seed taken before the boot and the running state that accepts events.
    private readonly List<ScriptEvent> _startup = [];
    private const int MaximumStartupEvents = 1024;
    private readonly Stopwatch _clock = new();
    private readonly Queue<long> _sentAt = new();
    private IScriptRuntime? _runtime;
    private CancellationTokenSource? _cancellation;
    private Channel<Work>? _queue;
    private long _generation;
    private long _privacyEpoch;
    private bool _wasPrivate;
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
    public bool IsPaused => IsRunning && isPrivate();
    public bool CanRun => !_disposed && !IsBusy && canRun() && !isPrivate();

    public void Configure(string worldKey, string worldName)
    {
        Stop();
        WorldName = worldName;
        if (_worldKey != worldKey)
        {
            _worldKey = worldKey;
            Error = null;
            try { Source = store.Load(worldKey) ?? ScriptExamples.Starter; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Source = ""; Error = L.Format(L.ScriptLoadFailed, ex.Message); }
        }
        Changed?.Invoke();
    }

    public void Save()
    {
        if (_worldKey is null || _disposed) return;
        try { store.Save(_worldKey, Source); Error = null; AppendLog(L.ScriptSaved); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { Error = L.Format(L.ScriptSaveFailed, ex.Message); Changed?.Invoke(); }
    }

    public async Task RunAsync()
    {
        if (!CanRun) { Error = isPrivate() ? L.ScriptPrivatePause : L.ScriptNeedConnection; Changed?.Invoke(); return; }
        Stop();
        Error = null; Log = "";
        if (Encoding.UTF8.GetByteCount(Source) > WorldScriptStore.MaximumBytes)
        { Error = L.ScriptSourceTooLarge; Changed?.Invoke(); return; }
        var generation = _generation;
        _wasPrivate = isPrivate();
        var privacy = _privacyEpoch;
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        var token = cancellation.Token;
        IsBusy = true; _startup.Clear(); Changed?.Invoke();
        IScriptRuntime? runtime = null;
        try
        {
            runtime = runtimeFactory.Create();
            _runtime = runtime;
            var source = Source;
            var restricted = restrictedSend?.Invoke() == true;
            // The host's protocol cache goes first, so mud.state.get answers from the script's first line.
            if (seedState?.Invoke() is { } seed)
            {
                var seeded = await Task.Run(() => runtime.DispatchAsync(new("state", seed), token), token);
                if (generation != _generation) return;
                if (seeded.Error is not null) { Fail(seeded.Error); return; }
            }
            var result = await Task.Run(() => runtime.LoadAsync(source, token, restricted), token);
            if (generation != _generation) return;
            if (result.Error is not null) { Fail(result.Error); return; }
            IsBusy = false; IsRunning = true;
            _clock.Restart(); _sentAt.Clear(); _lastTick = 0;
            _queue = Channel.CreateBounded<Work>(new BoundedChannelOptions(128)
            { SingleReader = false, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
            await ApplyAsync(result, generation, privacy);
            if (generation == _generation && IsRunning && privacy == _privacyEpoch)
                foreach (var pending in _startup) if (!Enqueue(new(pending, privacy))) break;
            _startup.Clear();
            if (generation == _generation && IsRunning)
            {
                var reader = _queue.Reader;
                _ = Task.Run(() => PumpAsync(runtime, reader, generation, token));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (generation == _generation) Fail(ex is OperationCanceledException ? L.ScriptWorkerFailed : ex.Message);
        }
        finally
        {
            if (generation == _generation) { IsBusy = false; Changed?.Invoke(); }
        }
    }

    public void Stop()
    {
        _generation++;
        IsRunning = IsBusy = false;
        _clock.Stop(); _lines.Clear(); _startup.Clear(); _tickPending = false;
        var queue = _queue; _queue = null;
        queue?.Writer.TryComplete();
        if (queue is not null) while (queue.Reader.TryRead(out var work)) work.Completion?.TrySetResult(true);
        var cancellation = _cancellation; _cancellation = null;
        cancellation?.Cancel();
        var runtime = _runtime; _runtime = null;
        runtime?.Stop();
        if (runtime is not null) _ = DisposeRuntimeAsync(runtime, cancellation);
        else cancellation?.Dispose();
        Changed?.Invoke();
    }

    private static async Task DisposeRuntimeAsync(IScriptRuntime runtime, CancellationTokenSource? cancellation)
    {
        try { await runtime.DisposeAsync(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* Stop already invalidated every pending effect. */ }
        finally { cancellation?.Dispose(); }
    }

    public void RefreshState()
    {
        var privacy = isPrivate();
        if (_wasPrivate != privacy)
        {
            _wasPrivate = privacy;
            _privacyEpoch++;
            _lines.Clear();
            _startup.Clear();
        }
        Changed?.Invoke();
    }

    public void DiscardPartialLine() => _lines.Clear();

    public void Feed(string text)
    {
        if (!IsRunning || isPrivate()) { _lines.Clear(); return; }
        foreach (var line in _lines.Feed(text))
            if (!Enqueue(new(new("line", line), _privacyEpoch))) break;
    }

    public void Publish(ScriptEvent input)
    {
        if (isPrivate()) return;
        if (IsBusy && !IsRunning)
        {
            if (_startup.Count < MaximumStartupEvents) _startup.Add(input);
            return;
        }
        if (!IsRunning) return;
        Enqueue(new(input, _privacyEpoch));
    }

    public Task<bool> HandleCommandAsync(string command)
    {
        if (!IsRunning || isPrivate()) return Task.FromResult(false);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(new(new("command", command), _privacyEpoch, completion));
        return completion.Task;
    }

    public void Tick()
    {
        if (!IsRunning || isPrivate() || _tickPending || _clock.ElapsedMilliseconds - _lastTick < 250) return;
        _lastTick = _clock.ElapsedMilliseconds;
        _tickPending = true;
        Enqueue(new(new("tick", ElapsedMilliseconds: _lastTick), _privacyEpoch));
    }

    private bool Enqueue(Work work)
    {
        if (_queue?.Writer.TryWrite(work) == true) return true;
        work.Completion?.TrySetResult(true);
        Fail(L.ScriptQueueOverflow);
        return false;
    }

    private async Task PumpAsync(IScriptRuntime runtime, ChannelReader<Work> reader, long generation, CancellationToken token)
    {
        try
        {
            await foreach (var work in reader.ReadAllAsync(token))
            {
                try
                {
                    var valid = await Dispatcher.UIThread.InvokeAsync(() => Valid(generation, work.PrivacyEpoch));
                    if (!valid) { work.Completion?.TrySetResult(true); continue; }
                    var result = await runtime.DispatchAsync(work.Input, token);
                    var applied = await Dispatcher.UIThread.InvokeAsync(() => ApplyAsync(result, generation, work.PrivacyEpoch));
                    work.Completion?.TrySetResult(!applied || result.Handled);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    work.Completion?.TrySetResult(true);
                    await Dispatcher.UIThread.InvokeAsync(() => { if (generation == _generation) Fail(ex is OperationCanceledException ? L.ScriptWorkerFailed : ex.Message); });
                }
                finally
                {
                    if (work.Input.Kind == "tick")
                        await Dispatcher.UIThread.InvokeAsync(() => { if (generation == _generation) _tickPending = false; });
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private bool Valid(long generation, long privacy) => generation == _generation && privacy == _privacyEpoch && IsRunning && canRun() && !isPrivate();

    private async Task<bool> ApplyAsync(ScriptResult result, long generation, long privacy)
    {
        if (generation != _generation || _disposed) return false;
        // Privacy invalidates actions, not a fatal failure of the currently owned worker.
        if (result.Error is not null) { Fail(result.Error); return false; }
        if (!Valid(generation, privacy)) return false;
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
                if (!await send(action.Text)) { if (generation == _generation) Stop(); return false; }
            }
            else if (action.Kind == "echo")
            {
                AppendLog(action.Text);
                echo(action.Text);
            }
            else if (action.Kind == "panel")
            {
                ScriptPanelAction panel;
                // The worker is isolated; its instructions are re-validated before anything is rendered.
                try { panel = ScriptPanelAction.Parse(action.Text); }
                catch (FormatException error) { Fail(L.Format(L.ScriptPanelRejected, error.Message)); return false; }
                panels?.Invoke(panel);
            }
            else if (action.Kind == "report")
            {
                // The script read an MSDP variable the world has not sent; the session asks for it once.
                if (JavaScriptEngine.IsValidMsdpName(action.Text)) report?.Invoke(action.Text);
            }
        }
        return true;
    }

    private void Fail(string error)
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

    public ValueTask DisposeAsync()
    {
        if (!_disposed) { _disposed = true; Stop(); }
        return ValueTask.CompletedTask;
    }
}
