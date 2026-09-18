using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wandur.Models;

/// <summary>Shared wire contracts. No transport, UI, storage or provider dependencies.</summary>
public static class ModelJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 32,
        WriteIndented = true
    };
}

public sealed record WorldEndpoint(string Host, int Port, bool UseTls = false)
{
    [JsonIgnore] public string NormalizedHost => Host.Trim().TrimEnd('.').ToLowerInvariant();
    public bool Matches(WorldEndpoint other) => NormalizedHost == other.NormalizedHost && Port == other.Port && UseTls == other.UseTls;
}

public sealed record ProtocolFieldReference(string Protocol, string Package, string Path);
public sealed record ObservedField(ProtocolFieldReference Source, string[] Types, bool Advertised = false);
public sealed record ProtocolEvidence
{
    public int SchemaVersion { get; init; } = 1;
    public ObservedField[] Fields { get; init; } = [];
    public string[] AdvertisedProtocols { get; init; } = [];
    public bool Limited { get; init; }
    public string? ServerId { get; init; }
}

/// <summary>Examples: character/resource/health/current, vehicle/resource/hull/maximum.
/// Keys are extensible game vocabulary; entity, category and member are constrained.</summary>
public sealed record MappingTarget(string Entity, string Category, string Key, string Member);
public sealed record FieldBinding
{
    public required ProtocolFieldReference Source { get; init; }
    public required MappingTarget Target { get; init; }
    public string Label { get; init; } = "";
    public string Conversion { get; init; } = "number";
    public double Scale { get; init; } = 1;
}

public sealed record WorldMapping
{
    public int SchemaVersion { get; init; } = 1;
    public required string WorldId { get; init; }
    public required WorldEndpoint Endpoint { get; init; }
    public required string SchemaFingerprint { get; init; }
    public int Revision { get; init; } = 1;
    public DateTimeOffset GeneratedAt { get; init; }
    public string Provenance { get; init; } = "deterministic";
    // Structural validation does not establish in-game semantics or complete coverage.
    public bool Provisional { get; init; } = true;
    public FieldBinding[] Bindings { get; init; } = [];
}

public sealed record MappingCatalog
{
    public int SchemaVersion { get; init; } = 1;
    public WorldMapping[] Worlds { get; init; } = [];
}
