namespace Wandur.Core.Agents;

public enum AgentRunMode { Preview, Step, Run }
public sealed record AgentObservation(long Revision, long Generation, string Text, bool CanAct);

public interface IAgentSessionGateway
{
    AgentObservation Observe();
    void SetAgentControl(bool enabled);
    Task<bool> SendAsync(string command, AgentObservation expected, CancellationToken cancellationToken);
}

/// <summary>One bounded decision loop per connection; transport and UI stay outside the runner.</summary>
public sealed class AgentRunner(IAgentProviderResolver providers, IAgentSessionGateway session,
    Func<AgentProfile, Task<string?>> readCredential)
{
    private volatile CancellationTokenSource? _cancellation;
    private readonly Queue<string> _activity = new();
    public bool IsBusy => _cancellation is not null;
    public string StatusKey { get; private set; } = "AgentStopped";
    public string Memory { get; private set; } = "";
    public string Activity => string.Join("\n\n", _activity);
    public event Action? Changed;

    // Cancellation can be signalled by the receiving thread before UI callbacks run.
    public void CancelPending() { try { _cancellation?.Cancel(); } catch (ObjectDisposedException) { } }
    public void Stop(string status = "AgentStopped") { CancelPending(); session.SetAgentControl(false); SetStatus(status); }
    public void ClearMemory() { Stop(); Memory = ""; _activity.Clear(); Changed?.Invoke(); }
    private void SetStatus(string key) { StatusKey = key; Changed?.Invoke(); }
    private static bool Same(AgentObservation a, AgentObservation b) => b.CanAct && a.Revision == b.Revision && a.Generation == b.Generation;

    public async Task StartAsync(AgentProfile profile, string goal, AgentRunMode mode)
    {
        if (IsBusy) return;
        if (!session.Observe().CanAct) { SetStatus("AgentUnavailable"); return; }
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(profile.MaxRunSeconds));
        _cancellation = cancellation;
        var token = cancellation.Token;
        try
        {
            AgentConfiguration.Validate(profile);
            if (string.IsNullOrWhiteSpace(goal) || goal.Length > 4000) throw new ArgumentException("Invalid goal.");
            var catalog = AgentCatalog.Parse(profile.Commands);
            var provider = providers.Resolve(profile.Provider);
            if (mode != AgentRunMode.Preview) session.SetAgentControl(true);
            SetStatus("AgentThinking");
            var key = await readCredential(profile).WaitAsync(token);
            token.ThrowIfCancellationRequested();
            for (var count = 0; count < profile.MaxDecisions; count++)
            {
                var observation = session.Observe();
                if (!observation.CanAct) { SetStatus("AgentUnavailable"); return; }
                SetStatus("AgentThinking");
                var decision = await provider.DecideAsync(new(profile, goal, Memory, observation.Text), key, token).WaitAsync(token);
                token.ThrowIfCancellationRequested();
                if (!Same(observation, session.Observe())) { SetStatus("AgentStale"); return; }
                if (decision.Memory.Length > 2048 || decision.Reason.Length > 512) throw new InvalidOperationException("Oversized decision.");
                var command = catalog.FirstOrDefault(c => c.Id == decision.Action);
                if (command is null && decision.Action is not ("wait" or "done")) throw new InvalidOperationException("Unknown action.");
                _activity.Enqueue($"{decision.Action}: {decision.Reason}");
                while (_activity.Count > 20) _activity.Dequeue();
                Changed?.Invoke();
                if (mode == AgentRunMode.Preview) { SetStatus("AgentPreviewReady"); return; }
                if (decision.Action == "done") { Memory = decision.Memory; SetStatus("AgentCompleted"); return; }
                if (command is not null && !await session.SendAsync(command.Command, observation, token))
                { SetStatus("AgentStale"); return; }
                token.ThrowIfCancellationRequested();
                Memory = decision.Memory;
                SetStatus("AgentWaiting");
                if (!await WaitForOutputAsync(observation, profile.ResponseTimeoutSeconds, token))
                { SetStatus("AgentNoOutput"); return; }
                if (mode == AgentRunMode.Step) { SetStatus("AgentPaused"); return; }
                await Task.Delay(TimeSpan.FromSeconds(profile.ActionIntervalSeconds), token);
            }
            SetStatus("AgentRunLimit");
        }
        catch (OperationCanceledException) { if (StatusKey is "AgentThinking" or "AgentWaiting") SetStatus("AgentPaused"); }
        catch (InvalidDataException) { SetStatus("AgentInvalidResponse"); }
        catch (TimeoutException) { SetStatus("AgentRequestTimedOut"); }
        catch (HttpRequestException) { SetStatus("AgentConnectionFailed"); }
        catch (ArgumentException) { SetStatus("AgentConfigurationRequired"); }
        catch (Exception) { SetStatus("AgentFailed"); }
        finally { _cancellation = null; session.SetAgentControl(false); Changed?.Invoke(); }
    }

    private async Task<bool> WaitForOutputAsync(AgentObservation before, int timeoutSeconds, CancellationToken token)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            var now = session.Observe();
            if (!now.CanAct || now.Generation != before.Generation) return false;
            if (now.Revision != before.Revision)
            {
                // Allow a response split across network packets to settle before the next inference.
                await Task.Delay(500, token);
                return true;
            }
            await Task.Delay(100, token);
        }
        return false;
    }
}
