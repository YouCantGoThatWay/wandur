using System.Text;
using System.Text.Json;

namespace Wandur.Core.Scripting;

/// <summary>The engines of one session, one per script, keyed by the id the host assigns. Every request is
/// handled on the calling thread, one engine call at a time, so no script's callbacks interleave with
/// another's. An engine that fails is discarded and its error goes back for that id only; the others are
/// untouched. This is the whole of what the worker process does, and what an in-process host does too.</summary>
public sealed class ScriptEngineSet
{
    private readonly Dictionary<string, JavaScriptEngine> _engines = new(StringComparer.Ordinal);
    private string? _seed;

    public int Count => _engines.Count;
    public bool IsLoaded(string id) => _engines.ContainsKey(id);

    /// <summary>Handles one request. Only a malformed request throws; every script failure is a result.</summary>
    public WorkerReply Handle(WorkerRequest request)
    {
        switch (request.Kind)
        {
            case "state": return new(Seed(request.Text));
            case "load": return new([Load(request.Id, request.Text, request.RestrictedSend)]);
            case "dispatch":
                return new(Dispatch(request.Ids ?? [request.Id], request.Event ?? throw new InvalidDataException("A dispatch request needs an event.")));
            case "stop": _engines.Remove(request.Id); return new([]);
            case "shutdown": _engines.Clear(); return new([]);
            default: throw new InvalidDataException("Unknown worker request.");
        }
    }

    /// <summary>Keeps the seed for engines loaded later and applies it to the running ones through the same
    /// no-callback path a seed before a load takes. Engines that cannot take it are discarded and named.</summary>
    public IReadOnlyList<ScriptResult> Seed(string state)
    {
        _seed = state;
        var failed = new List<ScriptResult>();
        foreach (var (id, engine) in _engines.ToArray())
        {
            var result = engine.Dispatch(new("state", state));
            if (result.Error is null) continue;
            _engines.Remove(id);
            failed.Add(result with { Id = id, Actions = [] });
        }
        return failed;
    }

    public ScriptResult Load(string id, string source, bool restrictedSend)
    {
        _engines.Remove(id);
        var engine = new JavaScriptEngine();
        // The stored seed reaches a new engine before its first line runs, exactly as a seed sent ahead of a load.
        if (_seed is { } seed) engine.Dispatch(new("state", seed));
        var result = engine.Load(source, restrictedSend);
        if (result.Error is null) _engines[id] = engine;
        return result with { Id = id };
    }

    public IReadOnlyList<ScriptResult> Dispatch(IReadOnlyList<string> ids, ScriptEvent input)
    {
        var results = new ScriptResult[ids.Count];
        for (var i = 0; i < ids.Count; i++)
        {
            var id = ids[i];
            if (!_engines.TryGetValue(id, out var engine)) { results[i] = new(false, []) { Id = id }; continue; }
            var result = engine.Dispatch(input);
            if (result.Error is not null) { _engines.Remove(id); result = result with { Actions = [] }; }
            results[i] = result with { Id = id };
        }
        return results;
    }

    public void Stop(string id) => _engines.Remove(id);
    public void Clear() => _engines.Clear();
}

public static class ScriptWorker
{
    /// <summary>A reply carries one result per engine, and a panel-heavy event across a full library can pass 2 MiB.</summary>
    internal const int MaximumMessageCharacters = 16 * 1024 * 1024;

    public static async Task RunAsync(TextReader input, TextWriter output)
    {
        var engines = new ScriptEngineSet();
        var messages = new MessageReader(input);
        while (true)
        {
            WorkerReply reply;
            var exit = false;
            try
            {
                var line = await messages.ReadAsync(CancellationToken.None).ConfigureAwait(false);
                if (line is null) return;
                var request = JsonSerializer.Deserialize<WorkerRequest>(line) ?? throw new JsonException("Missing request.");
                reply = engines.Handle(request);
                exit = request.Kind == "shutdown";
            }
            catch (Exception error)
            {
                // The host speaks the protocol; a line it cannot read means the pipe is not to be trusted any more.
                reply = new([], error.Message[..Math.Min(error.Message.Length, 2048)]);
                exit = true;
            }
            await output.WriteLineAsync(JsonSerializer.Serialize(reply)).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
            if (exit) return;
        }
    }

    /// <summary>Newline-framed messages with a size bound. ReadLineAsync allocates without one, so the framing
    /// is checked before deserializing; reads are block-sized and what follows a newline is kept for the next
    /// message.</summary>
    internal sealed class MessageReader(TextReader reader)
    {
        private readonly char[] _buffer = new char[16 * 1024];
        private int _start;
        private int _end;

        public async Task<string?> ReadAsync(CancellationToken cancellationToken)
        {
            var text = new StringBuilder();
            while (true)
            {
                if (_start == _end)
                {
                    _start = 0;
                    _end = await reader.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (_end == 0) return text.Length == 0 ? null : throw new EndOfStreamException("Incomplete worker message.");
                }
                var newline = Array.IndexOf(_buffer, '\n', _start, _end - _start);
                var take = (newline < 0 ? _end : newline) - _start;
                if (text.Length + take > MaximumMessageCharacters) throw new InvalidDataException("Worker message exceeds size limit.");
                text.Append(_buffer, _start, take);
                _start += take;
                if (newline < 0) continue;
                _start++;
                return text.ToString();
            }
        }
    }
}
