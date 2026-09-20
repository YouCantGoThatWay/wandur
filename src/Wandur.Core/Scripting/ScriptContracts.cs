namespace Wandur.Core.Scripting;

public sealed record ScriptAction(string Kind, string Text);

public sealed record ScriptEvent(string Kind, string Text = "", long ElapsedMilliseconds = 0)
{
    /// <summary>Set on a load request for a pack script whose send policy has not been relaxed by the user.</summary>
    public bool RestrictedSend { get; init; }
}

/// <summary>The outcome of one engine call. On the wire <see cref="Id"/> names the script the result belongs to.</summary>
public sealed record ScriptResult(bool Handled, IReadOnlyList<ScriptAction> Actions, string? Error = null)
{
    public string Id { get; init; } = "";
}

/// <summary>One line from the host to the session worker. <c>load</c> carries a script id, its source and the send
/// policy; <c>dispatch</c> carries an event and the ids of the engines that receive it, in order; <c>stop</c> discards
/// one engine; <c>state</c> carries the host's protocol cache, applied to every engine and kept for engines loaded
/// later; <c>shutdown</c> ends the process.</summary>
public sealed record WorkerRequest(string Kind, string Id = "", IReadOnlyList<string>? Ids = null, string Text = "",
    ScriptEvent? Event = null, bool RestrictedSend = false);

/// <summary>One line from the worker back to the host: one result per engine the request touched, in request order.
/// A request-level <see cref="Error"/> means the worker could not read the request and is exiting.</summary>
public sealed record WorkerReply(IReadOnlyList<ScriptResult> Results, string? Error = null);

/// <summary>The worker stopped answering or exited on its own. Every script it hosted is gone with it.</summary>
public sealed class ScriptWorkerException(string message) : Exception(message);

/// <summary>One worker per session hosting one engine per script. Requests are answered in order, one engine call
/// at a time. A script error comes back as that script's <see cref="ScriptResult.Error"/> and discards only that
/// engine; a failure of the worker itself is thrown as <see cref="ScriptWorkerException"/> and raised once through
/// <see cref="Failed"/> so an idle exit is noticed too. <see cref="Stop"/> must interrupt an outstanding request.</summary>
public interface ISessionScriptHost : IAsyncDisposable
{
    /// <summary>The worker is alive: started and neither stopped nor failed.</summary>
    bool IsRunning { get; }
    /// <summary>Raised from a background thread, once, when the worker fails outside a deliberate stop.</summary>
    event Action<string>? Failed;
    /// <summary>Applies the host's protocol cache to every engine and keeps it for later loads. The results name
    /// engines that could not take the seed and were discarded.</summary>
    Task<IReadOnlyList<ScriptResult>> SeedAsync(string state, CancellationToken cancellationToken = default);
    /// <summary>Creates the engine for a script, replacing any engine that id had, and runs its source.</summary>
    Task<ScriptResult> LoadAsync(string id, string source, bool restrictedSend = false, CancellationToken cancellationToken = default);
    /// <summary>Delivers one event to the named engines in order and returns one result per id, in the same order.
    /// An id without an engine gets an empty result.</summary>
    Task<IReadOnlyList<ScriptResult>> DispatchAsync(IReadOnlyList<string> ids, ScriptEvent input, CancellationToken cancellationToken = default);
    Task StopAsync(string id, CancellationToken cancellationToken = default);
    /// <summary>Ends the worker. Outstanding requests are interrupted; no failure is raised.</summary>
    void Stop();
}

/// <summary>Creates the session worker. A library asks for one when its first script needs to run.</summary>
public interface IScriptRuntimeFactory
{
    ISessionScriptHost Create();
}

public interface IWorldScriptStore
{
    string? Load(string worldKey);
    void Save(string worldKey, string source);
}
