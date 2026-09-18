using System.Text;
using Microsoft.Data.Sqlite;
using Wandur.Core.Discovery;

namespace Wandur.Core.Storage;

/// <summary>Directory metadata and illustration bytes share the client database.</summary>
public sealed class SqliteWorldCatalogCache(ClientDatabase database, string legacyPath) : IWorldCatalogCache
{
    public string? ReadSnapshot()
    {
        var bytes = Read("catalog:snapshot");
        if (bytes is not null) return Encoding.UTF8.GetString(bytes);
        if (!File.Exists(legacyPath)) return null;
        var legacy = File.ReadAllText(legacyPath);
        var snapshot = WorldDirectorySnapshot.Parse(legacy, out _); // Validate before accepting the migration.
        Import("catalog:snapshot", Encoding.UTF8.GetBytes(snapshot.ToJson()));
        return Encoding.UTF8.GetString(Read("catalog:snapshot")!);
    }
    public void WriteSnapshot(string json) => Write("catalog:snapshot", Encoding.UTF8.GetBytes(json));
    public byte[]? ReadArtwork(string key)
    {
        ValidateKey(key);
        var cached = Read("art:" + key);
        if (cached is not null) return cached;
        var path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(legacyPath))!, "directory-art", key + ".png");
        if (!File.Exists(path)) return null;
        var data = File.ReadAllBytes(path);
        Import("art:" + key, data);
        return Read("art:" + key);
    }
    public void WriteArtwork(string key, byte[] data) { ValidateKey(key); Write("art:" + key, data); }
    private static void ValidateKey(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 256 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new ArgumentException(nameof(key));
    }
    private byte[]? Read(string key) => database.Read(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM cache_entries WHERE key=$key"; command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as byte[];
    });
    private void Import(string key, byte[] data) => Save(key, data, overwrite: false);
    private void Write(string key, byte[] data) => Save(key, data, overwrite: true);
    private void Save(string key, byte[] data, bool overwrite) => database.Write((connection, transaction) =>
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = overwrite
            ? "INSERT INTO cache_entries(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value"
            : "INSERT OR IGNORE INTO cache_entries(key,value) VALUES($key,$value)";
        command.Parameters.AddWithValue("$key", key); command.Parameters.Add("$value", SqliteType.Blob).Value = data;
        command.ExecuteNonQuery(); return true;
    });
}
