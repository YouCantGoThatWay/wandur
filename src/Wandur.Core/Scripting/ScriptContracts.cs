namespace Wandur.Core.Scripting;

public sealed record ScriptAction(string Kind, string Text);

public sealed record ScriptEvent(string Kind, string Text = "", long ElapsedMilliseconds = 0)
{
    /// <summary>Set on a load request for a pack script whose send policy has not been relaxed by the user.</summary>
    public bool RestrictedSend { get; init; }
}

public sealed record ScriptResult(bool Handled, IReadOnlyList<ScriptAction> Actions, string? Error = null);

/// <summary>One isolated worker per running session. Stop must interrupt an outstanding request.</summary>
public interface IScriptRuntime : IAsyncDisposable
{
    bool IsRunning { get; }
    Task<ScriptResult> LoadAsync(string source, CancellationToken cancellationToken = default, bool restrictedSend = false);
    Task<ScriptResult> DispatchAsync(ScriptEvent input, CancellationToken cancellationToken = default);
    void Stop();
}

public interface IScriptRuntimeFactory
{
    IScriptRuntime Create();
}

public interface IWorldScriptStore
{
    string? Load(string worldKey);
    void Save(string worldKey, string source);
}
