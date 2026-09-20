using System.Globalization;
using Microsoft.Data.Sqlite;
using Wandur.Core.Settings;

namespace Wandur.Core.Storage;

/// <summary>How often a saved world has been connected to, with every connection's time kept so the score can weigh recency.</summary>
public sealed record WorldUsage(IReadOnlyList<DateTimeOffset> ConnectedAt)
{
    // The recency weights, in one place: a connection counts fully for 30 days, half for the next 60,
    // and a quarter after that, so a world played every week outranks one abandoned last spring.
    public const int RecentDays = 30;
    public const int FadingDays = 90;
    public const double RecentWeight = 1.0;
    public const double FadingWeight = 0.5;
    public const double OldWeight = 0.25;

    public int Connections => ConnectedAt.Count;
    public DateTimeOffset LastConnectedAt => ConnectedAt.Count == 0 ? DateTimeOffset.MinValue : ConnectedAt.Max();

    public double Score(DateTimeOffset now)
    {
        var score = 0.0;
        foreach (var at in ConnectedAt)
        {
            var age = now - at;
            score += age.TotalDays <= RecentDays ? RecentWeight : age.TotalDays <= FadingDays ? FadingWeight : OldWeight;
        }
        return score;
    }

    /// <summary>
    /// The default order of the saved worlds: connected worlds by score, then by last connection, then by name;
    /// worlds never connected follow in the order they were given, which is the manual order kept in <c>position</c>.
    /// </summary>
    public static IReadOnlyList<ConnectionProfile> Order(IReadOnlyList<ConnectionProfile> profiles, IReadOnlyDictionary<Guid, WorldUsage> usage, DateTimeOffset now)
    {
        if (usage.Count == 0) return profiles;
        var used = new List<(ConnectionProfile Profile, double Score, DateTimeOffset Last, int Index)>();
        var unused = new List<ConnectionProfile>();
        for (var index = 0; index < profiles.Count; index++)
        {
            var profile = profiles[index];
            if (usage.TryGetValue(profile.Id, out var entry) && entry.Connections > 0) used.Add((profile, entry.Score(now), entry.LastConnectedAt, index));
            else unused.Add(profile);
        }
        if (used.Count == 0) return profiles;
        used.Sort((a, b) =>
        {
            var byScore = b.Score.CompareTo(a.Score);
            if (byScore != 0) return byScore;
            var byLast = b.Last.CompareTo(a.Last);
            if (byLast != 0) return byLast;
            var byName = string.Compare(a.Profile.Name, b.Profile.Name, StringComparison.CurrentCultureIgnoreCase);
            return byName != 0 ? byName : a.Index.CompareTo(b.Index);
        });
        return [.. used.Select(entry => entry.Profile), .. unused];
    }
}

public interface IWorldUsageStore
{
    /// <summary>Counts one successful connection to the world at this address.</summary>
    void RecordConnection(string host, int port);
    /// <summary>The usage of every saved world that has one, keyed by profile id.</summary>
    IReadOnlyDictionary<Guid, WorldUsage> Load();
}

/// <summary>
/// Usage lives beside the profiles in the shared database, keyed by the world (which survives an address edit
/// and a catalog merge), with a summary row per world and an append-only log of connection times.
/// </summary>
public sealed class SqliteWorldUsageStore(ClientDatabase database, TimeProvider? time = null) : IWorldUsageStore
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public void RecordConnection(string host, int port)
    {
        var now = _time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
        database.Write((connection, transaction) =>
        {
            var world = database.ResolveWorld(connection, transaction, $"{host}:{port}");
            using var summary = connection.CreateCommand(); summary.Transaction = transaction;
            summary.CommandText = "INSERT INTO world_usage(world_id,connections,last_connected_at) VALUES($world,1,$now) " +
                "ON CONFLICT(world_id) DO UPDATE SET connections=connections+1,last_connected_at=excluded.last_connected_at";
            summary.Parameters.AddWithValue("$world", world); summary.Parameters.AddWithValue("$now", now);
            summary.ExecuteNonQuery();
            using var log = connection.CreateCommand(); log.Transaction = transaction;
            log.CommandText = "INSERT INTO world_connections(world_id,connected_at) VALUES($world,$now)";
            log.Parameters.AddWithValue("$world", world); log.Parameters.AddWithValue("$now", now);
            log.ExecuteNonQuery();
            return true;
        });
    }

    public IReadOnlyDictionary<Guid, WorldUsage> Load() => database.Read(connection =>
    {
        var times = new Dictionary<Guid, List<DateTimeOffset>>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT p.id, c.connected_at FROM profiles p JOIN world_connections c ON c.world_id = p.world_id";
        using var rows = command.ExecuteReader();
        while (rows.Read())
        {
            if (!Guid.TryParse(rows.GetString(0), out var id) ||
                !DateTimeOffset.TryParseExact(rows.GetString(1), "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)) continue;
            if (!times.TryGetValue(id, out var list)) times[id] = list = [];
            list.Add(at);
        }
        return (IReadOnlyDictionary<Guid, WorldUsage>)times.ToDictionary(pair => pair.Key, pair => new WorldUsage(pair.Value));
    });
}
