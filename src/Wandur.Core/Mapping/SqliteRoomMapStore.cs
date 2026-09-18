using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Wandur.Core.Storage;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Core.Mapping;

/// <summary>Normalized durable graph rows, with lazy, transactional legacy-file migration.</summary>
public sealed class SqliteRoomMapStore(ClientDatabase database, string legacyMapDirectory) : IRoomMapStore
{
    public MapSnapshot? Load(string host, int port) => Access(host, port, null);
    public void Save(string host, int port, MapSnapshot snapshot) => Access(host, port, snapshot);

    private MapSnapshot? Access(string host, int port, MapSnapshot? incoming)
    {
        try
        {
            if (incoming is not null) MapFileFormat.Serialize(incoming);
            return database.Write((connection, transaction) =>
            {
                var originalEndpoint = host.Trim().ToLowerInvariant() + ":" + port;
                var world = database.ResolveWorld(connection, transaction, originalEndpoint);
                ImportLegacy(connection, transaction, world, originalEndpoint);
                var current = ReadMap(connection, transaction, world);
                if (incoming is null) return current;
                var merged = current is null ? incoming : MapSnapshotMerge.Combine(current, incoming);
                WriteMap(connection, transaction, world, merged);
                return merged;
            });
        }
        catch (Exception ex) when (ex is JsonException or FormatException or UnauthorizedAccessException or NotSupportedException)
        { throw new IOException(L.MapStorageFailed, ex); }
    }

    private void ImportLegacy(SqliteConnection connection, SqliteTransaction transaction, string world, string originalEndpoint)
    {
        var aliases = new List<string> { originalEndpoint };
        using (var command = Command(connection, transaction, "SELECT endpoint_key FROM endpoints WHERE world_id=$world UNION SELECT source_key FROM legacy_endpoints WHERE world_id=$world", world))
        using (var reader = command.ExecuteReader()) while (reader.Read()) aliases.Add(reader.GetString(0));
        foreach (var endpoint in aliases.Where(a => a.StartsWith('[')).ToArray()) aliases.Add(endpoint.Replace("[", "").Replace("]", ""));
        foreach (var candidate in aliases.Distinct(StringComparer.Ordinal))
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(candidate)));
            var path = Path.Combine(legacyMapDirectory, hash + ".json");
            // Missing hashes remain eligible for later lookup using a newly discovered legacy spelling.
            if (!File.Exists(path)) continue;
            var key = "map-file:" + Path.GetFullPath(path);
            using var imported = Command(connection, transaction, "SELECT 1 FROM imports WHERE key=$key");
            imported.Parameters.AddWithValue("$key", key);
            if (imported.ExecuteScalar() is not null) continue;
            if (new FileInfo(path).Length > MapFileFormat.MaximumBytes) throw new FormatException("A legacy map exceeds the import size limit.");
            var legacy = MapFileFormat.Deserialize(File.ReadAllText(path));
            var current = ReadMap(connection, transaction, world);
            WriteMap(connection, transaction, world, current is null ? legacy : MapSnapshotMerge.Combine(legacy, current));
            using var mark = Command(connection, transaction, "INSERT INTO imports(key) VALUES($key)");
            mark.Parameters.AddWithValue("$key", key); mark.ExecuteNonQuery();
        }
    }

    private static MapSnapshot? ReadMap(SqliteConnection connection, SqliteTransaction transaction, string world)
    {
        using var metadata = Command(connection, transaction, "SELECT payload FROM map_snapshots WHERE world_id=$world", world);
        if (metadata.ExecuteScalar() is not string json) return null;
        var map = JsonSerializer.Deserialize<MapSnapshot>(json) ?? throw new FormatException("Missing map metadata.");
        var aliases = new List<MapRoomAlias>();
        using (var command = Command(connection, transaction, "SELECT source_id,target_id,revision FROM map_room_aliases WHERE world_id=$world ORDER BY rowid", world))
        using (var reader = command.ExecuteReader()) while (reader.Read()) aliases.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
        var roomDeletes = new List<MapRoomDeletion>();
        using (var command = Command(connection, transaction, "SELECT room_id,revision FROM map_room_deletions WHERE world_id=$world ORDER BY rowid", world))
        using (var reader = command.ExecuteReader()) while (reader.Read()) roomDeletes.Add(new(reader.GetString(0), reader.GetInt64(1)));
        var linkDeletes = new List<MapLinkDeletion>();
        using (var command = Command(connection, transaction, "SELECT from_id,direction,revision FROM map_link_deletions WHERE world_id=$world ORDER BY rowid", world))
        using (var reader = command.ExecuteReader()) while (reader.Read()) linkDeletes.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
        map = map with
        {
            Rooms = ReadRows<MapRoom>(connection, transaction, world, "map_rooms"),
            Links = ReadRows<MapLink>(connection, transaction, world, "map_links"),
            AreaSettings = ReadRows<MapAreaSettings>(connection, transaction, world, "map_areas"),
            RoomAliases = aliases, DeletedRooms = roomDeletes, DeletedLinks = linkDeletes,
            CurrentRoomId = null, CandidateRoomIds = [], State = MapTrackingState.Unknown
        };
        MapFileFormat.Serialize(map); // Validate persisted payloads before exposing them to graph consumers.
        return map;
    }

    private static IReadOnlyList<T> ReadRows<T>(SqliteConnection connection, SqliteTransaction transaction, string world, string table)
    {
        using var command = Command(connection, transaction, "SELECT payload FROM " + table + " WHERE world_id=$world ORDER BY rowid", world);
        using var reader = command.ExecuteReader();
        var values = new List<T>();
        while (reader.Read()) values.Add(JsonSerializer.Deserialize<T>(reader.GetString(0)) ?? throw new FormatException("Invalid map row."));
        return values;
    }

    private static void WriteMap(SqliteConnection connection, SqliteTransaction transaction, string world, MapSnapshot snapshot)
    {
        snapshot = snapshot with { CurrentRoomId = null, CandidateRoomIds = [], State = MapTrackingState.Unknown };
        MapFileFormat.Serialize(snapshot);
        foreach (var table in new[] { "map_rooms", "map_links", "map_areas", "map_room_aliases", "map_room_deletions", "map_link_deletions" })
        {
            using var delete = Command(connection, transaction, "DELETE FROM " + table + " WHERE world_id=$world", world); delete.ExecuteNonQuery();
        }
        foreach (var room in snapshot.Rooms)
        {
            using var command = Command(connection, transaction, "INSERT INTO map_rooms(world_id,room_id,area,x,y,z,payload) VALUES($world,$id,$area,$x,$y,$z,$payload)", world);
            command.Parameters.AddWithValue("$id", room.Id); command.Parameters.AddWithValue("$area", (object?)room.Area ?? DBNull.Value);
            command.Parameters.AddWithValue("$x", room.X); command.Parameters.AddWithValue("$y", room.Y); command.Parameters.AddWithValue("$z", room.Z);
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(room)); command.ExecuteNonQuery();
        }
        foreach (var link in snapshot.Links)
        {
            using var command = Command(connection, transaction, "INSERT INTO map_links(world_id,from_id,direction,to_id,payload) VALUES($world,$from,$direction,$to,$payload)", world);
            command.Parameters.AddWithValue("$from", link.FromId); command.Parameters.AddWithValue("$direction", link.Direction);
            command.Parameters.AddWithValue("$to", link.ToId); command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(link)); command.ExecuteNonQuery();
        }
        foreach (var area in snapshot.AreaSettings)
        {
            using var command = Command(connection, transaction, "INSERT INTO map_areas(world_id,area,payload) VALUES($world,$area,$payload)", world);
            command.Parameters.AddWithValue("$area", area.Area); command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(area)); command.ExecuteNonQuery();
        }
        foreach (var alias in snapshot.RoomAliases)
        {
            using var command = Command(connection, transaction, "INSERT INTO map_room_aliases(world_id,source_id,target_id,revision) VALUES($world,$from,$to,$revision)", world);
            command.Parameters.AddWithValue("$from", alias.SourceId); command.Parameters.AddWithValue("$to", alias.TargetId); command.Parameters.AddWithValue("$revision", alias.Revision); command.ExecuteNonQuery();
        }
        foreach (var deletion in snapshot.DeletedRooms)
        {
            using var command = Command(connection, transaction, "INSERT INTO map_room_deletions(world_id,room_id,revision) VALUES($world,$id,$revision)", world);
            command.Parameters.AddWithValue("$id", deletion.Id); command.Parameters.AddWithValue("$revision", deletion.Revision); command.ExecuteNonQuery();
        }
        foreach (var deletion in snapshot.DeletedLinks)
        {
            using var command = Command(connection, transaction, "INSERT INTO map_link_deletions(world_id,from_id,direction,revision) VALUES($world,$from,$direction,$revision)", world);
            command.Parameters.AddWithValue("$from", deletion.FromId); command.Parameters.AddWithValue("$direction", deletion.Direction); command.Parameters.AddWithValue("$revision", deletion.Revision); command.ExecuteNonQuery();
        }
        using var metadata = Command(connection, transaction, "INSERT INTO map_snapshots(world_id,payload) VALUES($world,$payload) ON CONFLICT(world_id) DO UPDATE SET payload=excluded.payload", world);
        metadata.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(snapshot with { Rooms = [], Links = [], AreaSettings = [], RoomAliases = [], DeletedRooms = [], DeletedLinks = [] }));
        metadata.ExecuteNonQuery();
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql, string? world = null)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        if (world is not null) command.Parameters.AddWithValue("$world", world);
        return command;
    }
}
