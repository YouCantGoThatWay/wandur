using System.Text.Json;
using Microsoft.Data.Sqlite;
using Wandur.Core.Storage;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Core.Settings;

/// <summary>Settings and ordered profiles in the shared database. A profile keeps its world on address edits.</summary>
public sealed class SqliteSettingsStore(ClientDatabase database, string legacySettingsPath) : ISettingsStore
{
    private const string ImportKey = "settings-json";
    public string FilePath => database.FilePath;

    public SettingsLoadResult Load()
    {
        try
        {
            return database.Write((connection, transaction) =>
            {
                EnsureImported(connection, transaction);
                return new SettingsLoadResult(ReadSettings(connection, transaction));
            });
        }
        catch (Exception error) when (error is JsonException or ArgumentException or IOException or UnauthorizedAccessException or FormatException)
        {
            return new(new(), L.Format(L.SettingsCouldNotBeReadDefaultsAreInUse, error.Message));
        }
    }

    public void Save(ClientSettings settings)
    {
        settings.Validate();
        try
        {
            database.Write((connection, transaction) =>
            {
                EnsureImported(connection, transaction);
                // A failed load must not silently replace corrupt database settings.
                ReadSettings(connection, transaction);
                WriteSettings(connection, transaction, settings);
                return true;
            });
        }
        catch (Exception error) when (error is JsonException or ArgumentException or FormatException)
        { throw new IOException(L.Format(L.SettingsCouldNotBeReadDefaultsAreInUse, error.Message), error); }
    }

    private void EnsureImported(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var marker = Command(connection, transaction, "SELECT 1 FROM imports WHERE key = $key", ("$key", ImportKey));
        if (marker.ExecuteScalar() is not null) return;
        using var existing = Command(connection, transaction, "SELECT 1 FROM client_settings WHERE id = 1");
        if (existing.ExecuteScalar() is null)
        {
            using var profiles = Command(connection, transaction, "SELECT COUNT(*) FROM profiles");
            if (Convert.ToInt64(profiles.ExecuteScalar()) != 0) throw new IOException(L.InvalidWorldProfile);
            var legacy = new SettingsStore(legacySettingsPath).Load();
            if (legacy.Warning is not null) throw new IOException(legacy.Warning);
            WriteSettings(connection, transaction, legacy.Settings);
        }
        using var imported = Command(connection, transaction, "INSERT INTO imports(key) VALUES ($key)", ("$key", ImportKey));
        imported.ExecuteNonQuery();
    }

    private static ClientSettings ReadSettings(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var settingsQuery = Command(connection, transaction, "SELECT payload FROM client_settings WHERE id = 1");
        var payload = settingsQuery.ExecuteScalar() as string ?? throw new IOException(L.EmptySettings);
        var settings = JsonSerializer.Deserialize<ClientSettings>(payload) ?? throw new JsonException(L.EmptySettings);
        var profiles = new List<ConnectionProfile>();
        using var profilesQuery = Command(connection, transaction, "SELECT id, payload, world_id FROM profiles ORDER BY position, id");
        using var rows = profilesQuery.ExecuteReader();
        while (rows.Read())
        {
            var profile = JsonSerializer.Deserialize<ConnectionProfile>(rows.GetString(1)) ?? throw new JsonException(L.InvalidWorldProfile);
            if (profile.Id.ToString("D") != rows.GetString(0)) throw new IOException(L.InvalidWorldProfile);
            if (rows.IsDBNull(2)) throw new IOException(L.InvalidWorldProfile);
            profile.Validate();
            using var endpoint = Command(connection, transaction, "SELECT world_id FROM endpoints WHERE endpoint_key = $endpoint",
                ("$endpoint", ClientDatabase.CanonicalEndpointKey($"{profile.Host}:{profile.Port}")));
            if (endpoint.ExecuteScalar() as string != rows.GetString(2)) throw new IOException(L.InvalidWorldProfile);
            profiles.Add(profile);
        }
        settings = settings with { Profiles = profiles };
        settings.Validate();
        return settings;
    }

    private void WriteSettings(SqliteConnection connection, SqliteTransaction transaction, ClientSettings settings)
    {
        var previousWorlds = new Dictionary<Guid, string>();
        using (var query = Command(connection, transaction, "SELECT id, world_id FROM profiles"))
        using (var rows = query.ExecuteReader())
            while (rows.Read())
            {
                if (!Guid.TryParse(rows.GetString(0), out var id)) throw new IOException(L.InvalidWorldProfile);
                previousWorlds.Add(id, rows.GetString(1));
            }

        // Resolve every endpoint before replacing any profile row. The enclosing
        // transaction rolls back aliases and settings together on a conflict.
        var worlds = new Dictionary<Guid, string>();
        // Existing associations take priority even if a newly added profile appears
        // earlier in the UI's saved order and uses the edited profile's new address.
        foreach (var profile in settings.Profiles.OrderByDescending(p => previousWorlds.ContainsKey(p.Id)))
            worlds[profile.Id] = database.ResolveWorld(connection, transaction,
                $"{profile.Host}:{profile.Port}", previousWorlds.GetValueOrDefault(profile.Id));
        using (var remove = Command(connection, transaction, "DELETE FROM profiles")) remove.ExecuteNonQuery();
        for (var index = 0; index < settings.Profiles.Count; index++)
        {
            var profile = settings.Profiles[index];
            using var insert = Command(connection, transaction,
                "INSERT INTO profiles(id, world_id, payload, position) VALUES ($id, $world, $payload, $position)",
                ("$id", profile.Id.ToString("D")), ("$world", worlds[profile.Id]),
                ("$payload", JsonSerializer.Serialize(profile)), ("$position", index));
            insert.ExecuteNonQuery();
        }
        using var save = Command(connection, transaction,
            "INSERT INTO client_settings(id, payload) VALUES (1, $payload) ON CONFLICT(id) DO UPDATE SET payload = excluded.payload",
            ("$payload", JsonSerializer.Serialize(settings with { Profiles = [] })));
        save.ExecuteNonQuery();
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql,
        params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command;
    }
}
