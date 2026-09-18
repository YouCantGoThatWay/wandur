using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Wandur.Core.Storage;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Core.Scripting;

/// <summary>One library per stable world, including TLS aliases. Legacy files are read-only migration backups.</summary>
public sealed class SqliteWorldScriptLibraryStore(ClientDatabase database, string legacyScriptsDirectory) : IWorldScriptLibraryStore
{
    public IReadOnlyList<WorldScriptDefinition> Load(string worldKey) => database.Write((connection, transaction) =>
    {
        var world = database.ResolveWorld(connection, transaction, worldKey);
        EnsureImported(connection, transaction, world, worldKey);
        return (IReadOnlyList<WorldScriptDefinition>)ReadScripts(connection, transaction, world);
    });

    public void Upsert(string worldKey, WorldScriptDefinition script)
    {
        Validate(script);
        database.Write((connection, transaction) =>
        {
            var world = database.ResolveWorld(connection, transaction, worldKey);
            EnsureImported(connection, transaction, world, worldKey);
            var scripts = ReadScripts(connection, transaction, world);
            var index = scripts.FindIndex(s => s.Id == script.Id);
            if (index < 0) scripts.Add(script); else scripts[index] = script;
            ValidateLibrary(scripts);
            WriteScript(connection, transaction, world, script);
            using var revive = Command(connection, transaction, "DELETE FROM script_deletions WHERE world_id = $world AND id = $id",
                ("$world", world), ("$id", script.Id.ToString("D")));
            revive.ExecuteNonQuery();
            return true;
        });
    }

    public void Delete(string worldKey, Guid id) => database.Write((connection, transaction) =>
    {
        var world = database.ResolveWorld(connection, transaction, worldKey);
        EnsureImported(connection, transaction, world, worldKey);
        using var delete = Command(connection, transaction, "DELETE FROM scripts WHERE world_id = $world AND id = $id",
            ("$world", world), ("$id", id.ToString("D")));
        delete.ExecuteNonQuery();
        using var tombstone = Command(connection, transaction, "INSERT OR IGNORE INTO script_deletions(world_id, id) VALUES ($world, $id)",
            ("$world", world), ("$id", id.ToString("D")));
        tombstone.ExecuteNonQuery();
        return true;
    });

    private void EnsureImported(SqliteConnection connection, SqliteTransaction transaction, string world, string requestedKey)
    {
        var importKey = "script-library:" + world;
        using var marker = Command(connection, transaction, "SELECT 1 FROM imports WHERE key = $key", ("$key", importKey));
        var initialized = marker.ExecuteScalar() is not null;
        var candidates = new HashSet<string>(StringComparer.Ordinal) { requestedKey };
        using (var endpoints = Command(connection, transaction,
            "SELECT endpoint_key FROM endpoints WHERE world_id = $world UNION SELECT source_key FROM legacy_endpoints WHERE world_id = $world", ("$world", world)))
        using (var rows = endpoints.ExecuteReader())
            while (rows.Read())
            {
                var key = rows.GetString(0);
                candidates.Add(key);
                if (key != "demo") { candidates.Add(key + ":False"); candidates.Add(key + ":True"); }
            }
        var deleted = new HashSet<Guid>();
        using (var deletions = Command(connection, transaction, "SELECT id FROM script_deletions WHERE world_id = $world", ("$world", world)))
        using (var rows = deletions.ExecuteReader())
            while (rows.Read())
            {
                if (!Guid.TryParse(rows.GetString(0), out var id)) throw new IOException(L.ScriptLibraryInvalid);
                deleted.Add(id);
            }

        // Record each discovered source hash, including empty libraries. New aliases
        // can discover orphan backups later; tombstones prevent deleted IDs returning.
        var imported = new Dictionary<Guid, WorldScriptDefinition>();
        var importedKeys = new List<string>();
        try
        {
            foreach (var key in candidates.Order(StringComparer.Ordinal))
            {
                var stem = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
                var sourceMarker = "script-source:" + stem;
                using var seen = Command(connection, transaction, "SELECT 1 FROM imports WHERE key = $key", ("$key", sourceMarker));
                if (seen.ExecuteScalar() is not null) continue;
                var libraryPath = Path.Combine(legacyScriptsDirectory, stem + ".scripts.json");
                var sourcePath = Path.Combine(legacyScriptsDirectory, stem + ".js");
                IReadOnlyList<WorldScriptDefinition>? legacy = null;
                if (File.Exists(libraryPath))
                {
                    var bytes = ReadBounded(libraryPath, WorldScriptLibraryStore.MaximumBytes);
                    if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble)) bytes = bytes[3..];
                    legacy = JsonSerializer.Deserialize<List<WorldScriptDefinition>>(bytes) ?? throw new IOException(L.ScriptLibraryInvalid);
                }
                else if (File.Exists(sourcePath))
                {
                    var source = new UTF8Encoding(false, true).GetString(ReadBounded(sourcePath, WorldScriptStore.MaximumBytes));
                    if (source.StartsWith('\uFEFF')) source = source[1..];
                    legacy = [new(Guid.NewGuid(), L.ScriptDefaultName, source)];
                }
                if (legacy is null) continue;
                importedKeys.Add(sourceMarker);
                ValidateLibrary(legacy);
                foreach (var script in legacy)
                {
                    if (deleted.Contains(script.Id)) continue;
                    if (imported.TryGetValue(script.Id, out var previous) && previous != script)
                        throw new IOException(L.ScriptLibraryInvalid);
                    imported[script.Id] = script;
                }
            }
            var existing = ReadScripts(connection, transaction, world);
            var existingIds = existing.Select(s => s.Id).ToHashSet();
            var additions = imported.Values.Where(s => !existingIds.Contains(s.Id)).ToList();
            if (!initialized && importedKeys.Count == 0 && existing.Count == 0)
                additions.Add(new(Guid.NewGuid(), L.ScriptDefaultName, ScriptExamples.Starter));
            ValidateLibrary(existing.Concat(additions).ToArray());
            foreach (var script in additions) WriteScript(connection, transaction, world, script);
            foreach (var key in importedKeys.Append(importKey))
            {
                using var complete = Command(connection, transaction, "INSERT OR IGNORE INTO imports(key) VALUES ($key)", ("$key", key));
                complete.ExecuteNonQuery();
            }
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        { throw new IOException(L.ScriptLibraryInvalid, error); }
    }

    private static List<WorldScriptDefinition> ReadScripts(SqliteConnection connection, SqliteTransaction transaction, string world)
    {
        var scripts = new List<WorldScriptDefinition>();
        using var query = Command(connection, transaction,
            "SELECT id, name, source, enabled, macro_json FROM scripts WHERE world_id = $world ORDER BY rowid", ("$world", world));
        using var rows = query.ExecuteReader();
        while (rows.Read())
        {
            if (!Guid.TryParse(rows.GetString(0), out var id) || rows.GetInt64(3) is not (0 or 1)) throw new IOException(L.ScriptLibraryInvalid);
            try
            {
                var macro = rows.IsDBNull(4) ? null : JsonSerializer.Deserialize<MacroDefinition>(rows.GetString(4)) ?? throw new IOException(L.MacroInvalid);
                scripts.Add(new(id, rows.GetString(1), rows.GetString(2), rows.GetInt64(3) == 1, macro));
            }
            catch (JsonException error) { throw new IOException(L.MacroInvalid, error); }
        }
        try { ValidateLibrary(scripts); }
        catch (ArgumentException error) { throw new IOException(L.ScriptLibraryInvalid, error); }
        return scripts;
    }

    private static void WriteScript(SqliteConnection connection, SqliteTransaction transaction, string world, WorldScriptDefinition script)
    {
        using var save = Command(connection, transaction,
            "INSERT INTO scripts(world_id, id, name, source, enabled, macro_json) VALUES ($world, $id, $name, $source, $enabled, $macro) " +
            "ON CONFLICT(world_id, id) DO UPDATE SET name = excluded.name, source = excluded.source, enabled = excluded.enabled, macro_json = excluded.macro_json",
            ("$world", world), ("$id", script.Id.ToString("D")), ("$name", script.Name), ("$source", script.Source), ("$enabled", script.Enabled ? 1 : 0), ("$macro", script.Macro is null ? DBNull.Value : JsonSerializer.Serialize(script.Macro)));
        save.ExecuteNonQuery();
    }

    private static void Validate(WorldScriptDefinition script)
    {
        if (script is null || script.Id == Guid.Empty) throw new ArgumentException(L.ScriptLibraryInvalid);
        if (string.IsNullOrWhiteSpace(script.Name) || script.Name.Length > 120 || script.Name.Any(char.IsControl)) throw new ArgumentException(L.ScriptInvalidName);
        if (script.Source is null || Encoding.UTF8.GetByteCount(script.Source) > WorldScriptStore.MaximumBytes) throw new ArgumentException(L.ScriptSourceTooLarge);
        MacroCompiler.Validate(script);
    }
    private static void ValidateLibrary(IReadOnlyList<WorldScriptDefinition> scripts)
    {
        if (scripts.Count > WorldScriptLibraryStore.MaximumScripts) throw new ArgumentException(L.ScriptLibraryTooLarge);
        foreach (var script in scripts) Validate(script);
        if (scripts.Select(s => s.Id).Distinct().Count() != scripts.Count) throw new ArgumentException(L.ScriptLibraryInvalid);
        if (JsonSerializer.SerializeToUtf8Bytes(scripts).Length > WorldScriptLibraryStore.MaximumBytes) throw new ArgumentException(L.ScriptLibraryTooLarge);
    }
    private static byte[] ReadBounded(string path, int maximumBytes)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > maximumBytes) throw new IOException(L.ScriptLibraryTooLarge);
        using var content = new MemoryStream();
        var buffer = new byte[8192]; int count;
        while ((count = stream.Read(buffer)) > 0)
        {
            if (content.Length + count > maximumBytes) throw new IOException(L.ScriptLibraryTooLarge);
            content.Write(buffer, 0, count);
        }
        return content.ToArray();
    }
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql,
        params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command;
    }
}
