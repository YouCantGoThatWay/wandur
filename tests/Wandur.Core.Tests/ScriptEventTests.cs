using Wandur.Core.Scripting;

namespace Wandur.Core.Tests;

public sealed class ScriptEventTests
{
    [Fact]
    public void EventConstantsDispatchLineAndGmcpWithoutExposingHostApis()
    {
        var engine = new JavaScriptEngine();
        Assert.Null(engine.Load("""
            if (!Object.isFrozen(Events)) throw Error('Events must be frozen');
            const descriptor = Object.getOwnPropertyDescriptor(globalThis, 'Events');
            if (descriptor.writable || descriptor.configurable) throw Error('Events must be permanent');
            if (Object.keys(Events).sort().join(',') !== 'Gmcp,Key,Line,Msdp') throw Error('Unexpected event constants');
            if (typeof System !== 'undefined' || typeof require !== 'undefined' || typeof process !== 'undefined' || typeof fetch !== 'undefined')
                throw Error('Host APIs must remain unavailable');
            mud.on(Events.Line, event => mud.echo(event.text));
            mud.on(Events.Gmcp, event => mud.echo(event.package + ':' + event.data.hp));
            """).Error);
        Assert.Equal("Hello", Assert.Single(engine.Dispatch(new("line", "Hello")).Actions).Text);
        Assert.Equal("Char.Vitals:42", Assert.Single(engine.Dispatch(new("gmcp", "Char.Vitals {\"hp\":42}")).Actions).Text);
    }

    [Theory]
    [InlineData("Events.Line = 'other';")]
    [InlineData("Events.Gmcp = 'other';")]
    [InlineData("Events.Other = 'other';")]
    [InlineData("globalThis.Events = {};")]
    [InlineData("delete globalThis.Events;")]
    [InlineData("Object.defineProperty(globalThis, 'Events', { value: {} });")]
    public void EventConstantsCannotBeChangedInStrictMode(string mutation)
    {
        var engine = new JavaScriptEngine();
        var result = engine.Load("'use strict'; if (typeof Events === 'undefined') throw Error('Missing Events'); " + mutation);
        Assert.NotNull(result.Error);
        Assert.DoesNotContain("Missing Events", result.Error);
        Assert.False(engine.IsRunning);
    }

    [Fact]
    public void LineSubscribersReceiveTextBeforeMatchingTriggers()
    {
        var engine = new JavaScriptEngine();
        Assert.Null(engine.Load("""
            mud.on('line', event => mud.echo('first: ' + event.text));
            mud.on('line', event => mud.echo('second: ' + event.text));
            mud.trigger(/^Hello (.+)$/, match => mud.send('say Hi ' + match[1]));
            """).Error);
        var result = engine.Dispatch(new("line", "Hello traveler"));
        Assert.Null(result.Error);
        Assert.Equal(new[] { new ScriptAction("echo", "first: Hello traveler"), new ScriptAction("echo", "second: Hello traveler"), new ScriptAction("send", "say Hi traveler") }, result.Actions);
    }

    [Theory]
    [InlineData("Char.Vitals {\"hp\":42}", "Char.Vitals:42")]
    [InlineData("Core.Ping", "Core.Ping:null")]
    [InlineData("Char.Vitals {\n  \"hp\": 42\n}", "Char.Vitals:42")]
    public void GmcpSubscribersReceiveStructuredData(string message, string expected)
    {
        var engine = new JavaScriptEngine();
        Assert.Null(engine.Load("mud.on('gmcp', e => mud.echo(e.package + ':' + (e.data === null ? null : e.data.hp)));").Error);
        var result = engine.Dispatch(new("gmcp", message));
        Assert.Null(result.Error);
        Assert.Equal(expected, Assert.Single(result.Actions).Text);
    }

    [Theory]
    [InlineData("Char.Vitals {bad}")]
    [InlineData(" {\"hp\":1}")]
    [InlineData("")]
    public void MalformedGmcpIsIgnored(string message)
    {
        var engine = new JavaScriptEngine();
        engine.Load("mud.on('gmcp', e => mud.send('look'));");
        var result = engine.Dispatch(new("gmcp", message));
        Assert.Null(result.Error); Assert.Empty(result.Actions); Assert.True(engine.IsRunning);
    }

    [Theory]
    [InlineData("mud.on('unknown', () => {});")]
    [InlineData("mud.on('line', async () => {});")]
    [InlineData("mud.on('line', 'bad');")]
    [InlineData("for(let i=0;i<257;i++) mud.on('line', () => {});")]
    public void InvalidSubscriptionsAreRejected(string source)
    {
        Assert.NotNull(new JavaScriptEngine().Load(source).Error);
    }

    [Fact]
    public void FailingSubscriberDiscardsActionsFromEarlierSubscribers()
    {
        var engine = new JavaScriptEngine();
        engine.Load("mud.on('line', e => mud.send('look')); mud.on('line', e => { throw Error('failure'); });");
        var result = engine.Dispatch(new("line", "test"));
        Assert.NotNull(result.Error); Assert.Empty(result.Actions); Assert.False(engine.IsRunning);
    }
}
