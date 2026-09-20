using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Wandur.Core.Scripting;

public sealed class ProcessScriptRuntimeFactory(string executablePath, string? entryAssemblyPath = null) : IScriptRuntimeFactory
{
    public ISessionScriptHost Create() => new ProcessSessionScriptHost(executablePath, entryAssemblyPath);
}

/// <summary>The session's worker process, started on the first request and spoken to over a line protocol on
/// its stdin and stdout. One request is outstanding at a time; the worker answers them in order.</summary>
public sealed class ProcessSessionScriptHost(string executablePath, string? entryAssemblyPath = null) : ISessionScriptHost
{
    /// <summary>The wait for a reply, plus this much again for every engine beyond the first that the request
    /// runs. Jint's per-callback limits keep a reply far inside it; passing it means the process is stuck.</summary>
    public static readonly TimeSpan RequestDeadline = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PerEngineAllowance = TimeSpan.FromMilliseconds(500);
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly object _sync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<string> _loaded = new(StringComparer.Ordinal);
    private Process? _process;
    private ScriptWorker.MessageReader? _replies;
    private bool _stopped;
    private bool _failed;

    public event Action<string>? Failed;

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                if (_stopped || _failed || _process is null) return false;
                try { return !_process.HasExited; }
                catch (InvalidOperationException) { return false; }
            }
        }
    }

    /// <summary>The worker's process id while it runs, for tests that watch or kill it.</summary>
    public int? ProcessId { get { lock (_sync) return _process is null || _stopped ? null : _process.Id; } }

    public Task<IReadOnlyList<ScriptResult>> SeedAsync(string state, CancellationToken cancellationToken = default)
    {
        if (state.Length > JavaScriptEngine.MaximumStateCharacters) return Task.FromResult<IReadOnlyList<ScriptResult>>([]);
        int engines;
        lock (_sync) engines = _loaded.Count;
        return RequestAsync(new WorkerRequest("state", Text: state), null, engines, cancellationToken);
    }

    public async Task<ScriptResult> LoadAsync(string id, string source, bool restrictedSend = false, CancellationToken cancellationToken = default)
    {
        if (Encoding.UTF8.GetByteCount(source) > JavaScriptEngine.MaximumSourceBytes) return new(false, [], "Source exceeds 256 KiB.") { Id = id };
        var results = await RequestAsync(new WorkerRequest("load", id, Text: source, RestrictedSend: restrictedSend), [id], 1, cancellationToken).ConfigureAwait(false);
        lock (_sync) { if (results[0].Error is null) _loaded.Add(id); else _loaded.Remove(id); }
        return results[0];
    }

    public async Task<IReadOnlyList<ScriptResult>> DispatchAsync(IReadOnlyList<string> ids, ScriptEvent input, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0) return [];
        var limit = input.Kind == "state" ? JavaScriptEngine.MaximumStateCharacters : JavaScriptEngine.MaximumEventCharacters;
        if (input.Text.Length > limit)
            return ids.Select(id => new ScriptResult(false, [], $"Event text exceeds {limit} characters.") { Id = id }).ToArray();
        var results = await RequestAsync(new WorkerRequest("dispatch", Ids: ids, Event: input), ids, ids.Count, cancellationToken).ConfigureAwait(false);
        lock (_sync) foreach (var result in results) if (result.Error is not null) _loaded.Remove(result.Id);
        return results;
    }

    public async Task StopAsync(string id, CancellationToken cancellationToken = default)
    {
        lock (_sync) { if (_stopped || _process is null || !_loaded.Remove(id)) return; }
        await RequestAsync(new WorkerRequest("stop", id), [], 1, cancellationToken).ConfigureAwait(false);
    }

    /// <param name="expectedIds">The ids the reply must answer, in order, or null when any set is acceptable.</param>
    private async Task<IReadOnlyList<ScriptResult>> RequestAsync(WorkerRequest request, IReadOnlyList<string>? expectedIds, int engines, CancellationToken cancellationToken)
    {
        lock (_sync) if (_stopped || _failed) throw new ScriptWorkerException("The script worker is not running.");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var acquired = false;
        try
        {
            await _requests.WaitAsync(cancellation.Token).ConfigureAwait(false);
            acquired = true;
            cancellation.CancelAfter(RequestDeadline + PerEngineAllowance * Math.Max(0, engines - 1));
            Process process;
            ScriptWorker.MessageReader replies;
            lock (_sync)
            {
                if (_stopped || _failed) throw new ScriptWorkerException("The script worker is not running.");
                if (_process is null)
                {
                    var start = new ProcessStartInfo(executablePath)
                    {
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardInputEncoding = new UTF8Encoding(false),
                        StandardOutputEncoding = Encoding.UTF8
                    };
                    if (entryAssemblyPath is not null) start.ArgumentList.Add(entryAssemblyPath);
                    start.ArgumentList.Add("--script-worker");
                    var started = Process.Start(start) ?? throw new ScriptWorkerException("Could not start the script worker.");
                    started.EnableRaisingEvents = true;
                    started.Exited += (_, _) => Fail("The script worker exited.");
                    _process = started;
                    _replies = new(started.StandardOutput);
                    // Drain stderr without retaining logs or allowing a full pipe to block the child.
                    _ = DrainErrorsAsync(started.StandardError, _lifetime.Token);
                }
                process = _process;
                replies = _replies!;
            }
            var message = JsonSerializer.Serialize(request);
            if (message.Length > ScriptWorker.MaximumMessageCharacters) throw new InvalidDataException("Worker message exceeds size limit.");
            await process.StandardInput.WriteLineAsync(message.AsMemory(), cancellation.Token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellation.Token).ConfigureAwait(false);
            var line = await replies.ReadAsync(cancellation.Token).ConfigureAwait(false)
                ?? throw new EndOfStreamException("The script worker exited without a response.");
            var reply = JsonSerializer.Deserialize<WorkerReply>(line) ?? throw new JsonException("Invalid worker response.");
            if (reply.Error is not null) throw new ScriptWorkerException(reply.Error);
            ValidateReply(reply, expectedIds);
            lock (_sync) if (_stopped) throw new OperationCanceledException();
            return reply.Results;
        }
        catch (OperationCanceledException)
        {
            bool deliberate;
            lock (_sync) deliberate = _stopped || cancellationToken.IsCancellationRequested;
            if (deliberate) { Stop(); throw; }
            var reason = "The script worker did not respond in time.";
            Fail(reason);
            throw new ScriptWorkerException(reason);
        }
        catch (Exception error) when (error is not ScriptWorkerException)
        {
            var reason = error.Message[..Math.Min(error.Message.Length, 2048)];
            Fail(reason);
            throw new ScriptWorkerException(reason);
        }
        catch (ScriptWorkerException error) { Fail(error.Message); throw; }
        finally { if (acquired) _requests.Release(); }
    }

    private static void ValidateReply(WorkerReply reply, IReadOnlyList<string>? expectedIds)
    {
        if (reply.Results is null) throw new InvalidDataException("Invalid worker response.");
        if (expectedIds is not null)
        {
            if (reply.Results.Count != expectedIds.Count) throw new InvalidDataException("Worker answered the wrong scripts.");
            for (var i = 0; i < expectedIds.Count; i++)
                if (reply.Results[i]?.Id != expectedIds[i]) throw new InvalidDataException("Worker answered the wrong scripts.");
        }
        foreach (var result in reply.Results) ValidateResult(result);
    }

    /// <summary>The worker is untrusted; every result is checked against the per-event limits before the host acts on it.</summary>
    public static void ValidateResult(ScriptResult result)
    {
        if (result?.Actions is null || result.Id is null || result.Id.Length > 128 || result.Actions.Count > JavaScriptEngine.MaximumActions || result.Error?.Length > 2048)
            throw new InvalidDataException("Invalid worker response.");
        var length = 0;
        var panelLength = 0;
        var panels = 0;
        var reports = 0;
        var outputs = 0;
        foreach (var action in result.Actions)
        {
            if (action is null || action.Text is null || action.Kind is not ("send" or "echo" or "panel" or "report"))
                throw new InvalidDataException("Invalid worker action.");
            if (action.Kind == "panel")
            {
                if (++panels > ScriptPanelAction.MaximumActionsPerEvent || action.Text.Length > JavaScriptEngine.MaximumPanelActionCharacters)
                    throw new InvalidDataException("Invalid panel action.");
                panelLength += action.Text.Length;
                continue;
            }
            if (action.Kind == "report")
            {
                // A name reaches the wire only if the host would accept it from a mapping.
                if (++reports > JavaScriptEngine.MaximumReportsPerScript || !JavaScriptEngine.IsValidMsdpName(action.Text))
                    throw new InvalidDataException("Invalid report action.");
                continue;
            }
            if (++outputs > 32 || action.Text.Length > 8192) throw new InvalidDataException("Invalid worker action.");
            if (action.Kind == "send" && (action.Text.Length is 0 or > 4096 || action.Text.Any(c => char.IsControl(c) || c is '\u2028' or '\u2029')))
                throw new InvalidDataException("Invalid worker command.");
            length += action.Text.Length;
        }
        if (length > 32768 || panelLength > JavaScriptEngine.MaximumPanelCharacters)
            throw new InvalidDataException("Worker output exceeds size limit.");
    }

    private static async Task DrainErrorsAsync(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[1024];
        try { while (await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false) > 0) { } }
        catch (Exception) { /* The pipe closes when the worker is stopped. */ }
    }

    /// <summary>Records a failure once and tells the owner. A stop that was asked for is not a failure.</summary>
    private void Fail(string reason)
    {
        lock (_sync)
        {
            if (_stopped || _failed) return;
            _failed = true;
        }
        Stop(deliberate: false);
        Failed?.Invoke(reason);
    }

    public void Stop() => Stop(deliberate: true);

    private void Stop(bool deliberate)
    {
        lock (_sync)
        {
            if (deliberate) { if (_stopped) return; _stopped = true; }
            _lifetime.Cancel();
            _loaded.Clear();
            try { if (_process is { HasExited: false }) _process.Kill(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        // Wait only in Dispose; Stop itself never waits for JavaScript or pipe I/O.
        await _requests.WaitAsync().ConfigureAwait(false);
        try { lock (_sync) { _process?.Dispose(); _process = null; _replies = null; } }
        finally { _requests.Release(); }
    }
}
