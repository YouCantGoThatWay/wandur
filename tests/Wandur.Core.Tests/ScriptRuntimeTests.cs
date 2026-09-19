using System.Diagnostics;
using System.Text.Json;
using Wandur.Core.Scripting;

namespace Wandur.Core.Tests;

public sealed class ScriptRuntimeTests
{
    [Fact]
    public async Task WorkerLoadsAndDispatchesJsonWithoutWritingOtherOutput()
    {
        var input = JsonSerializer.Serialize(new ScriptEvent("load", "mud.alias('x', () => mud.send('look'))")) + "\n" +
                    JsonSerializer.Serialize(new ScriptEvent("command", "x")) + "\n";
        var output = new StringWriter();
        await ScriptWorker.RunAsync(new StringReader(input), output);
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Null(JsonSerializer.Deserialize<ScriptResult>(lines[0])!.Error);
        Assert.Equal(new ScriptAction("send", "look"), Assert.Single(JsonSerializer.Deserialize<ScriptResult>(lines[1])!.Actions));
    }

    [Fact]
    public async Task RealWorkerKeepsStateAndStopsAfterScriptFailure()
    {
        await using var runtime = CreateRuntime();
        Assert.Null((await runtime.LoadAsync("let n=0; mud.alias('x', () => mud.echo(String(++n))); mud.alias('bad', () => { mud.send('discard'); throw Error('boom') });")).Error);
        Assert.True(runtime.IsRunning);
        Assert.Equal("1", Assert.Single((await runtime.DispatchAsync(new("command", "x"))).Actions).Text);
        Assert.Equal("2", Assert.Single((await runtime.DispatchAsync(new("command", "x"))).Actions).Text);
        var failed = await runtime.DispatchAsync(new("command", "bad"));
        Assert.Contains("boom", failed.Error);
        Assert.Empty(failed.Actions);
        Assert.False(runtime.IsRunning);
    }

    [Fact]
    public async Task StopInterruptsPendingLoadAndInvalidatesItsEffects()
    {
        await using var runtime = CreateRuntime();
        var pending = runtime.LoadAsync("mud.send('discard'); while(true) {}");
        Assert.True(runtime.IsRunning);
        var timer = Stopwatch.StartNew();
        runtime.Stop();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Empty(result.Actions);
        Assert.False(runtime.IsRunning);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task RealWorkerCarriesPanelActionsAndHonoursTheRestrictedSendPolicy()
    {
        await using var runtime = CreateRuntime();
        var loaded = await runtime.LoadAsync("""
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
        var click = await runtime.DispatchAsync(new("panel", ScriptPanelAction.EventJson("ship", "flee", "click")));
        Assert.Equal(new ScriptAction("send", "flee"), Assert.Single(click.Actions));
        var refused = await runtime.DispatchAsync(new("line", "hit"));
        Assert.Contains("Pack send policy", refused.Error);
        Assert.Empty(refused.Actions);
        Assert.False(runtime.IsRunning);
    }

    private static IScriptRuntime CreateRuntime()
    {
        var worker = Path.Combine(AppContext.BaseDirectory, "ScriptWorkerHost", "Wandur.ScriptWorkerHost.dll");
        Assert.True(File.Exists(worker), $"Worker test host is missing: {worker}");
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        return new ProcessScriptRuntimeFactory(dotnet, worker).Create();
    }

    [Fact]
    public async Task RealWorkerPreservesUnicodeInSourceEventsAndActions()
    {
        await using var runtime = CreateRuntime();
        var loaded = await runtime.LoadAsync("mud.alias(/^(.+)$/, m => { mud.echo('こんにちは 🌍 ' + m[1]); mud.send('examiner café'); });");
        Assert.Null(loaded.Error);
        var result = await runtime.DispatchAsync(new("command", "Grüße 世界 🧙"));
        Assert.Null(result.Error);
        Assert.True(result.Handled);
        Assert.Equal(new ScriptAction("echo", "こんにちは 🌍 Grüße 世界 🧙"), result.Actions[0]);
        Assert.Equal(new ScriptAction("send", "examiner café"), result.Actions[1]);
    }

    [Fact]
    public async Task ParentDeadlineTerminatesAnUnresponsiveWorker()
    {
        await using var runtime = CreateRuntime();
        Assert.Null((await runtime.LoadAsync("// test-host-unresponsive")).Error);
        var result = await runtime.DispatchAsync(new("command", "look")).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(result.Error);
        Assert.Empty(result.Actions);
        Assert.False(runtime.IsRunning);
    }

    [Fact]
    public async Task StopCancelsActiveAndQueuedRequestsWithoutWaitingForWorker()
    {
        await using var runtime = CreateRuntime();
        Assert.Null((await runtime.LoadAsync("// test-host-unresponsive")).Error);
        var active = runtime.DispatchAsync(new("command", "look"));
        var queued = runtime.DispatchAsync(new("tick", ElapsedMilliseconds: 1000));
        Assert.False(active.IsCompleted);
        var timer = Stopwatch.StartNew();
        runtime.Stop();
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(1));
        var results = await Task.WhenAll(active, queued).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.All(results, result => Assert.Empty(result.Actions));
        Assert.False(runtime.IsRunning);
    }

    [Fact]
    public async Task MalformedWorkerInputReturnsOneErrorAndExits()
    {
        var output = new StringWriter();
        await ScriptWorker.RunAsync(new StringReader("not json\n"), output);
        var result = JsonSerializer.Deserialize<ScriptResult>(output.ToString());
        Assert.NotNull(result!.Error);
        Assert.Empty(result.Actions);
    }
}
