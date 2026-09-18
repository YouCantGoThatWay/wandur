using System.Text.Json;
using Wandur.Models;

namespace Wandur.Discovery.Tests;

public class ContractTests
{
    internal static WorldMapping Example() => new()
    {
        WorldId = "mudverse:509", Endpoint = new("legendsofthejedi.com", 5656), SchemaFingerprint = new string('a',64),
        GeneratedAt = DateTimeOffset.Parse("2026-09-18T12:00:00Z"),
        Bindings = [new() { Source = new("MSDP", "MSDP", "/HEALTH"), Target = new("character", "resource", "health", "current"), Label = "HP" }]
    };
    [Fact]
    public void SharedContractRoundTripsAndValidatesGenericAndVehicleResources()
    {
        var mapping = Example();
        mapping = mapping with { Bindings = [..mapping.Bindings, new() { Source = new("GMCP", "Ship.Status", "/shield"),
            Target = new("vehicle", "resource", "shield", "current"), Label = "Shield" }] };
        var restored = JsonSerializer.Deserialize<WorldMapping>(JsonSerializer.Serialize(mapping, ModelJson.Options), ModelJson.Options);
        Assert.True(MappingValidation.IsValid(restored));
        Assert.Equal("vehicle", restored!.Bindings[1].Target.Entity);
        Assert.True(restored.Endpoint.Matches(new(" LEGENDSOFTHEJEDI.COM. ",5656)));
        Assert.False(restored.Endpoint.Matches(new("legendsofthejedi.com",5656,true)));
    }
    [Fact]
    public void UnknownTargetsDuplicateDestinationsSecretsAndInventedSourcesAreRejected()
    {
        var m = Example(); var b = m.Bindings[0];
        Assert.False(MappingValidation.IsValid(m with { Bindings = [b,b] }));
        Assert.False(MappingValidation.IsValid(m with { Bindings = [b with { Target = b.Target with { Member = "execute" } }] }));
        Assert.False(MappingValidation.IsValid(m with { Bindings = [b with { Source = new("GMCP","Char.Login.Result","/token") }] }));
        Assert.False(MappingValidation.IsValid(m with { Bindings = [b with { Scale = double.NaN }] }));
        Assert.False(MappingValidation.IsValid(m, new ProtocolEvidence()));
        Assert.True(MappingValidation.IsValid(m, new ProtocolEvidence { Fields = [new(b.Source,["string"],true)] }));
    }
    [Fact]
    public void MissingOrZeroMaximumDoesNotInventAPercentage()
    {
        var current = new Observation<double>(40,DateTimeOffset.UtcNow);
        Assert.Null(new ResourceState("HP",current,null).Percentage);
        Assert.Null(new ResourceState("HP",current,current with { Value = 0 }).Percentage);
        Assert.Equal(20,new ResourceState("HP",current,current with { Value = 200 }).Percentage);
    }
}
