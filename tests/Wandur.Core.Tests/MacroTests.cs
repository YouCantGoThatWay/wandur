using Wandur.Core.Scripting;
using Xunit;

namespace Wandur.Core.Tests;

public sealed class MacroTests
{
    [Fact]
    public void TriggerMatchesLiteralTextAndSafelySendsCommandsInOrder()
    {
        var macro = new MacroDefinition(MacroKind.Trigger, "[Hungry]", "eat bread\nsay \"thanks\"; mud.send('oops')", IgnoreCase: true);
        var engine = new JavaScriptEngine();
        Assert.Null(engine.Load(MacroCompiler.Compile(macro)).Error);
        Assert.Empty(engine.Dispatch(new("line", "Hungry")).Actions);
        var result = engine.Dispatch(new("line", "You are [HUNGRY] now"));
        Assert.Null(result.Error);
        Assert.Equal(new[] { "eat bread", "say \"thanks\"; mud.send('oops')" }, result.Actions.Select(a => a.Text));
    }

    [Fact]
    public void AliasConsumesOnlyTheExactCommand()
    {
        var engine = new JavaScriptEngine();
        Assert.Null(engine.Load(MacroCompiler.Compile(new(MacroKind.Alias, "h+", "look"))).Error);
        Assert.False(engine.Dispatch(new("command", "hhh")).Handled);
        Assert.False(engine.Dispatch(new("command", "h+ now")).Handled);
        var result = engine.Dispatch(new("command", "h+"));
        Assert.True(result.Handled);
        Assert.Equal("look", Assert.Single(result.Actions).Text);
    }

    [Fact]
    public void TimerWaitsAndDoesNotCatchUpWithABurst()
    {
        var engine = new JavaScriptEngine();
        Assert.Empty(engine.Load(MacroCompiler.Compile(new(MacroKind.Timer, "", "score", IntervalSeconds: 10))).Actions);
        Assert.Empty(engine.Dispatch(new("tick", ElapsedMilliseconds: 9999)).Actions);
        Assert.Single(engine.Dispatch(new("tick", ElapsedMilliseconds: 10000)).Actions);
        Assert.Single(engine.Dispatch(new("tick", ElapsedMilliseconds: 90000)).Actions);
        Assert.Empty(engine.Dispatch(new("tick", ElapsedMilliseconds: 90001)).Actions);
    }

    [Fact]
    public void ShortcutRespondsOnlyToItsFunctionKey()
    {
        var engine = new JavaScriptEngine();
        Assert.Null(engine.Load(MacroCompiler.Compile(new(MacroKind.Shortcut, "F4", "score"))).Error);
        Assert.Empty(engine.Dispatch(new("key", "F5")).Actions);
        Assert.Equal("score", Assert.Single(engine.Dispatch(new("key", "F4")).Actions).Text);
    }

    [Fact]
    public void InvalidRulesAreRejectedBeforeTheyCanReplaceSavedRules()
    {
        Assert.Throws<ArgumentException>(() => MacroCompiler.Compile(new(MacroKind.Trigger, "", "look")));
        Assert.Throws<ArgumentException>(() => MacroCompiler.Compile(new(MacroKind.Timer, "", "look", IntervalSeconds: 0)));
        Assert.Throws<ArgumentException>(() => MacroCompiler.Compile(new(MacroKind.Shortcut, "Enter", "look")));
        Assert.Throws<ArgumentException>(() => MacroCompiler.Compile(new(MacroKind.Alias, "h", "say hi\u001b")));
    }
}
