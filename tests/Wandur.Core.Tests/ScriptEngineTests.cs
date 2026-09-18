using Wandur.Core.Scripting;

namespace Wandur.Core.Tests;

public sealed class ScriptEngineTests
{
    [Fact]
    public void FirstMatchingAliasConsumesCommandAndReceivesCaptures()
    {
        var engine = new JavaScriptEngine();
        Assert.Null(engine.Load("mud.alias(/^hit (.+)$/, m => mud.send('kill ' + m[1])); mud.alias(/hit/, () => mud.send('wrong'));").Error);
        var result = engine.Dispatch(new("command", "hit goblin"));
        Assert.True(result.Handled);
        Assert.Equal(new ScriptAction("send", "kill goblin"), Assert.Single(result.Actions));
        Assert.False(engine.Dispatch(new("command", "look")).Handled);
    }

    [Fact]
    public void AllTriggersRunWithPersistentStateAndGlobalPatternsReset()
    {
        var engine = new JavaScriptEngine();
        engine.Load("let count = 0; mud.trigger(/orc/g, m => mud.echo(m[0] + ++count)); mud.trigger('orc', () => mud.send('look'));");
        Assert.Equal(2, engine.Dispatch(new("line", "orc")).Actions.Count);
        Assert.Equal("orc2", engine.Dispatch(new("line", "orc")).Actions[0].Text);
    }

    [Fact]
    public void TimersUseElapsedTimeAndDoNotCatchUpAfterPauses()
    {
        var engine = new JavaScriptEngine();
        engine.Load("mud.every(1, () => mud.send('look'));");
        Assert.Empty(engine.Dispatch(new("tick", ElapsedMilliseconds: 999)).Actions);
        Assert.Single(engine.Dispatch(new("tick", ElapsedMilliseconds: 1000)).Actions);
        Assert.Single(engine.Dispatch(new("tick", ElapsedMilliseconds: 10000)).Actions);
        Assert.Empty(engine.Dispatch(new("tick", ElapsedMilliseconds: 10001)).Actions);
    }

    [Fact]
    public void FailedCallbackDiscardsEarlierActionsAndStopsEngine()
    {
        var engine = new JavaScriptEngine();
        engine.Load("mud.trigger('x', () => mud.send('look')); mud.trigger('x', () => {throw new Error('boom')});");
        var result = engine.Dispatch(new("line", "x"));
        Assert.Contains("boom", result.Error);
        Assert.Empty(result.Actions);
        Assert.False(engine.IsRunning);
        Assert.Empty(engine.Dispatch(new("line", "x")).Actions);
    }

    [Theory]
    [InlineData("mud.send('bad\\ncommand')")]
    [InlineData("mud.send('')")]
    [InlineData("mud.send('x'.repeat(4097))")]
    [InlineData("mud.echo('x'.repeat(8193))")]
    [InlineData("for(let i=0;i<33;i++) mud.send('look')")]
    [InlineData("for(let i=0;i<257;i++) mud.alias('x', () => {})")]
    [InlineData("mud.every(0.5, () => {})")]
    [InlineData("mud.trigger('x', async () => {})")]
    [InlineData("System.IO.File.ReadAllText('/etc/passwd')")]
    [InlineData("importNamespace('System')")]
    [InlineData("while(true) {}")]
    public void InvalidOrExcessiveScriptsFailAtomically(string source)
    {
        var engine = new JavaScriptEngine();
        var result = engine.Load("mud.echo('discard me');" + source);
        Assert.NotNull(result.Error);
        Assert.Empty(result.Actions);
        Assert.False(engine.IsRunning);
    }

    [Fact]
    public void SourceIsBoundedAndRunsHaveSeparateGlobals()
    {
        Assert.NotNull(new JavaScriptEngine().Load(new string(' ', 262145)).Error);
        var first = new JavaScriptEngine();
        first.Load("globalThis.secret = 42");
        Assert.Equal("undefined", Assert.Single(new JavaScriptEngine().Load("mud.echo(typeof secret)").Actions).Text);
    }

    [Fact]
    public void ExecutionBudgetResetsForEachEventAndLimitsCallbacks()
    {
        var engine = new JavaScriptEngine();
        Assert.Null(engine.Load("mud.alias('safe', () => { for(let i=0;i<20000;i++) {} mud.send('look'); }); mud.alias('loop', () => { mud.send('discard'); while(true) {} });").Error);
        for (var i = 0; i < 5; i++) Assert.Null(engine.Dispatch(new("command", "safe")).Error);
        var failed = engine.Dispatch(new("command", "loop"));
        Assert.NotNull(failed.Error);
        Assert.Empty(failed.Actions);
    }

    [Fact]
    public void CatastrophicRegexIsBoundedAndDiscardsEffects()
    {
        var engine = new JavaScriptEngine();
        Assert.Null(engine.Load("mud.trigger(/^/, () => mud.echo('discard')); mud.trigger(/(a+)+$/, () => mud.send('look'));").Error);
        var failed = engine.Dispatch(new("line", new string('a', 1000) + "!"));
        Assert.NotNull(failed.Error);
        Assert.Empty(failed.Actions);
    }
}
