using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wandur.Core.Settings;
using Wandur.Core.Discovery;
using Wandur.Core.Protocol;
using Wandur.Models;

namespace Wandur.Core.Tests;

public sealed class ProtocolBindingTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-18T12:00:00Z");
    private static FieldBinding Binding(string path, string category = "resource", string key = "health", string member = "current", string entity = "character", string conversion = "number", string protocol = "GMCP", string package = "Char.Vitals", double scale = 1) => new()
    { Source = new(protocol, package, path), Target = new(entity, category, key, member), Conversion = conversion, Scale = scale, Label = key };
    private static WorldMapping Mapping(params FieldBinding[] bindings) => new()
    { WorldId = "test", Endpoint = new("mud.example", 4000), SchemaFingerprint = new('a', 64), GeneratedAt = Now, Bindings = bindings };
    private static void Observe(ProtocolBindingEngine engine, string text, DateTimeOffset? now = null, byte option = 201) =>
        engine.Observe(option, ProtocolDiagnosticFormatter.Format(option, Encoding.UTF8.GetBytes(text)), now ?? Now);

    [Fact]
    public void MappingRefreshKeepsOnlyCompatibleLifetimeStateAndDoesNotReplayPackets()
    {
        var hp = Binding("/hp");
        var mapping = Mapping(hp);
        var engine = new ProtocolBindingEngine(mapping);
        Observe(engine, "Char.Vitals {\"hp\":75,\"max\":100}");
        engine.UpdateMapping(Mapping(hp, Binding("/max", member: "maximum")));
        Assert.Equal(Now, engine.State.Character.Resources["health"].Current!.ReceivedAt);
        Assert.Null(engine.State.Character.Resources["health"].Maximum);
        Observe(engine, "Char.Vitals {\"max\":100}");
        Assert.Equal(75, engine.State.Character.Resources["health"].Percentage);
        // A changed source or newly introduced identity cannot inherit observations.
        engine.UpdateMapping(Mapping(Binding("/different")));
        Assert.Empty(engine.State.Character.Resources);
        Observe(engine, "Char.Vitals {\"different\":15}");
        engine.UpdateMapping(Mapping(Binding("/different"), Binding("/name", "identity", "name", "value", conversion: "text")));
        Assert.Empty(engine.State.Character.Resources);
        Observe(engine, "Char.Vitals {\"different\":20,\"name\":\"first\"}");
        engine.Reset();
        engine.UpdateMapping(mapping);
        Assert.Empty(engine.State.Character.Identity);
        Assert.Empty(engine.State.Character.Resources);
    }

    [Fact]
    public void IncrementalResourcesRetainOriginalTimestampAndMissingMaximum()
    {
        var engine = new ProtocolBindingEngine(Mapping(Binding("/hp"), Binding("/max", member: "maximum")));
        Observe(engine, "Char.Vitals {\"hp\":75}");
        Assert.Null(engine.State.Character.Resources["health"].Maximum);
        Observe(engine, "Char.Vitals {\"max\":100}", Now.AddMinutes(1));
        var resource = engine.State.Character.Resources["health"];
        Assert.Equal(75, resource.Percentage);
        Assert.Equal(Now, resource.Current!.ReceivedAt);
        Assert.True(resource.Current.IsStale(Now.AddMinutes(2), TimeSpan.FromSeconds(90)));
        Assert.False(resource.Maximum!.IsStale(Now.AddMinutes(2), TimeSpan.FromSeconds(90)));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"garbage\"")]
    [InlineData("\"NaN\"")]
    [InlineData("1e999")]
    [InlineData("true")]
    [InlineData("{}")]
    public void InvalidNumericUpdatesClearPreviousObservation(string value)
    {
        var engine = new ProtocolBindingEngine(Mapping(Binding("/hp")));
        Observe(engine, "Char.Vitals {\"hp\":75}");
        Observe(engine, "Char.Vitals {\"hp\":" + value + "}", Now.AddMinutes(1));
        Assert.Null(engine.State.Character.Resources["health"].Current);
    }

    /// <summary>A new character name is a new lifetime: everything observed before it goes, whatever the packet's
    /// property order. Another entity's name change keeps what was observed, because a world reports a variable only
    /// when it changes, so a maximum it does not repeat for the next opponent is the same maximum.</summary>
    [Theory]
    [InlineData("character")]
    [InlineData("opponent")]
    [InlineData("vehicle")]
    public void IdentityChangeResetsTheCharacterAndKeepsAnotherEntitysUnrepeatedObservations(string entity)
    {
        var engine = new ProtocolBindingEngine(Mapping(Binding("/hp", entity: entity), Binding("/max", member: "maximum", entity: entity),
            Binding("/name", "identity", "name", "value", entity, "text"), Binding("/room", "location", "name", "value", "world", "text")));
        Observe(engine, "Char.Vitals {\"name\":\"first\",\"hp\":75,\"max\":100,\"room\":\"old\"}");
        Observe(engine, "Char.Vitals {\"hp\":25,\"name\":\"second\"}");
        var state = entity == "character" ? engine.State.Character : entity == "opponent" ? engine.State.Opponent : engine.State.Vehicle;
        Assert.Equal(25, state.Resources["health"].Current!.Value);
        if (entity == "character") { Assert.Null(state.Resources["health"].Maximum); Assert.Empty(engine.State.World.Location); }
        else { Assert.Equal(100, state.Resources["health"].Maximum!.Value); Assert.Equal("old", engine.State.World.Location["name"].Value); }
        Assert.Equal("second", state.Identity["name"].Value);
        engine.Reset(); Assert.Empty(engine.State.Character.Identity); Assert.Empty(engine.State.Opponent.Identity); Assert.Empty(engine.State.Vehicle.Identity);
    }

    /// <summary>The reproduction of the second-fight report, one MSDP variable per packet: the first fight ends with the
    /// name cleared, the second opponent's health arrives before its name, and its maximum is never sent again because it
    /// did not change. The opponent is presented empty while its name is cleared and comes back whole with the next name.</summary>
    [Fact]
    public void AClearedOpponentNameHidesTheOpponentUntilTheNextNameAndKeepsWhatTheWorldDoesNotRepeat()
    {
        var engine = new ProtocolBindingEngine(Mapping(Binding("/OPPONENTHEALTH", entity: "opponent", protocol: "MSDP", package: "MSDP"),
            Binding("/OPPONENTHEALTHMAX", member: "maximum", entity: "opponent", protocol: "MSDP", package: "MSDP"),
            Binding("/OPPONENTNAME", "identity", "name", "value", "opponent", "text", "MSDP", "MSDP")));
        void Msdp(string variable, string value) => Observe(engine, "\u0001" + variable + "\u0002" + value, option: 69);
        Msdp("OPPONENTNAME", "A Vicious Womprat"); Msdp("OPPONENTHEALTHMAX", "100"); Msdp("OPPONENTHEALTH", "100"); Msdp("OPPONENTHEALTH", "30");
        Assert.Equal(30, engine.State.Opponent.Resources["health"].Percentage);
        // The fight ends: only the name is cleared. The opponent is gone from the state, the observations are not.
        Msdp("OPPONENTNAME", "");
        Assert.Empty(engine.State.Opponent.Resources); Assert.Empty(engine.State.Opponent.Identity);
        // A same-named opponent: the world repeats neither the name's letters nor the maximum, only the health.
        Msdp("OPPONENTHEALTH", "100"); Msdp("OPPONENTNAME", "A Vicious Womprat");
        Assert.Equal(100, engine.State.Opponent.Resources["health"].Percentage);
        Assert.Equal("A Vicious Womprat", engine.State.Opponent.Identity["name"].Value);
        // A different opponent with the same maximum, its health before its name and nothing cleared in between.
        Msdp("OPPONENTHEALTH", "80"); Msdp("OPPONENTNAME", "A Stormtrooper");
        Assert.Equal(80, engine.State.Opponent.Resources["health"].Percentage);
        Assert.Equal("A Stormtrooper", engine.State.Opponent.Identity["name"].Value);
        // The three cleared in the alphabetical order most tables use, then a new fight with its maximum first.
        Msdp("OPPONENTHEALTH", "0"); Msdp("OPPONENTHEALTHMAX", "0"); Msdp("OPPONENTNAME", "");
        Assert.Empty(engine.State.Opponent.Resources);
        Msdp("OPPONENTHEALTHMAX", "250"); Msdp("OPPONENTHEALTH", "250");
        Assert.Empty(engine.State.Opponent.Resources);
        Msdp("OPPONENTNAME", "A Wookiee");
        Assert.Equal(100, engine.State.Opponent.Resources["health"].Percentage);
        Assert.Equal(250, engine.State.Opponent.Resources["health"].Maximum!.Value);
        // A mapping refresh with the same bindings keeps all of it; a reset clears the retained observations too.
        Assert.False(engine.UpdateMapping(Mapping(Binding("/OPPONENTNAME", "identity", "name", "value", "opponent", "text", "MSDP", "MSDP"),
            Binding("/OPPONENTHEALTHMAX", member: "maximum", entity: "opponent", protocol: "MSDP", package: "MSDP"),
            Binding("/OPPONENTHEALTH", entity: "opponent", protocol: "MSDP", package: "MSDP"))));
        Assert.Equal(250, engine.State.Opponent.Resources["health"].Maximum!.Value);
        engine.Reset();
        Msdp("OPPONENTNAME", "A Wookiee");
        Assert.Empty(engine.State.Opponent.Resources);
    }

    [Fact]
    public void NativeAndGmcpMsdpNeverMergeImplicitlyAndPointersAreExact()
    {
        var engine = new ProtocolBindingEngine(Mapping(Binding("/HEALTH", protocol: "MSDP", package: "MSDP"),
            Binding("/h~1p/~0value/0", key: "mana", package: "MSDP", scale: 2)));
        Observe(engine, "MSDP {\"HEALTH\":999,\"h/p\":{\"~value\":[\"12.5\"]}}");
        Assert.False(engine.State.Character.Resources.ContainsKey("health"));
        Assert.Equal(25, engine.State.Character.Resources["mana"].Current!.Value);
        Observe(engine, "\u0001HEALTH\u000242", option: 69);
        Assert.Equal(42, engine.State.Character.Resources["health"].Current!.Value);
        Observe(engine, "msdp {\"h/p\":{\"~value\":[100]}}");
        Assert.Equal(25, engine.State.Character.Resources["mana"].Current!.Value);
    }

    [Fact]
    public void PrivateMalformedAndTruncatedPacketsCannotUpdateState()
    {
        var engine = new ProtocolBindingEngine(Mapping(Binding("/hp")));
        foreach (var content in new[] { new ProtocolDiagnosticContent("Char.Vitals", "{\"hp\":1}", true, false),
            new ProtocolDiagnosticContent("Char.Vitals", "{\"hp\":1}", false, true),
            new ProtocolDiagnosticContent("Char.Vitals", "{\"hp\":1}", false, false) { Redacted = true },
            ProtocolDiagnosticFormatter.Format(201, Encoding.UTF8.GetBytes("Char.Vitals {\"hp\":\"hidden\"}"), secrets: ["hidden"]) })
            engine.Observe(201, content, Now);
        Assert.Empty(engine.State.Character.Resources);
    }

    [Fact]
    public void WrongShapedAncestorInvalidatesNestedValueWithoutUsingAncestorAsNumber()
    {
        var engine = new ProtocolBindingEngine(Mapping(Binding("/vitals/hp")));
        Observe(engine, "Char.Vitals {\"vitals\":{\"hp\":75}}");
        Observe(engine, "Char.Vitals {\"vitals\":4}");
        Assert.Null(engine.State.Character.Resources["health"].Current);
    }

    [Fact]
    public void CapabilitiesRemainOptionalAndZeroAndFalseAreRealObservations()
    {
        var engine = new ProtocolBindingEngine(Mapping(
            Binding("/xp", "progression", "experience"), Binding("/next", "progression", "experience", "maximum"),
            Binding("/str", "attribute", "strength"), Binding("/base", "attribute", "strength", "base"),
            Binding("/gold", "currency", "gold", "carried"), Binding("/bank", "currency", "gold", "bank"),
            Binding("/total", "currency", "gold", "total"), Binding("/level", "metric", "level", "value"),
            Binding("/fighting", "metric", "fighting", "value", conversion: "boolean"),
            Binding("/position", "metric", "position", "value", conversion: "text")));
        Observe(engine, "Char.Vitals {\"xp\":0,\"next\":100,\"str\":12,\"base\":10,\"gold\":0,\"bank\":15,\"total\":15,\"level\":2,\"fighting\":false,\"position\":\"standing\"}");
        var state = engine.State.Character;
        Assert.Equal(0, state.Progression["experience"].Percentage);
        Assert.Equal(12, state.Attributes["strength"].Current!.Value);
        Assert.Equal(10, state.Attributes["strength"].Base!.Value);
        Assert.Equal(0, state.Currencies["gold"].Carried!.Value);
        Assert.Equal(15, state.Currencies["gold"].Bank!.Value);
        Assert.Equal(15, state.Currencies["gold"].Total!.Value);
        Assert.Equal(2, state.Metrics["level"].Number!.Value);
        Assert.False(state.Metrics["fighting"].Boolean!.Value);
        Assert.Equal("standing", state.Metrics["position"].Text!.Value);
        Assert.Empty(state.Resources); Assert.Empty(engine.State.Vehicle.Resources);
        Observe(engine, "Char.Vitals {\"fighting\":\"maybe\",\"position\":\"bad\\nline\"}");
        Assert.Null(state.Metrics["fighting"].Boolean); Assert.Null(state.Metrics["position"].Text);
    }

    [Fact]
    public void ExcessiveValuesAreUnknownAndInvalidMapsAreRejected()
    {
        var engine = new ProtocolBindingEngine(Mapping(Binding("/hp", scale: 1_000_000),
            Binding("/status", "metric", "status", "value", conversion: "text")));
        Observe(engine, "Char.Vitals {\"hp\":1e12,\"status\":\"" + new string('x', 513) + "\"}");
        Assert.Null(engine.State.Character.Resources["health"].Current);
        Assert.Null(engine.State.Character.Metrics["status"].Text);
        foreach (var scale in new[] { 0, -1, double.NaN, double.PositiveInfinity, 1_000_001d })
            Assert.Throws<ArgumentException>(() => new ProtocolBindingEngine(Mapping(Binding("/hp", scale: scale))));
        Assert.Throws<ArgumentException>(() => new ProtocolBindingEngine(Mapping(Binding("/hp"), Binding("/other"))));
    }

    [Fact]
    public void OptionalMalformedMappingDoesNotDiscardListing()
    {
        foreach (var invalid in new[] { "42", "[]", "{\"bindings\":null}", "{\"schema_version\":\"oops\"}" })
        {
            var listing = JsonSerializer.Deserialize<WorldListing>("{\"id\":\"test\",\"name\":\"Test\",\"protocol_mapping\":" + invalid + "}", ModelJson.Options)!;
            Assert.Equal("Test", listing.Name); Assert.Null(listing.ProtocolMapping);
        }
    }

    [Theory]
    [InlineData("endpoint", "null")]
    [InlineData("endpoint/host", "null")]
    [InlineData("endpoint/port", "\"4000\"")]
    [InlineData("world_id", "null")]
    [InlineData("schema_fingerprint", "null")]
    [InlineData("schema_version", "[]")]
    [InlineData("bindings", "null")]
    [InlineData("bindings/0", "null")]
    [InlineData("bindings/0/source", "null")]
    [InlineData("bindings/0/source/path", "null")]
    [InlineData("bindings/0/source/package", "{}")]
    [InlineData("bindings/0/target", "null")]
    [InlineData("bindings/0/target/key", "null")]
    [InlineData("bindings/0/scale", "\"NaN\"")]
    [InlineData("bindings/0/scale", "0")]
    public void InvalidOptionalMappingIsIgnoredByWholeCatalogAndSavedProfile(string path, string replacement)
    {
        var mapping = JsonSerializer.SerializeToNode(Mapping(Binding("/hp")), ModelJson.Options)!;
        var parts = path.Split('/');
        var parent = mapping;
        foreach (var part in parts[..^1]) parent = parent is JsonArray array ? array[int.Parse(part)]! : parent[part]!;
        if (parent is JsonArray items) items[int.Parse(parts[^1])] = JsonNode.Parse(replacement);
        else parent[parts[^1]] = JsonNode.Parse(replacement);
        var listing = "{\"id\":\"test\",\"name\":\"Test\",\"host\":\"mud.example\",\"port\":4000,\"protocol_mapping\":" + mapping.ToJsonString() + "}";
        using var catalog = new WorldCatalog(new SnapshotCache("{\"schema_version\":2,\"format\":\"wandur.directory\",\"fetched_at\":\"2026-09-18T12:00:00Z\",\"worlds\":[" + listing + "]}"));
        Assert.Null(catalog.Warning);
        Assert.Null(Assert.Single(catalog.Worlds).ProtocolMapping);
        var profile = JsonSerializer.Deserialize<ConnectionProfile>("{\"Name\":\"Test\",\"Host\":\"mud.example\",\"ProtocolMapping\":" + mapping.ToJsonString() + "}")!;
        Assert.Equal("Test", profile.Name); Assert.Null(profile.ProtocolMapping);
    }

    [Fact]
    public void InvalidAndDifferentProfileEndpointsCannotActivateMapping()
    {
        var mapping = Mapping(Binding("/hp"));
        var profile = new ConnectionProfile { Host = "MUD.EXAMPLE.", Port = 4000, ProtocolMapping = mapping };
        Assert.NotNull(profile.GetProtocolMapping());
        foreach (var changed in new[] { profile with { Host = null! }, profile with { Host = "" }, profile with { Host = "other.example" },
            profile with { Port = 4001 }, profile with { UseTls = true } }) Assert.Null(changed.GetProtocolMapping());
        var listing = new WorldListing { Id = "test", Name = "Test", ProtocolMapping = mapping };
        Assert.Null(listing.MappingForEndpoint(null!, 4000, false));
        // The listing's id is the directory's opaque label and may change under a mapping; the endpoint decides.
        Assert.Same(mapping, (listing with { Id = "another" }).MappingForEndpoint("mud.example", 4000, false));
        Assert.Null((listing with { Id = "another" }).MappingForEndpoint("mud.example", 4001, false));
    }

    private sealed class SnapshotCache(string json) : IWorldCatalogCache
    {
        public string ReadSnapshot() => json;
        public void WriteSnapshot(string value) { }
        public byte[]? ReadArtwork(string key) => null;
        public void WriteArtwork(string key, byte[] data) { }
    }

    [Fact]
    public void MappingRoundtripsAndOnlyTravelsToItsExactEndpoint()
    {
        var mapping = Mapping(Binding("/hp"));
        var listing = new WorldListing { Id = "test", Name = "Test", Host = "MUD.EXAMPLE.", Port = 4000, TlsPort = 4001, ProtocolMapping = mapping };
        var copy = JsonSerializer.Deserialize<WorldListing>(JsonSerializer.Serialize(listing, ModelJson.Options), ModelJson.Options)!;
        Assert.NotNull(copy.ToProfile().ProtocolMapping);
        Assert.Null(copy.ToProfile(tls: true).ProtocolMapping);
        Assert.Null((copy with { Host = "other.example" }).ToProfile().ProtocolMapping);
    }
}
