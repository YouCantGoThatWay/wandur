using System.Text;
using Wandur.Core.Scripting;

namespace Wandur.Core.Tests;

public sealed class ScriptStateAndPanelTests
{
    private static JavaScriptEngine Loaded(string source, bool restrictedSend = false)
    {
        var engine = new JavaScriptEngine();
        Assert.Null(engine.Load(source, restrictedSend).Error);
        return engine;
    }

    // Load already flushes the actions a script declares at the top level.
    private static IReadOnlyList<ScriptAction> Declared(string source)
    {
        var engine = new JavaScriptEngine();
        var result = engine.Load(source);
        Assert.Null(result.Error);
        return result.Actions;
    }

    private static ScriptEvent Msdp(string variable, string json) => new("msdp", $$"""{"variable":"{{variable}}","value":{{json}}}""");

    [Fact]
    public void MsdpSubscribersReceiveDecodedVariableUpdates()
    {
        var engine = Loaded("mud.on(Events.Msdp, e => mud.echo(e.variable + '=' + JSON.stringify(e.value)));");
        Assert.Equal("SHIPHULL=\"100\"", Assert.Single(engine.Dispatch(Msdp("SHIPHULL", "\"100\"")).Actions).Text);
        Assert.Equal("AFFECTS=[\"haste\",\"sanctuary\"]", Assert.Single(engine.Dispatch(Msdp("AFFECTS", "[\"haste\",\"sanctuary\"]")).Actions).Text);
        Assert.Equal("ROOM={\"VNUM\":\"12\"}", Assert.Single(engine.Dispatch(Msdp("ROOM", "{\"VNUM\":\"12\"}")).Actions).Text);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"value\":1}")]
    [InlineData("[]")]
    public void MalformedMsdpMessagesAreIgnored(string text)
    {
        var engine = Loaded("mud.on(Events.Msdp, () => mud.send('look'));");
        var result = engine.Dispatch(new("msdp", text));
        Assert.Null(result.Error);
        Assert.Empty(result.Actions);
        Assert.True(engine.IsRunning);
    }

    [Fact]
    public void StateGetReflectsTheLatestGmcpAndMsdpValuesAndIsUndefinedForUnknownPaths()
    {
        var engine = Loaded("""
            mud.alias(/^probe$/, () => {
                mud.echo(String(mud.state.get('gmcp.Char.Vitals.hp')));
                mud.echo(String(mud.state.get('msdp.SHIPHULL')));
                mud.echo(String(mud.state.get('msdp.NOPE')));
                mud.echo(String(mud.state.get('gmcp.Char.Nope.hp')));
                mud.echo(JSON.stringify(mud.state.snapshot()));
            });
            """);
        Assert.Empty(engine.Dispatch(new("gmcp", "Char.Vitals {\"hp\":42,\"mana\":10}")).Actions);
        Assert.Empty(engine.Dispatch(new("gmcp", "Char.Vitals {\"hp\":7,\"mana\":10}")).Actions);
        Assert.Empty(engine.Dispatch(Msdp("SHIPHULL", "\"1200\"")).Actions);
        var actions = engine.Dispatch(new("command", "probe")).Actions;
        // The one unknown MSDP read asks the world for that variable; the GMCP miss asks for nothing.
        Assert.Equal(new ScriptAction("report", "NOPE"), Assert.Single(actions, a => a.Kind == "report"));
        var echoes = actions.Where(a => a.Kind == "echo").Select(a => a.Text).ToArray();
        Assert.Equal("7", echoes[0]);
        Assert.Equal("1200", echoes[1]);
        Assert.Equal("undefined", echoes[2]);
        Assert.Equal("undefined", echoes[3]);
        Assert.Equal("""{"gmcp":{"Char":{"Vitals":{"hp":7,"mana":10}}},"msdp":{"SHIPHULL":"1200"}}""", echoes[4]);
    }

    [Fact]
    public void StateSnapshotIsACopyThatScriptsCannotUseToRewriteTheCache()
    {
        var engine = Loaded("""
            mud.alias(/^probe$/, () => {
                const copy = mud.state.get('gmcp.Char.Vitals');
                copy.hp = 999;
                mud.echo(String(mud.state.get('gmcp.Char.Vitals.hp')));
            });
            """);
        engine.Dispatch(new("gmcp", "Char.Vitals {\"hp\":5}"));
        Assert.Equal("5", Assert.Single(engine.Dispatch(new("command", "probe")).Actions).Text);
    }

    [Fact]
    public void StateSeedSentBeforeLoadIsVisibleToTopLevelCodeAndFiresNoCallbacks()
    {
        var engine = new JavaScriptEngine();
        var seeded = engine.Dispatch(new("state", """{"gmcp":{"Char.Vitals":{"hp":7},"9bad":{"x":1}},"msdp":{"HEALTH":"100","AFFECTS":["haste"],"__proto__":{"polluted":true}}}"""));
        Assert.Null(seeded.Error);
        Assert.Empty(seeded.Actions);
        var loaded = engine.Load("""
            mud.on(Events.Msdp, () => mud.echo("msdp fired"));
            mud.on(Events.Gmcp, () => mud.echo("gmcp fired"));
            mud.echo("health:" + mud.state.get("msdp.HEALTH") + " hp:" + mud.state.get("gmcp.Char.Vitals.hp") + " affects:" + mud.state.get("msdp.AFFECTS").join("+"));
            mud.echo(JSON.stringify(mud.state.snapshot()));
            """);
        Assert.Null(loaded.Error);
        Assert.Equal(
        [
            new ScriptAction("echo", "health:100 hp:7 affects:haste"),
            new ScriptAction("echo", """{"gmcp":{"Char":{"Vitals":{"hp":7}}},"msdp":{"HEALTH":"100","AFFECTS":["haste"]}}""")
        ], loaded.Actions);
    }

    [Fact]
    public void StateSeedWhileRunningAppliesTheWorkerLimitsAndFiresNoCallbacks()
    {
        var engine = Loaded("""
            mud.on(Events.Msdp, () => mud.echo("fired"));
            mud.alias(/^count$/, () => mud.echo(String(Object.keys(mud.state.snapshot().msdp).length) + ":" + mud.state.get("msdp.V0")));
            """);
        var entries = string.Join(",", Enumerable.Range(0, 520).Select(i => $"\"V{i}\":\"{i}\""));
        var seeded = engine.Dispatch(new("state", "{\"msdp\":{" + entries + "}}"));
        Assert.Null(seeded.Error);
        Assert.Empty(seeded.Actions);
        Assert.Equal(new ScriptAction("echo", "512:0"), Assert.Single(engine.Dispatch(new("command", "count")).Actions));
        Assert.Empty(engine.Dispatch(new("state", "not json")).Actions);
        Assert.Empty(engine.Dispatch(new("state", "[]")).Actions);
        Assert.True(engine.IsRunning);
    }

    [Fact]
    public void ReadingAnUnknownMsdpVariableReportsItOnceAndInvalidNamesNever()
    {
        var engine = Loaded("mud.alias(/^read (.+)$/, m => mud.echo(String(mud.state.get(m[1]))));");
        Assert.Equal([new ScriptAction("report", "LEVELCOMBAT"), new ScriptAction("echo", "undefined")],
            engine.Dispatch(new("command", "read msdp.LEVELCOMBAT")).Actions);
        Assert.Equal([new ScriptAction("echo", "undefined")], engine.Dispatch(new("command", "read msdp.LEVELCOMBAT")).Actions);
        Assert.Equal([new ScriptAction("report", "ROOM"), new ScriptAction("echo", "undefined")],
            engine.Dispatch(new("command", "read msdp.ROOM.VNUM")).Actions);
        foreach (var path in new[] { "msdp.1BAD", "msdp.bad-name", "msdp.", "gmcp.Char.Vitals", "msdp." + new string('X', 129) })
            Assert.Equal([new ScriptAction("echo", "undefined")], engine.Dispatch(new("command", "read " + path)).Actions);
        Assert.Equal([new ScriptAction("echo", "[object Object]")], engine.Dispatch(new("command", "read msdp")).Actions);
        engine.Dispatch(Msdp("HEALTH", "\"5\""));
        Assert.Equal([new ScriptAction("echo", "5")], engine.Dispatch(new("command", "read msdp.HEALTH")).Actions);
        Assert.Equal([new ScriptAction("echo", "undefined")], engine.Dispatch(new("command", "read msdp.HEALTH.sub")).Actions);
    }

    [Fact]
    public void AScriptMayAskForAtMostSixtyFourVariables()
    {
        var engine = Loaded("mud.alias(/^probe$/, () => { for (let i = 0; i < 70; i++) mud.state.get('msdp.VAR_' + i); });");
        var result = engine.Dispatch(new("command", "probe"));
        Assert.Null(result.Error);
        Assert.Equal(JavaScriptEngine.MaximumReportsPerScript, result.Actions.Count);
        Assert.All(result.Actions, action => Assert.Equal("report", action.Kind));
        Assert.Empty(engine.Dispatch(new("command", "probe")).Actions);
    }

    [Fact]
    public void PanelDeclarationsBecomePanelActionsWithStableShapes()
    {
        var actions = Declared("""
            const p = mud.panel("ship", { title: "Ship", dock: "right" });
            p.gauge("hull", { label: "Hull", value: 0, max: 100 });
            p.label("system", { text: "In orbit" });
            p.button("flee", { label: "Flee", onClick: () => mud.send("flee") });
            p.toggle("auto", { label: "Auto repair", value: false, onChange: () => {} });
            p.input("say", { placeholder: "Say...", onSubmit: text => mud.send("say " + text) });
            p.list("crew", { title: "Crew", items: ["Ann", "Bo"], onSelect: item => mud.send("look " + item) });
            p.table("skills", { columns: ["Skill", "Level"], rows: [["Piloting", 12]] });
            p.separator("s1");
            p.group("g1", { title: "Combat", children: ["flee", "auto"] });
            p.remove("system");
            p.hide(); p.show();
            """);
        Assert.All(actions, action => Assert.Equal("panel", action.Kind));
        Assert.Equal(
        [
            """{"panel":"ship","action":"create","title":"Ship","dock":"right"}""",
            """{"panel":"ship","action":"widget","widget":"hull","kind":"gauge","props":{"label":"Hull","value":0,"max":100}}""",
            """{"panel":"ship","action":"widget","widget":"system","kind":"label","props":{"text":"In orbit"}}""",
            """{"panel":"ship","action":"widget","widget":"flee","kind":"button","props":{"label":"Flee"}}""",
            """{"panel":"ship","action":"widget","widget":"auto","kind":"toggle","props":{"label":"Auto repair","value":false}}""",
            """{"panel":"ship","action":"widget","widget":"say","kind":"input","props":{"placeholder":"Say..."}}""",
            """{"panel":"ship","action":"widget","widget":"crew","kind":"list","props":{"title":"Crew","items":["Ann","Bo"]}}""",
            """{"panel":"ship","action":"widget","widget":"skills","kind":"table","props":{"columns":["Skill","Level"],"rows":[["Piloting","12"]]}}""",
            """{"panel":"ship","action":"widget","widget":"s1","kind":"separator","props":{}}""",
            """{"panel":"ship","action":"widget","widget":"g1","kind":"group","props":{"title":"Combat","children":["flee","auto"]}}""",
            """{"panel":"ship","action":"remove","widget":"system"}""",
            """{"panel":"ship","action":"hide"}""",
            """{"panel":"ship","action":"show"}"""
        ], actions.Select(a => a.Text));
        foreach (var action in actions) Assert.NotNull(ScriptPanelAction.Parse(action.Text));
    }

    [Fact]
    public void ReusingAPanelDoesNotRepeatItsDeclarationAndClosingRemovesIt()
    {
        var engine = new JavaScriptEngine();
        var first = engine.Load("""
            mud.alias(/^again$/, () => { mud.panel("ship", { title: "Ship" }).label("a", { text: "x" }); });
            mud.alias(/^rename$/, () => { mud.panel("ship", { title: "Freighter" }); });
            mud.alias(/^done$/, () => { mud.panel("ship").close(); });
            mud.panel("ship", { title: "Ship" });
            """);
        Assert.Equal("""{"panel":"ship","action":"create","title":"Ship","dock":"right"}""", Assert.Single(first.Actions).Text);
        Assert.Equal("""{"panel":"ship","action":"widget","widget":"a","kind":"label","props":{"text":"x"}}""",
            Assert.Single(engine.Dispatch(new("command", "again")).Actions).Text);
        Assert.Equal("""{"panel":"ship","action":"create","title":"Freighter","dock":"right"}""",
            Assert.Single(engine.Dispatch(new("command", "rename")).Actions).Text);
        Assert.Equal("""{"panel":"ship","action":"close"}""", Assert.Single(engine.Dispatch(new("command", "done")).Actions).Text);
    }

    [Theory]
    [InlineData("for (let i = 0; i < 9; i++) mud.panel('p' + i);", "Maximum 8 panels per script.")]
    [InlineData("mud.panel('p').label('a', { text: 'x'.repeat(4097) });", "exceeds 4096 characters")]
    [InlineData("mud.panel('p').list('a', { items: new Array(501).fill('x') });", "exceeds 500 items")]
    [InlineData("mud.panel('p').table('a', { rows: new Array(501).fill(['x']) });", "rows exceeds 500 items.")]
    public void PanelLimitViolationsStopTheScriptWithAnErrorAndNoActions(string source, string expected)
    {
        var engine = new JavaScriptEngine();
        var result = engine.Load(source);
        Assert.NotNull(result.Error);
        Assert.Contains(expected, result.Error);
        Assert.Empty(result.Actions);
        Assert.False(engine.IsRunning);
    }

    [Fact]
    public void MoreThanSixtyFourWidgetsOnOnePanelIsRejected()
    {
        var engine = Loaded("let n = 0; mud.alias(/^add$/, () => { const p = mud.panel('p'); for (let i = 0; i < 25; i++) p.label('w' + (n++), { text: 'x' }); });");
        for (var batch = 0; batch < 2; batch++) Assert.Null(engine.Dispatch(new("command", "add")).Error);
        var result = engine.Dispatch(new("command", "add"));
        Assert.Contains("Maximum 64 widgets per panel.", result.Error);
        Assert.Empty(result.Actions);
        Assert.False(engine.IsRunning);
    }

    [Fact]
    public void MoreThanThirtyTwoPanelActionsInOneEventIsRejected()
    {
        var engine = Loaded("mud.alias(/^flood$/, () => { const p = mud.panel('p'); for (let i = 0; i < 40; i++) p.label('w' + i, { text: 'x' }); });");
        var result = engine.Dispatch(new("command", "flood"));
        Assert.Contains("Maximum 32 panel actions per event.", result.Error);
        Assert.Empty(result.Actions);
    }

    [Fact]
    public void PanelCallbackEventsInvokeTheRegisteredHandler()
    {
        var engine = Loaded("""
            const p = mud.panel("ship");
            p.button("flee", { label: "Flee", onClick: () => mud.send("flee") });
            p.toggle("auto", { label: "Auto", value: false, onChange: on => mud.echo("auto:" + on) });
            p.input("say", { onSubmit: text => mud.send("say " + text) });
            p.list("crew", { items: ["Ann"], onSelect: item => mud.send("look " + item) });
            """);
        engine.Dispatch(new("flush"));
        Assert.Equal(new ScriptAction("send", "flee"),
            Assert.Single(engine.Dispatch(new("panel", ScriptPanelAction.EventJson("ship", "flee", "click"))).Actions));
        Assert.Equal(new ScriptAction("echo", "auto:true"),
            Assert.Single(engine.Dispatch(new("panel", ScriptPanelAction.EventJson("ship", "auto", "change", flag: true))).Actions));
        Assert.Equal(new ScriptAction("send", "say hello"),
            Assert.Single(engine.Dispatch(new("panel", ScriptPanelAction.EventJson("ship", "say", "submit", text: "hello"))).Actions));
        Assert.Equal(new ScriptAction("send", "look Ann"),
            Assert.Single(engine.Dispatch(new("panel", ScriptPanelAction.EventJson("ship", "crew", "select", text: "Ann"))).Actions));
        Assert.Empty(engine.Dispatch(new("panel", ScriptPanelAction.EventJson("ship", "gone", "click"))).Actions);
    }

    [Fact]
    public void RemovingAWidgetDropsItsCallback()
    {
        var engine = Loaded("""
            const p = mud.panel("ship");
            p.button("flee", { onClick: () => mud.send("flee") });
            mud.alias(/^drop$/, () => p.remove("flee"));
            """);
        engine.Dispatch(new("flush"));
        Assert.Single(engine.Dispatch(new("panel", ScriptPanelAction.EventJson("ship", "flee", "click"))).Actions);
        engine.Dispatch(new("command", "drop"));
        Assert.Empty(engine.Dispatch(new("panel", ScriptPanelAction.EventJson("ship", "flee", "click"))).Actions);
    }

    [Fact]
    public void RestrictedSendAllowsAliasesAndButtonClicksOnly()
    {
        const string source = """
            mud.alias(/^go$/, () => mud.send("north"));
            mud.trigger(/^attacked$/, () => mud.send("flee"));
            mud.on(Events.Gmcp, () => mud.send("score"));
            mud.every(1, () => mud.send("tick"));
            const p = mud.panel("ship");
            p.button("flee", { onClick: () => mud.send("flee") });
            p.toggle("auto", { onChange: () => mud.send("repair") });
            """;
        var engine = Loaded(source, restrictedSend: true);
        engine.Dispatch(new("flush"));
        Assert.Equal(new ScriptAction("send", "north"), Assert.Single(engine.Dispatch(new("command", "go")).Actions));
        Assert.Equal(new ScriptAction("send", "flee"),
            Assert.Single(engine.Dispatch(new("panel", ScriptPanelAction.EventJson("ship", "flee", "click"))).Actions));
        var refused = engine.Dispatch(new("line", "attacked"));
        Assert.Contains("Pack send policy", refused.Error);
        Assert.Empty(refused.Actions);
        Assert.False(engine.IsRunning);

        foreach (var (kind, text) in new[] { ("gmcp", "Char.Vitals {\"hp\":1}"), ("tick", ""),
            ("panel", ScriptPanelAction.EventJson("ship", "auto", "change", flag: true)) })
        {
            var restricted = Loaded(source, restrictedSend: true);
            restricted.Dispatch(new("flush"));
            var result = restricted.Dispatch(new(kind, text, 5000));
            Assert.Contains("Pack send policy", result.Error);
            Assert.Empty(result.Actions);
        }
        var unrestricted = Loaded(source);
        unrestricted.Dispatch(new("flush"));
        Assert.Equal(new ScriptAction("send", "flee"), Assert.Single(unrestricted.Dispatch(new("line", "attacked")).Actions));
    }

    [Fact]
    public void RestrictedTopLevelSendIsRefusedBeforeTheScriptStarts()
    {
        var engine = new JavaScriptEngine();
        var result = engine.Load("mud.send('look');", restrictedSend: true);
        Assert.Contains("Pack send policy", result.Error);
        Assert.False(engine.IsRunning);
        Assert.Null(new JavaScriptEngine().Load("mud.send('look');").Error);
    }

    [Fact]
    public void MsdpPayloadsDecodeIntoOneEventPerVariable()
    {
        var payload = new List<byte>();
        void Variable(string name, params byte[] value) { payload.Add(1); payload.AddRange(Encoding.UTF8.GetBytes(name)); payload.Add(2); payload.AddRange(value); }
        Variable("SHIPHULL", Encoding.UTF8.GetBytes("1200"));
        Variable("AFFECTS", [5, 2, .. Encoding.UTF8.GetBytes("haste"), 2, .. Encoding.UTF8.GetBytes("sanctuary"), 6]);
        Variable("ROOM", [3, 1, .. Encoding.UTF8.GetBytes("VNUM"), 2, .. Encoding.UTF8.GetBytes("42"), 4]);
        var events = MsdpScriptEvents.Decode([.. payload]);
        Assert.Equal(["msdp", "msdp", "msdp"], events.Select(e => e.Kind));
        Assert.Equal(
        [
            """{"variable":"SHIPHULL","value":"1200"}""",
            """{"variable":"AFFECTS","value":["haste","sanctuary"]}""",
            """{"variable":"ROOM","value":{"VNUM":"42"}}"""
        ], events.Select(e => e.Text));
        Assert.Empty(MsdpScriptEvents.Decode([9, 9, 9]));
        Assert.Empty(MsdpScriptEvents.Decode([]));
    }

    [Theory]
    [InlineData("""{"panel":"ship","action":"widget","widget":"a","kind":"nope","props":{}}""")]
    [InlineData("""{"panel":"ship","action":"nope"}""")]
    [InlineData("""{"panel":"bad id","action":"show"}""")]
    [InlineData("""{"panel":"ship","action":"create","title":"Ship","dock":"middle"}""")]
    [InlineData("[]")]
    [InlineData("{")]
    public void InvalidPanelInstructionsAreRejectedByTheClientParser(string json)
        => Assert.Throws<FormatException>(() => ScriptPanelAction.Parse(json));
}
