using System.Text.Json;
using Wandur.Core.Scripting;

namespace Wandur.Core.Tests;

/// <summary>The generator feeds docs/scripting-reference.json to a model, so it must describe the API
/// the engine actually registers, not an aspiration.</summary>
public sealed class ScriptReferenceDocumentTests
{
    private static JsonElement Reference()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wandur.sln"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var path = Path.Combine(directory.FullName, "docs", "scripting-reference.json");
        Assert.True(File.Exists(path), $"Missing reference document: {path}");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    }

    private static JsonElement EngineApi()
    {
        var engine = new JavaScriptEngine();
        var result = engine.Load("""
            mud.echo(JSON.stringify({
                mud: Object.keys(mud),
                state: Object.keys(mud.state),
                events: Events,
                panel: Object.keys(mud.panel('probe'))
            }));
            """);
        Assert.Null(result.Error);
        var echo = result.Actions.Single(action => action.Kind == "echo");
        return JsonDocument.Parse(echo.Text).RootElement.Clone();
    }

    private static IEnumerable<JsonElement> Members(JsonElement reference, string global)
        => reference.GetProperty("globals").EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == global).GetProperty("members").EnumerateArray();

    [Fact]
    public void EveryDocumentedMudMemberExistsInTheRegisteredApiAndNothingIsMissing()
    {
        var reference = Reference();
        var api = EngineApi();
        var documented = Members(reference, "mud").Select(member => member.GetProperty("name").GetString()!).Order().ToArray();
        var registered = api.GetProperty("mud").EnumerateArray().Select(name => name.GetString()!).Order().ToArray();
        Assert.Equal(registered, documented);

        var state = Members(reference, "mud").Single(member => member.GetProperty("name").GetString() == "state");
        Assert.Equal("namespace", state.GetProperty("kind").GetString());
        Assert.Equal(api.GetProperty("state").EnumerateArray().Select(name => name.GetString()!).Order(),
            state.GetProperty("members").EnumerateArray().Select(member => member.GetProperty("name").GetString()!).Order());
        foreach (var member in Members(reference, "mud").Where(member => member.GetProperty("name").GetString() != "state"))
        {
            Assert.Equal("function", member.GetProperty("kind").GetString());
            Assert.StartsWith("mud." + member.GetProperty("name").GetString() + "(", member.GetProperty("signature").GetString());
        }
    }

    [Fact]
    public void EveryDocumentedEventConstantMatchesTheFrozenEventsObject()
    {
        var reference = Reference();
        var events = EngineApi().GetProperty("events");
        var registered = events.EnumerateObject().ToDictionary(item => item.Name, item => item.Value.GetString()!);
        Assert.Equal(registered.Keys.Order(), Members(reference, "Events").Select(member => member.GetProperty("name").GetString()!).Order());
        foreach (var member in Members(reference, "Events"))
            Assert.Equal(registered[member.GetProperty("name").GetString()!], member.GetProperty("value").GetString());
        Assert.Equal(registered.Values.Order(),
            reference.GetProperty("events").EnumerateArray().Select(item => item.GetProperty("value").GetString()!).Order());
    }

    [Fact]
    public void EveryDocumentedWidgetKindAndPanelMethodExistsOnADeclaredPanel()
    {
        var reference = Reference();
        var panel = reference.GetProperty("panel");
        var registered = EngineApi().GetProperty("panel").EnumerateArray().Select(name => name.GetString()!).ToArray();
        var kinds = panel.GetProperty("widgets").EnumerateArray().Select(widget => widget.GetProperty("kind").GetString()!).ToArray();
        var methods = panel.GetProperty("methods").EnumerateArray().Select(method => method.GetProperty("name").GetString()!).ToArray();
        Assert.Equal(registered.Order(), kinds.Concat(methods).Order());
        Assert.Equal(JavaScriptEngine.PanelWidgetKinds.Order(), kinds.Order());
        foreach (var widget in panel.GetProperty("widgets").EnumerateArray())
            Assert.StartsWith("panel." + widget.GetProperty("kind").GetString() + "(", widget.GetProperty("signature").GetString());
    }

    [Fact]
    public void DocumentedPanelActionsCallbackEventsAndLimitsMatchTheContracts()
    {
        var reference = Reference();
        var panel = reference.GetProperty("panel");
        Assert.Equal(ScriptPanelAction.Actions.Order(),
            panel.GetProperty("actions").EnumerateArray().Select(action => action.GetProperty("action").GetString()!).Order());
        var callbacks = panel.GetProperty("widgets").EnumerateArray()
            .SelectMany(widget => widget.GetProperty("callbacks").EnumerateArray())
            .Select(callback => callback.GetProperty("event").GetString()!).Distinct().Order();
        Assert.Equal(ScriptPanelAction.EventNames.Order(), callbacks);

        var limits = reference.GetProperty("limits");
        Assert.Equal(ScriptPanelAction.MaximumPanels, limits.GetProperty("panelsPerScript").GetInt32());
        Assert.Equal(ScriptPanelAction.MaximumWidgets, limits.GetProperty("widgetsPerPanel").GetInt32());
        Assert.Equal(ScriptPanelAction.MaximumActionsPerEvent, limits.GetProperty("panelActionsPerEvent").GetInt32());
        Assert.Equal(ScriptPanelAction.MaximumStringCharacters, limits.GetProperty("propertyStringCharacters").GetInt32());
        Assert.Equal(ScriptPanelAction.MaximumCollectionItems, limits.GetProperty("listItems").GetInt32());
        Assert.Equal(ScriptPanelAction.MaximumCollectionItems, limits.GetProperty("tableRows").GetInt32());
        Assert.Equal(ScriptPanelAction.MaximumColumns, limits.GetProperty("tableColumns").GetInt32());
        Assert.Equal(ScriptPanelAction.MaximumChildren, limits.GetProperty("groupChildren").GetInt32());
        Assert.Equal(JavaScriptEngine.MaximumPanelActionCharacters, limits.GetProperty("panelActionCharacters").GetInt32());
        Assert.Equal(JavaScriptEngine.MaximumPanelCharacters, limits.GetProperty("panelCharactersPerEvent").GetInt32());
        Assert.Equal(JavaScriptEngine.MaximumSourceBytes, limits.GetProperty("sourceBytes").GetInt32());
        Assert.Equal(JavaScriptEngine.MaximumEventCharacters, limits.GetProperty("eventCharacters").GetInt32());
        Assert.Equal(WorldScriptLibraryStore.MaximumScripts, limits.GetProperty("scriptsPerWorld").GetInt32());
        Assert.Equal(MsdpScriptEvents.MaximumVariables, limits.GetProperty("msdpVariablesPerPayload").GetInt32());
        Assert.Equal(MsdpScriptEvents.MaximumValueCharacters, limits.GetProperty("msdpValueCharacters").GetInt32());
        Assert.Equal(JavaScriptEngine.MaximumReportsPerScript, limits.GetProperty("msdpReportsPerScript").GetInt32());
        Assert.Equal(JavaScriptEngine.MaximumStateCharacters, limits.GetProperty("stateSeedCharacters").GetInt32());
        // The host cache that seeds a worker keeps the same bounds the worker documents for its own cache.
        Assert.Equal(ProtocolStateCache.MaximumEntries, limits.GetProperty("stateEntriesPerBucket").GetInt32());
        Assert.Equal(ProtocolStateCache.MaximumValueCharacters, limits.GetProperty("stateValueCharacters").GetInt32());
        Assert.Equal(ProtocolStateCache.MaximumBucketCharacters, limits.GetProperty("stateBucketCharacters").GetInt32());
        var report = reference.GetProperty("state").GetProperty("report");
        Assert.Matches(report.GetProperty("namePattern").GetString()!, "LEVELCOMBAT");
        Assert.DoesNotMatch(report.GetProperty("namePattern").GetString()!, "1BAD");
        Assert.True(JavaScriptEngine.IsValidMsdpName("LEVELCOMBAT"));
        Assert.False(JavaScriptEngine.IsValidMsdpName("1BAD"));
    }

    [Fact]
    public void PackProvenanceAndPanelIdentifiersAreDocumentedAsImplemented()
    {
        var reference = Reference();
        var pack = reference.GetProperty("packScripts");
        var provenance = pack.GetProperty("listingFields").EnumerateArray()
            .Single(field => field.GetProperty("name").GetString() == "provenance")
            .GetProperty("values").EnumerateArray().Select(value => value.GetString()!).ToArray();
        Assert.Equal([ScriptPackInfo.Generated, ScriptPackInfo.Reviewed], provenance.Order());
        Assert.All(provenance, value => Assert.True(ScriptPackInfo.IsSupported(value)));
        Assert.False(ScriptPackInfo.IsSupported("community"));
        Assert.Equal(["alias", "panel button click"], pack.GetProperty("sendPolicy").GetProperty("allowedFrom").EnumerateArray().Select(value => value.GetString()!));

        var pattern = reference.GetProperty("panel").GetProperty("identifiers").GetProperty("pattern").GetString()!;
        Assert.Matches(pattern, "ship");
        Assert.Matches(pattern, "hull.left-1_a");
        Assert.DoesNotMatch(pattern, "bad id");
        Assert.DoesNotMatch(pattern, new string('x', 65));
    }

    [Fact]
    public void ReferenceDocumentKeepsItsTopLevelShape()
        => Assert.Equal(["description", "events", "globals", "limits", "packScripts", "panel", "state", "text", "title", "version"],
            Reference().EnumerateObject().Select(member => member.Name).Order());

    [Fact]
    public void DockValuesFocusAndTheColorCodeSectionAreDocumentedAsImplemented()
    {
        var reference = Reference();
        var dock = Members(reference, "mud").Single(member => member.GetProperty("name").GetString() == "panel")
            .GetProperty("options").EnumerateArray().Single(option => option.GetProperty("name").GetString() == "dock");
        Assert.Equal(ScriptPanelAction.Docks.Order(), dock.GetProperty("values").EnumerateArray().Select(value => value.GetString()!).Order());
        Assert.Equal(ScriptPanelAction.DockRight, dock.GetProperty("default").GetString());

        var panel = reference.GetProperty("panel");
        var focus = panel.GetProperty("methods").EnumerateArray().Single(method => method.GetProperty("name").GetString() == "focus");
        Assert.Equal((int)ScriptPanelAction.FocusInterval.TotalSeconds, focus.GetProperty("rateLimitSeconds").GetInt32());
        var show = panel.GetProperty("methods").EnumerateArray().Single(method => method.GetProperty("name").GetString() == "show");
        Assert.Equal("panel.show(options)", show.GetProperty("signature").GetString());
        Assert.Equal("focus", Assert.Single(show.GetProperty("options").EnumerateArray()).GetProperty("name").GetString());
        var bars = panel.GetProperty("bars");
        Assert.Equal(ScriptPanelAction.BarsWidgetKinds.Order(), bars.GetProperty("widgets").EnumerateArray().Select(kind => kind.GetString()!).Order());
        Assert.Equal(1, reference.GetProperty("limits").GetProperty("panelFocusPerSecond").GetInt32());

        // The color code section lists exactly the letters the parser knows, on the palette entries it uses.
        var text = reference.GetProperty("text");
        var letters = text.GetProperty("letters").EnumerateArray()
            .ToDictionary(entry => entry.GetProperty("code").GetString()![1], entry => entry.GetProperty("palette").GetInt32());
        Assert.Equal(16, letters.Count);
        foreach (var (letter, index) in letters)
        {
            var run = Assert.Single(Wandur.Core.Terminal.MudColorCodes.Parse("&" + letter + "x"));
            Assert.Equal(index, run.Style.ForegroundIndex);
        }
        foreach (var letter in "xrgObpcwzRGYBPCW") Assert.Contains(letter, letters.Keys);
        Assert.Equal("&", text.GetProperty("foreground").GetString());
        Assert.Equal("^", text.GetProperty("background").GetString());
        Assert.Equal(["&D", "&d"], text.GetProperty("reset").EnumerateArray().Select(value => value.GetString()!));
        Assert.Equal("A Vicious Womprat", Wandur.Core.Terminal.MudColorCodes.Strip(text.GetProperty("example").GetString()!));
        Assert.Equal(4096, reference.GetProperty("limits").GetProperty("formatCharacters").GetInt32());
    }
}
