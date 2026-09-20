using System.Diagnostics;
using System.Text.Json;
using Wandur.Core.Scripting;

namespace Wandur.Core.Tests;

public sealed class ScriptRuntimeTests
{
    private static string Line(WorkerRequest request) => JsonSerializer.Serialize(request) + "\n";
    private static WorkerReply Reply(string line) => JsonSerializer.Deserialize<WorkerReply>(line)!;

    [Fact]
    public async Task WorkerHostsTwoEnginesAndAnswersEveryRequestWithTheScriptId()
    {
        var input =
            Line(new("load", "a", Text: "let n = 0; mud.alias('x', () => mud.echo('a' + ++n)); mud.trigger(/^go$/, () => mud.echo('a-line'));")) +
            Line(new("load", "b", Text: "mud.trigger(/^go$/, () => mud.echo('b-line')); mud.alias('x', () => mud.echo('b'));")) +
            Line(new("dispatch", Ids: ["a", "b"], Event: new("line", "go"))) +
            Line(new("dispatch", Ids: ["a"], Event: new("command", "x"))) +
            Line(new("stop", "a")) +
            Line(new("dispatch", Ids: ["a", "b"], Event: new("command", "x"))) +
            Line(new("shutdown"));
        var output = new StringWriter();
        await ScriptWorker.RunAsync(new StringReader(input), output);
        var replies = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(Reply).ToArray();
        Assert.Equal(7, replies.Length);
        Assert.All(replies, reply => Assert.Null(reply.Error));
        Assert.Equal("a", Assert.Single(replies[0].Results).Id);
        Assert.Equal("b", Assert.Single(replies[1].Results).Id);
        // One event, fanned out in the order the ids were given, one result each.
        Assert.Equal(["a", "b"], replies[2].Results.Select(result => result.Id));
        Assert.Equal(new ScriptAction("echo", "a-line"), Assert.Single(replies[2].Results[0].Actions));
        Assert.Equal(new ScriptAction("echo", "b-line"), Assert.Single(replies[2].Results[1].Actions));
        Assert.Equal("a1", Assert.Single(Assert.Single(replies[3].Results).Actions).Text);
        Assert.Empty(replies[4].Results);
        // The stopped script answers empty without an error; the other keeps its globals and keeps running.
        Assert.Empty(replies[5].Results[0].Actions);
        Assert.Null(replies[5].Results[0].Error);
        Assert.True(replies[5].Results[1].Handled);
        Assert.Equal("b", Assert.Single(replies[5].Results[1].Actions).Text);
        Assert.Empty(replies[6].Results);
    }

    [Fact]
    public void ASeedReachesEnginesLoadedBeforeAndAfterItWithoutFiringCallbacks()
    {
        var engines = new ScriptEngineSet();
        const string source = """
            mud.on(Events.Msdp, () => mud.echo("fired"));
            mud.alias(/^read$/, () => mud.echo("health:" + mud.state.get("msdp.HEALTH")));
            mud.echo("load:" + mud.state.get("msdp.HEALTH"));
            """;
        var early = engines.Handle(new("load", "early", Text: source));
        Assert.Equal([new ScriptAction("report", "HEALTH"), new ScriptAction("echo", "load:undefined")], Assert.Single(early.Results).Actions);
        Assert.Empty(engines.Handle(new("state", Text: """{"msdp":{"HEALTH":"100"}}""")).Results);
        var late = engines.Handle(new("load", "late", Text: source));
        Assert.Equal(new ScriptAction("echo", "load:100"), Assert.Single(late.Results).Actions[0]);
        var read = engines.Handle(new("dispatch", Ids: ["early", "late"], Event: new("command", "read")));
        Assert.Equal(["health:100", "health:100"], read.Results.Select(result => Assert.Single(result.Actions).Text));
        // A later seed replaces the stored one and reaches both running engines, still without a callback.
        Assert.Empty(engines.Handle(new("state", Text: """{"msdp":{"HEALTH":"7"}}""")).Results);
        read = engines.Handle(new("dispatch", Ids: ["early", "late"], Event: new("command", "read")));
        Assert.Equal(["health:7", "health:7"], read.Results.Select(result => Assert.Single(result.Actions).Text));
        Assert.Equal(2, engines.Count);
    }

    [Fact]
    public void ARunawayCallbackStopsThatScriptOnlyAndTheOtherKeepsRunning()
    {
        var engines = new ScriptEngineSet();
        Assert.Null(engines.Handle(new("load", "loop", Text: "mud.trigger(/^go$/, () => { mud.send('discard'); while (true) {} });")).Results[0].Error);
        Assert.Null(engines.Handle(new("load", "fine", Text: "let n = 0; mud.trigger(/^go$/, () => mud.echo('fine' + ++n));")).Results[0].Error);
        var timer = Stopwatch.StartNew();
        var reply = engines.Handle(new("dispatch", Ids: ["loop", "fine"], Event: new("line", "go")));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5));
        Assert.NotNull(reply.Results[0].Error);
        Assert.Empty(reply.Results[0].Actions);
        Assert.Equal("fine1", Assert.Single(reply.Results[1].Actions).Text);
        Assert.False(engines.IsLoaded("loop"));
        Assert.True(engines.IsLoaded("fine"));
        reply = engines.Handle(new("dispatch", Ids: ["loop", "fine"], Event: new("line", "go")));
        Assert.Null(reply.Results[0].Error);
        Assert.Empty(reply.Results[0].Actions);
        Assert.Equal("fine2", Assert.Single(reply.Results[1].Actions).Text);
    }

    [Fact]
    public async Task RealWorkerKeepsStateAndDiscardsOnlyTheScriptThatFailed()
    {
        await using var host = CreateHost();
        Assert.Null((await host.LoadAsync("a", "let n=0; mud.alias('x', () => mud.echo(String(++n))); mud.alias('bad', () => { mud.send('discard'); throw Error('boom') });")).Error);
        Assert.Null((await host.LoadAsync("b", "mud.alias('x', () => mud.echo('b'));")).Error);
        Assert.True(host.IsRunning);
        Assert.Equal("1", Assert.Single((await host.DispatchAsync(["a"], new("command", "x")))[0].Actions).Text);
        Assert.Equal("2", Assert.Single((await host.DispatchAsync(["a"], new("command", "x")))[0].Actions).Text);
        var failed = await host.DispatchAsync(["a", "b"], new("command", "bad"));
        Assert.Contains("boom", failed[0].Error);
        Assert.Empty(failed[0].Actions);
        Assert.Null(failed[1].Error);
        Assert.True(host.IsRunning);
        var after = await host.DispatchAsync(["a", "b"], new("command", "x"));
        Assert.Empty(after[0].Actions);
        Assert.Equal("b", Assert.Single(after[1].Actions).Text);
    }

    [Fact]
    public async Task StopInterruptsPendingLoadAndInvalidatesItsEffects()
    {
        await using var host = CreateHost();
        var pending = host.LoadAsync("a", "mud.send('discard'); while(true) {}");
        var timer = Stopwatch.StartNew();
        host.Stop();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.False(host.IsRunning);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task RealWorkerCarriesPanelActionsAndHonoursTheRestrictedSendPolicy()
    {
        await using var host = CreateHost();
        var loaded = await host.LoadAsync("ship", """
            const p = mud.panel("ship", { title: "Ship" });
            p.gauge("hull", { label: "Hull", value: 12, max: 100 });
            p.button("flee", { onClick: () => mud.send("flee") });
            mud.trigger(/^hit$/, () => mud.send("flee"));
            """, restrictedSend: true);
        Assert.Null(loaded.Error);
        Assert.Equal(
        [
            new ScriptAction("panel", """{"panel":"ship","action":"create","title":"Ship","dock":"right"}"""),
            new ScriptAction("panel", """{"panel":"ship","action":"widget","widget":"hull","kind":"gauge","props":{"label":"Hull","value":12,"max":100}}"""),
            new ScriptAction("panel", """{"panel":"ship","action":"widget","widget":"flee","kind":"button","props":{"label":"flee"}}""")
        ], loaded.Actions);
        var click = (await host.DispatchAsync(["ship"], new("panel", ScriptPanelAction.EventJson("ship", "flee", "click"))))[0];
        Assert.Equal(new ScriptAction("send", "flee"), Assert.Single(click.Actions));
        var refused = (await host.DispatchAsync(["ship"], new("line", "hit")))[0];
        Assert.Contains("Pack send policy", refused.Error);
        Assert.Empty(refused.Actions);
    }

    internal static ProcessSessionScriptHost CreateHost()
    {
        var worker = Path.Combine(AppContext.BaseDirectory, "ScriptWorkerHost", "Wandur.ScriptWorkerHost.dll");
        Assert.True(File.Exists(worker), $"Worker test host is missing: {worker}");
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        return new ProcessSessionScriptHost(dotnet, worker);
    }

    [Fact]
    public async Task RealWorkerAppliesAStateSeedToEnginesBeforeAndAfterItAndReportsUnknownVariables()
    {
        await using var host = CreateHost();
        const string source = """
            mud.on(Events.Msdp, () => mud.echo("fired"));
            mud.echo("health:" + mud.state.get("msdp.HEALTH") + " hp:" + mud.state.get("gmcp.Char.Vitals.hp"));
            mud.echo("level:" + mud.state.get("msdp.LEVELCOMBAT"));
            mud.alias(/^again$/, () => mud.echo("level:" + mud.state.get("msdp.LEVELCOMBAT")));
            """;
        Assert.Null((await host.LoadAsync("early", "mud.alias(/^again$/, () => mud.echo('early:' + mud.state.get('msdp.HEALTH')));")).Error);
        Assert.Empty(await host.SeedAsync("""{"gmcp":{"Char.Vitals":{"hp":9}},"msdp":{"HEALTH":"100"}}"""));
        var loaded = await host.LoadAsync("late", source);
        Assert.Null(loaded.Error);
        Assert.Equal(
        [
            new ScriptAction("echo", "health:100 hp:9"),
            new ScriptAction("report", "LEVELCOMBAT"),
            new ScriptAction("echo", "level:undefined")
        ], loaded.Actions);
        var again = await host.DispatchAsync(["early", "late"], new("command", "again"));
        Assert.Equal([new ScriptAction("echo", "early:100")], again[0].Actions);
        Assert.Equal([new ScriptAction("echo", "level:undefined")], again[1].Actions);
        Assert.True(host.IsRunning);
    }

    [Fact]
    public async Task RealWorkerPreservesUnicodeInSourceEventsAndActions()
    {
        await using var host = CreateHost();
        var loaded = await host.LoadAsync("u", "mud.alias(/^(.+)$/, m => { mud.echo('こんにちは 🌍 ' + m[1]); mud.send('examiner café'); });");
        Assert.Null(loaded.Error);
        var result = (await host.DispatchAsync(["u"], new("command", "Grüße 世界 🧙")))[0];
        Assert.Null(result.Error);
        Assert.True(result.Handled);
        Assert.Equal(new ScriptAction("echo", "こんにちは 🌍 Grüße 世界 🧙"), result.Actions[0]);
        Assert.Equal(new ScriptAction("send", "examiner café"), result.Actions[1]);
    }

    [Fact]
    public async Task ParentDeadlineTerminatesAnUnresponsiveWorkerAndRaisesFailedOnce()
    {
        await using var host = CreateHost();
        var failures = new List<string>();
        host.Failed += failures.Add;
        Assert.Null((await host.LoadAsync("a", "mud.alias(/.*/, () => {});")).Error);
        await Assert.ThrowsAsync<ScriptWorkerException>(() => host.DispatchAsync(["a"], new("command", "test-host-unresponsive")).WaitAsync(TimeSpan.FromSeconds(6)));
        Assert.False(host.IsRunning);
        await Task.Delay(200);
        Assert.Single(failures);
        await Assert.ThrowsAsync<ScriptWorkerException>(() => host.DispatchAsync(["a"], new("command", "x")));
    }

    [Fact]
    public async Task AWorkerThatExitsOnItsOwnRaisesFailedEvenWhileIdle()
    {
        await using var host = CreateHost();
        var failed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Failed += reason => failed.TrySetResult(reason);
        Assert.Null((await host.LoadAsync("a", "mud.alias(/.*/, () => {});")).Error);
        var processId = host.ProcessId;
        Assert.NotNull(processId);
        // The request that carries the crash marker never gets a reply: the exit is seen as a failure.
        await Assert.ThrowsAsync<ScriptWorkerException>(() => host.DispatchAsync(["a"], new("command", "test-host-crash")).WaitAsync(TimeSpan.FromSeconds(6)));
        Assert.NotEmpty(await failed.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.False(host.IsRunning);
        Assert.True(SpinWait.SpinUntil(() =>
        {
            try { return Process.GetProcessById(processId.Value).HasExited; }
            catch (ArgumentException) { return true; }
        }, TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task StopCancelsActiveAndQueuedRequestsWithoutWaitingForWorker()
    {
        await using var host = CreateHost();
        Assert.Null((await host.LoadAsync("a", "mud.alias(/.*/, () => {});")).Error);
        var active = host.DispatchAsync(["a"], new("command", "test-host-unresponsive"));
        var queued = host.DispatchAsync(["a"], new("tick", ElapsedMilliseconds: 1000));
        Assert.False(active.IsCompleted);
        var timer = Stopwatch.StartNew();
        host.Stop();
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active.WaitAsync(TimeSpan.FromSeconds(2)));
        await Assert.ThrowsAnyAsync<Exception>(() => queued.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(host.IsRunning);
    }

    [Fact]
    public async Task MalformedWorkerInputReturnsOneErrorAndExits()
    {
        var output = new StringWriter();
        await ScriptWorker.RunAsync(new StringReader("not json\n"), output);
        var reply = Reply(output.ToString());
        Assert.NotNull(reply.Error);
        Assert.Empty(reply.Results);
    }

    [Fact]
    public void ResultsAreValidatedPerScript()
    {
        ProcessSessionScriptHost.ValidateResult(new(true, [new("send", "look")]) { Id = "a" });
        Assert.Throws<InvalidDataException>(() => ProcessSessionScriptHost.ValidateResult(new(false, [new("send", "bad\ncommand")]) { Id = "a" }));
        Assert.Throws<InvalidDataException>(() => ProcessSessionScriptHost.ValidateResult(new(false, [new("dance", "x")]) { Id = "a" }));
        Assert.Throws<InvalidDataException>(() => ProcessSessionScriptHost.ValidateResult(new(false, Enumerable.Repeat(new ScriptAction("echo", "x"), 33).ToArray()) { Id = "a" }));
        Assert.Throws<InvalidDataException>(() => ProcessSessionScriptHost.ValidateResult(new(false, [new("report", "1bad")]) { Id = "a" }));
    }
}
