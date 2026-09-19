using System.Globalization;
using System.Net;
using Microsoft.Data.Sqlite;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Core.Storage;

/// <summary>One local SQLite database, with short-lived connections and transactional mutations.</summary>
public sealed class ClientDatabase(string path)
{
    private readonly object _initializationGate = new();
    private bool _initialized;
    public string FilePath { get; } = Path.GetFullPath(path);

    public SqliteConnection OpenConnection()
    {
        SqliteConnection? connection = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = FilePath, ForeignKeys = true, DefaultTimeout = 30, Pooling = true }.ToString());
            connection.Open();
            lock (_initializationGate)
            {
                if (!_initialized)
                {
                    Initialize(connection);
                    _initialized = true;
                }
            }
            return connection;
        }
        catch (Exception ex) when (ex is SqliteException or UnauthorizedAccessException)
        { connection?.Dispose(); throw new IOException(L.DatabaseOpenFailed, ex); }
        catch { connection?.Dispose(); throw; }
    }

    public T Read<T>(Func<SqliteConnection, T> read)
    {
        try { using var connection = OpenConnection(); return read(connection); }
        catch (SqliteException ex) { throw new IOException(L.DatabaseReadFailed, ex); }
    }

    public T Write<T>(Func<SqliteConnection, SqliteTransaction, T> write)
    {
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction(deferred: false);
            var result = write(connection, transaction);
            transaction.Commit();
            return result;
        }
        catch (SqliteException ex) { throw new IOException(L.DatabaseWriteFailed, ex); }
    }

    /// <summary>The world an endpoint already belongs to, or null. Reads only: it creates nothing.</summary>
    public static string? FindWorld(SqliteConnection connection, string endpointKey)
    {
        using var lookup = connection.CreateCommand();
        lookup.CommandText = "SELECT world_id FROM endpoints WHERE endpoint_key=$endpoint";
        lookup.Parameters.AddWithValue("$endpoint", CanonicalEndpointKey(endpointKey));
        return lookup.ExecuteScalar() as string;
    }

    public string ResolveWorld(SqliteConnection connection, SqliteTransaction transaction, string endpointKey, string? preferredWorldId = null)
    {
        var sourceKey = endpointKey.Trim().ToLowerInvariant();
        var suffix = sourceKey.LastIndexOf(':');
        if (suffix >= 0 && bool.TryParse(sourceKey[(suffix + 1)..], out _)) sourceKey = sourceKey[..suffix];
        endpointKey = CanonicalEndpointKey(endpointKey);
        using var lookup = connection.CreateCommand(); lookup.Transaction = transaction;
        lookup.CommandText = "SELECT world_id FROM endpoints WHERE endpoint_key=$endpoint";
        lookup.Parameters.AddWithValue("$endpoint", endpointKey);
        var existing = lookup.ExecuteScalar() as string;
        if (existing is not null)
        {
            if (preferredWorldId is not null && existing != preferredWorldId)
                throw new IOException(L.DatabaseWorldConflict);
            RememberLegacyEndpoint(connection, transaction, endpointKey, sourceKey, existing);
            return existing;
        }
        var id = preferredWorldId ?? Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || id.Any(char.IsControl)) throw new IOException(L.InvalidWorldProfile);
        using var insert = connection.CreateCommand(); insert.Transaction = transaction;
        insert.CommandText = "INSERT OR IGNORE INTO worlds(id) VALUES($id); INSERT INTO endpoints(endpoint_key,world_id) VALUES($endpoint,$id)";
        insert.Parameters.AddWithValue("$id", id); insert.Parameters.AddWithValue("$endpoint", endpointKey);
        insert.ExecuteNonQuery();
        RememberLegacyEndpoint(connection, transaction, endpointKey, sourceKey, id);
        return id;
    }

    private static void RememberLegacyEndpoint(SqliteConnection connection, SqliteTransaction transaction, string endpoint, string source, string world)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO legacy_endpoints(endpoint_key,source_key,world_id) VALUES($endpoint,$source,$world)";
        command.Parameters.AddWithValue("$endpoint", endpoint); command.Parameters.AddWithValue("$source", source); command.Parameters.AddWithValue("$world", world);
        command.ExecuteNonQuery();
    }

    public static string CanonicalEndpoint(string key) => CanonicalEndpointKey(key);
    public static string CanonicalEndpointKey(string key)
    {
        key = key.Trim();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 2048 || key.Any(char.IsControl)) throw new IOException(L.InvalidWorldProfile);
        var separator = key.LastIndexOf(':');
        if (separator >= 0 && bool.TryParse(key[(separator + 1)..], out _)) key = key[..separator];
        separator = key.LastIndexOf(':');
        if (separator < 0 || !int.TryParse(key[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port))
            return key.ToLowerInvariant(); // Named local/demo worlds have no network address.
        if (port is < 1 or > 65535) throw new IOException(L.InvalidWorldProfile);
        var host = key[..separator].Trim().TrimEnd('.');
        if (host.StartsWith('[') && host.EndsWith(']')) host = host[1..^1];
        if (string.IsNullOrWhiteSpace(host)) throw new IOException(L.InvalidWorldProfile);
        if (IPAddress.TryParse(host, out var address)) host = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "[" + address + "]" : address.ToString();
        else
        {
            try { host = new IdnMapping().GetAscii(host).ToLowerInvariant(); }
            catch (ArgumentException ex) { throw new IOException(L.InvalidWorldProfile, ex); }
        }
        return host + ":" + port.ToString(CultureInfo.InvariantCulture);
    }

    private static void Initialize(SqliteConnection connection)
    {
        using var version = connection.CreateCommand(); version.CommandText = "PRAGMA user_version";
        var schemaVersion = Convert.ToInt32(version.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (schemaVersion > 3) throw new IOException(L.DatabaseVersionUnsupported);
        using var journal = connection.CreateCommand(); journal.CommandText = "PRAGMA journal_mode=WAL"; journal.ExecuteScalar();
        using var transaction = connection.BeginTransaction(deferred: false);
        // A second database instance may have migrated while this connection waited for the write lock.
        version.Transaction = transaction;
        schemaVersion = Convert.ToInt32(version.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (schemaVersion > 3) throw new IOException(L.DatabaseVersionUnsupported);
        using var schema = connection.CreateCommand(); schema.Transaction = transaction;
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS worlds(id TEXT PRIMARY KEY NOT NULL);
            CREATE TABLE IF NOT EXISTS endpoints(endpoint_key TEXT PRIMARY KEY NOT NULL,world_id TEXT NOT NULL REFERENCES worlds(id));
            CREATE INDEX IF NOT EXISTS endpoints_world ON endpoints(world_id);
            CREATE TABLE IF NOT EXISTS legacy_endpoints(endpoint_key TEXT NOT NULL,source_key TEXT PRIMARY KEY NOT NULL,world_id TEXT NOT NULL REFERENCES worlds(id));
            CREATE INDEX IF NOT EXISTS legacy_endpoints_world ON legacy_endpoints(world_id);
            CREATE TABLE IF NOT EXISTS client_settings(id INTEGER PRIMARY KEY,payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS profiles(id TEXT PRIMARY KEY NOT NULL,world_id TEXT REFERENCES worlds(id),payload TEXT NOT NULL,position INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS profiles_world ON profiles(world_id);
            CREATE TABLE IF NOT EXISTS scripts(world_id TEXT NOT NULL REFERENCES worlds(id),id TEXT NOT NULL,name TEXT NOT NULL,source TEXT NOT NULL,enabled INTEGER NOT NULL,PRIMARY KEY(world_id,id));
            CREATE TABLE IF NOT EXISTS script_deletions(world_id TEXT NOT NULL REFERENCES worlds(id),id TEXT NOT NULL,PRIMARY KEY(world_id,id));
            CREATE TABLE IF NOT EXISTS imports(key TEXT PRIMARY KEY NOT NULL);
            CREATE TABLE IF NOT EXISTS cache_entries(key TEXT PRIMARY KEY NOT NULL,value BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS world_capabilities(endpoint_key TEXT PRIMARY KEY NOT NULL,payload TEXT NOT NULL,observed_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS map_snapshots(world_id TEXT PRIMARY KEY NOT NULL REFERENCES worlds(id),payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS world_agent_profiles(world_id TEXT PRIMARY KEY NOT NULL REFERENCES worlds(id),payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS map_rooms(world_id TEXT NOT NULL REFERENCES worlds(id),room_id TEXT NOT NULL,area TEXT,x REAL NOT NULL,y REAL NOT NULL,z REAL NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(world_id,room_id));
            CREATE INDEX IF NOT EXISTS map_rooms_coordinates ON map_rooms(world_id,area,z,x,y);
            CREATE TABLE IF NOT EXISTS map_links(world_id TEXT NOT NULL REFERENCES worlds(id),from_id TEXT NOT NULL,direction TEXT NOT NULL,to_id TEXT NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(world_id,from_id,direction));
            CREATE INDEX IF NOT EXISTS map_links_destination ON map_links(world_id,to_id);
            CREATE TABLE IF NOT EXISTS map_areas(world_id TEXT NOT NULL REFERENCES worlds(id),area TEXT NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(world_id,area));
            CREATE TABLE IF NOT EXISTS map_room_aliases(world_id TEXT NOT NULL REFERENCES worlds(id),source_id TEXT NOT NULL,target_id TEXT NOT NULL,revision INTEGER NOT NULL,PRIMARY KEY(world_id,source_id));
            CREATE TABLE IF NOT EXISTS map_room_deletions(world_id TEXT NOT NULL REFERENCES worlds(id),room_id TEXT NOT NULL,revision INTEGER NOT NULL,PRIMARY KEY(world_id,room_id));
            CREATE TABLE IF NOT EXISTS map_link_deletions(world_id TEXT NOT NULL REFERENCES worlds(id),from_id TEXT NOT NULL,direction TEXT NOT NULL,revision INTEGER NOT NULL,PRIMARY KEY(world_id,from_id,direction));
            
            """;
        schema.ExecuteNonQuery();
        if (schemaVersion < 2)
        {
            schema.CommandText = "ALTER TABLE scripts ADD COLUMN macro_json TEXT; PRAGMA user_version=2;";
            schema.ExecuteNonQuery();
        }
        schema.CommandText = "PRAGMA user_version=3";
        schema.ExecuteNonQuery();
        transaction.Commit();
    }
}
