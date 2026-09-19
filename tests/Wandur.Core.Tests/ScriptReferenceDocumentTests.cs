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
        => Assert.Equal(["description", "events", "globals", "limits", "packScripts", "panel", "state", "title", "version"],
            Reference().EnumerateObject().Select(member => member.Name).Order());
}
