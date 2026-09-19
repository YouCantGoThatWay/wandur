using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Wandur.Core.Scripting;

public sealed class ProcessScriptRuntimeFactory(string executablePath, string? entryAssemblyPath = null) : IScriptRuntimeFactory
{
    public IScriptRuntime Create() => new ProcessScriptRuntime(executablePath, entryAssemblyPath);
}

public sealed class ProcessScriptRuntime(string executablePath, string? entryAssemblyPath = null) : IScriptRuntime
{
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly object _sync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Process? _process;
    private bool _stopped;
    private bool _loaded;

    public bool IsRunning { get { lock (_sync) return !_stopped && _process is not null; } }

    public Task<ScriptResult> LoadAsync(string source, CancellationToken cancellationToken = default, bool restrictedSend = false)
        => RequestAsync(new ScriptEvent("load", source) { RestrictedSend = restrictedSend }, cancellationToken);

    public Task<ScriptResult> DispatchAsync(ScriptEvent input, CancellationToken cancellationToken = default)
        => RequestAsync(input, cancellationToken);

    private async Task<ScriptResult> RequestAsync(ScriptEvent input, CancellationToken cancellationToken)
    {
        lock (_sync) if (_stopped) return new(false, []);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var acquired = false;
        try
        {
            await _requests.WaitAsync(cancellation.Token).ConfigureAwait(false);
            acquired = true;
            cancellation.CancelAfter(TimeSpan.FromSeconds(2));
            using var interruption = cancellation.Token.Register(Stop);
            Process process;
            lock (_sync)
            {
                if (_stopped) return new(false, []);
                if (input.Kind == "load")
                {
                    if (_loaded) throw new InvalidOperationException("A runtime can load only one script.");
                    if (Encoding.UTF8.GetByteCount(input.Text) > JavaScriptEngine.MaximumSourceBytes)
                        throw new InvalidDataException("Source exceeds 256 KiB.");
                    _loaded = true;
                }
                // A state seed is the one message that may precede the load; the engine holds it until then.
                else if (_process is null && input.Kind != "state") throw new InvalidOperationException("Script worker has not been loaded.");
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
                    _process = Process.Start(start) ?? throw new InvalidOperationException("Could not start script worker.");
                    // Drain stderr without retaining logs or allowing a full pipe to block the child.
                    _ = DrainErrorsAsync(_process.StandardError, _lifetime.Token);
                }
                process = _process ?? throw new InvalidOperationException("Could not start script worker.");
            }
            var limit = input.Kind == "state" ? JavaScriptEngine.MaximumStateCharacters : JavaScriptEngine.MaximumEventCharacters;
            if (input.Kind != "load" && input.Text.Length > limit)
                throw new InvalidDataException($"Event text exceeds {limit} characters.");
            var message = JsonSerializer.Serialize(input);
            if (message.Length > ScriptWorker.MaximumMessageCharacters) throw new InvalidDataException("Worker message exceeds size limit.");
            await process.StandardInput.WriteLineAsync(message.AsMemory(), cancellation.Token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellation.Token).ConfigureAwait(false);
            var line = await ScriptWorker.ReadMessageAsync(process.StandardOutput, cancellation.Token).ConfigureAwait(false)
                ?? throw new EndOfStreamException("Script worker exited without a response.");
            var result = JsonSerializer.Deserialize<ScriptResult>(line) ?? throw new JsonException("Invalid worker response.");
            ValidateResult(result);
            lock (_sync) if (_stopped) return new(false, []);
            if (result.Error is not null) { Stop(); return result with { Actions = [] }; }
            return result;
        }
        catch (OperationCanceledException)
        {
            Stop();
            return new(false, [], cancellationToken.IsCancellationRequested ? null : "Script worker request interrupted or timed out.");
        }
        catch (Exception error)
        {
            Stop();
            return new(false, [], error.Message[..Math.Min(error.Message.Length, 2048)]);
        }
        finally { if (acquired) _requests.Release(); }
    }

    private static void ValidateResult(ScriptResult result)
    {
        if (result.Actions is null || result.Actions.Count > JavaScriptEngine.MaximumActions || result.Error?.Length > 2048)
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
                // The worker is untrusted; a name reaches the wire only if the host would accept it from a mapping.
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

    public void Stop()
    {
        lock (_sync)
        {
            if (_stopped) return;
            _stopped = true;
            _lifetime.Cancel();
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
        try { lock (_sync) { _process?.Dispose(); _process = null; } }
        finally { _requests.Release(); }
    }
}
