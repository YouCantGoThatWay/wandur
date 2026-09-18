using Wandur.Core.Agents;

namespace Wandur.Core.Tests;

public sealed class AgentRunnerTests
{
    [Fact]
    public async Task PreviewNeverSendsOrCommitsMemory()
    {
        var world = new World(); var runner = Create(world, new Provider());
        await runner.StartAsync(new() { Model = "test" }, "Explore", AgentRunMode.Preview);
        Assert.Empty(world.Commands); Assert.Equal("", runner.Memory);
        Assert.Equal("AgentPreviewReady", runner.StatusKey); Assert.False(world.Owned);
    }

    [Fact]
    public async Task StepSendsCatalogCommandAndThenPauses()
    {
        var world = new World(); var runner = Create(world, new Provider());
        await runner.StartAsync(new() { Model = "test" }, "Explore", AgentRunMode.Step);
        Assert.Equal(new[] { "look" }, world.Commands);
        Assert.Equal("Room visited", runner.Memory); Assert.False(world.Owned);
        Assert.Equal("AgentPaused", runner.StatusKey);
    }

    [Fact]
    public async Task ChangedWorldDiscardsDecision()
    {
        var world = new World(); var provider = new Provider { OnDecision = () => world.Revision++ };
        var runner = Create(world, provider);
        await runner.StartAsync(new() { Model = "test" }, "Explore", AgentRunMode.Step);
        Assert.Empty(world.Commands); Assert.Equal("AgentStale", runner.StatusKey);
    }

    [Fact]
    public async Task StopCancelsPendingDecisionEvenIfProviderIgnoresCancellation()
    {
        var world = new World(); var pending = new TaskCompletionSource<AgentDecision>();
        var provider = new Provider { Pending = pending.Task }; var runner = Create(world, provider);
        var running = runner.StartAsync(new() { Model = "test" }, "Explore", AgentRunMode.Run);
        runner.Stop(); pending.SetResult(new("look", "observe", "memory")); await running;
        Assert.Empty(world.Commands); Assert.False(world.Owned);
    }

    [Fact]
    public async Task UnknownActionFailsClosed()
    {
        var world = new World(); var runner = Create(world, new Provider { Action = "kill" });
        await runner.StartAsync(new() { Model = "test" }, "Explore", AgentRunMode.Step);
        Assert.Empty(world.Commands); Assert.Equal("AgentFailed", runner.StatusKey);
    }

    [Fact]
    public async Task StopReleasesBlockedCredentialLookup()
    {
        var world = new World(); var pending = new TaskCompletionSource<string?>();
        var runner = new AgentRunner(new AgentProviderRegistry([new Provider()]), world, _ => pending.Task);
        var task = runner.StartAsync(new() { Model = "test" }, "Explore", AgentRunMode.Run);
        runner.Stop(); await task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(runner.IsBusy); Assert.Empty(world.Commands);
        pending.SetResult("unused-secret");
    }

    private static AgentRunner Create(World world, Provider provider) => new(new AgentProviderRegistry([provider]), world, _ => Task.FromResult<string?>(null));
    private sealed class Provider : IAgentModelProvider
    {
        public string Key => "openai-compatible";
        public string Action { get; init; } = "look";
        public Action? OnDecision { get; init; }
        public Task<AgentDecision>? Pending { get; init; }
        public Task<AgentDecision> DecideAsync(AgentRequest request, string? apiKey, CancellationToken cancellationToken)
        { OnDecision?.Invoke(); return Pending ?? Task.FromResult(new AgentDecision(Action, "Observe room", "Room visited")); }
        public Task<IReadOnlyList<string>> ListModelsAsync(AgentProfile profile, string? apiKey, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([]);
    }
    private sealed class World : IAgentSessionGateway
    {
        public long Revision;
        public bool Owned;
        public List<string> Commands { get; } = [];
        public AgentObservation Observe() => new(Revision, 1, "A quiet room", true);
        public void SetAgentControl(bool enabled) => Owned = enabled;
        public Task<bool> SendAsync(string command, AgentObservation expected, CancellationToken cancellationToken)
        { Commands.Add(command); Revision++; return Task.FromResult(true); }
    }
}
