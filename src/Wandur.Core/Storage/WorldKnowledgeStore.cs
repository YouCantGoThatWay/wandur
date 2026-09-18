using System.Text.Json;
using Wandur.Core.Protocol;

namespace Wandur.Core.Storage;

public sealed record WorldCapabilityHistory(
    TelnetOptionState Gmcp, TelnetOptionState Msdp,
    DateTimeOffset? GmcpObservedAt, DateTimeOffset? MsdpObservedAt,
    IReadOnlyDictionary<string, DateTimeOffset> RoomFields, DateTimeOffset UpdatedAt);

public sealed record ProtocolObservation(TelnetOptionState State, DateTimeOffset ObservedAt);

/// <summary>Only evidence received in this batch, stamped when it arrived rather than when saved.</summary>
public sealed record KnowledgeObservation
{
    public ProtocolObservation? Gmcp { get; init; }
    public ProtocolObservation? Msdp { get; init; }
    public IReadOnlyDictionary<string, DateTimeOffset> RoomFields { get; init; } = new Dictionary<string, DateTimeOffset>();
}

public interface IWorldKnowledgeStore
{
    WorldCapabilityHistory? Load(string host, int port);
    void Record(string host, int port, KnowledgeObservation observation);
}

/// <summary>Historical observations are never reused as current-session negotiation state.</summary>
public sealed class SqliteWorldKnowledgeStore(ClientDatabase database) : IWorldKnowledgeStore
{
    public WorldCapabilityHistory? Load(string host, int port) => database.Read(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM world_capabilities WHERE endpoint_key=$key";
        command.Parameters.AddWithValue("$key", ClientDatabase.CanonicalEndpointKey($"{host}:{port}"));
        return command.ExecuteScalar() is string json ? ReadHistory(json) : null;
    });
    public void Record(string host, int port, KnowledgeObservation observation)
    {
        if (observation.RoomFields is null || observation.RoomFields.Keys.Any(f => !ValidField(f)) ||
            observation.Gmcp is { State: not (TelnetOptionState.Enabled or TelnetOptionState.Disabled) } ||
            observation.Msdp is { State: not (TelnetOptionState.Enabled or TelnetOptionState.Disabled) })
            throw new ArgumentException(nameof(observation));
        if (observation.Gmcp is null && observation.Msdp is null && observation.RoomFields.Count == 0) return;
        database.Write((connection, transaction) =>
        {
            var key = ClientDatabase.CanonicalEndpointKey($"{host}:{port}");
            using var read = connection.CreateCommand(); read.Transaction = transaction;
            read.CommandText = "SELECT payload FROM world_capabilities WHERE endpoint_key=$key"; read.Parameters.AddWithValue("$key", key);
            var previous = read.ExecuteScalar() is string json ? ReadHistory(json) : null;
            var knownFields = previous?.RoomFields.ToDictionary(f => f.Key, f => f.Value) ?? [];
            foreach (var field in observation.RoomFields)
                if (!knownFields.TryGetValue(field.Key, out var timestamp) || field.Value > timestamp) knownFields[field.Key] = field.Value;
            var gmcp = Newer(observation.Gmcp, previous?.Gmcp, previous?.GmcpObservedAt);
            var msdp = Newer(observation.Msdp, previous?.Msdp, previous?.MsdpObservedAt);
            var latest = knownFields.Values.Concat(new[] { gmcp?.ObservedAt, msdp?.ObservedAt, previous?.UpdatedAt }
                .Where(value => value.HasValue).Select(value => value!.Value)).Max();
            var history = new WorldCapabilityHistory(
                gmcp?.State ?? TelnetOptionState.Unknown, msdp?.State ?? TelnetOptionState.Unknown,
                gmcp?.ObservedAt, msdp?.ObservedAt, knownFields, latest);
            using var write = connection.CreateCommand(); write.Transaction = transaction;
            write.CommandText = "INSERT INTO world_capabilities(endpoint_key,payload,observed_at) VALUES($key,$payload,$at) ON CONFLICT(endpoint_key) DO UPDATE SET payload=excluded.payload,observed_at=excluded.observed_at";
            write.Parameters.AddWithValue("$key", key); write.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(history)); write.Parameters.AddWithValue("$at", latest.ToString("O"));
            write.ExecuteNonQuery(); return true;
        });
    }

    private static ProtocolObservation? Newer(ProtocolObservation? incoming, TelnetOptionState? previous, DateTimeOffset? observedAt) =>
        incoming is not null && (observedAt is null || incoming.ObservedAt > observedAt) ? incoming :
        observedAt is { } time && previous is { } state ? new(state, time) : null;

    private static bool ValidField(string field) => field is "id" or "name" or "description" or "area" or "exits" or "terrain" or "coordinates" or "symbol";

    private static WorldCapabilityHistory ReadHistory(string json)
    {
        try
        {
            var history = JsonSerializer.Deserialize<WorldCapabilityHistory>(json);
            if (history is null || history.RoomFields is null || history.RoomFields.Keys.Any(field => !ValidField(field)) ||
                !Enum.IsDefined(history.Gmcp) || !Enum.IsDefined(history.Msdp) ||
                (history.Gmcp == TelnetOptionState.Unknown) != (history.GmcpObservedAt is null) ||
                (history.Msdp == TelnetOptionState.Unknown) != (history.MsdpObservedAt is null))
                throw new IOException("Invalid stored world capability history. Existing data has been preserved.");
            return history;
        }
        catch (JsonException error) { throw new IOException("Unable to read world capability history. Existing data has been preserved.", error); }
    }
}
