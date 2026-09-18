using System.Text.Json;
using Microsoft.Data.Sqlite;
using Wandur.Core.Discovery;
using Wandur.Core.Protocol;
using Wandur.Core.Storage;

namespace Wandur.Core.Tests;

public sealed class SqliteCatalogAndKnowledgeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wandur-sqlite-cache-" + Guid.NewGuid());
    private ClientDatabase Database => new(Path.Combine(_directory, "wandur.db"));
    private string Legacy => Path.Combine(_directory, "directory.json");
    public SqliteCatalogAndKnowledgeTests() => Directory.CreateDirectory(_directory);
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(_directory, true); }

    private static string Snapshot(params WorldListing[] worlds) => JsonSerializer.Serialize(new
    { SchemaVersion = 2, Format = "wandur.directory", FetchedAt = DateTimeOffset.UtcNow, Worlds = worlds },
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

    [Fact]
    public void DirectoryAndArtworkImportSurviveWithoutLegacyFilesAndNeverOverwriteNewData()
    {
        var world = new WorldListing { Id = "fixture", Name = "Original", Host = "example.test", Port = 4000, Description = "Description preserved", BannerUrl = "https://example.test/art.png" };
        var snapshot = Snapshot(world);
        File.WriteAllText(Legacy, snapshot);
        var images = Path.Combine(_directory, "directory-art"); Directory.CreateDirectory(images);
        byte[] artwork = [1, 2, 3, 4]; File.WriteAllBytes(Path.Combine(images, world.ArtKey + ".png"), artwork);
        var cache = new SqliteWorldCatalogCache(Database, Legacy);
        using (var catalog = new WorldCatalog(cache))
        {
            Assert.Equal("Description preserved", Assert.Single(catalog.Worlds).Description);
            Assert.NotNull(catalog.FetchedAt);
        }
        Assert.Equal(artwork, cache.ReadArtwork(world.ArtKey));
        Assert.Equal(snapshot, File.ReadAllText(Legacy));
        cache.WriteArtwork(world.ArtKey, [9,8]);
        File.Delete(Legacy); Directory.Delete(images, true);
        var reloaded = new SqliteWorldCatalogCache(Database, Legacy);
        Assert.Equal(new byte[] {9,8}, reloaded.ReadArtwork(world.ArtKey));
        using var restored = new WorldCatalog(reloaded);
        Assert.Equal("Original", Assert.Single(restored.Worlds).Name);
    }

    [Fact]
    public void CorruptLegacyCatalogDoesNotBecomeAcceptedCache()
    {
        File.WriteAllText(Legacy, "invalid-json");
        var cache = new SqliteWorldCatalogCache(Database, Legacy);
        Assert.ThrowsAny<JsonException>(() => cache.ReadSnapshot());
        Assert.Equal("invalid-json", File.ReadAllText(Legacy));
        File.WriteAllText(Legacy, Snapshot());
        Assert.NotNull(cache.ReadSnapshot());
        Assert.Throws<ArgumentException>(() => cache.ReadArtwork("../escape"));
    }

    [Fact]
    public void HistoricalCapabilitiesPreserveEvidenceWithoutInventingCurrentNegotiation()
    {
        var store = new SqliteWorldKnowledgeStore(Database);
        var firstAt = new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.Zero);
        var secondAt = firstAt.AddHours(1);
        store.Record("EXAMPLE.test", 4000, new KnowledgeObservation
        { Gmcp = new(TelnetOptionState.Enabled, firstAt), RoomFields = new Dictionary<string, DateTimeOffset> { ["id"] = firstAt, ["exits"] = firstAt } });
        var first = store.Load("example.test", 4000)!;
        store.Record("example.test", 4000, new KnowledgeObservation
        { Msdp = new(TelnetOptionState.Disabled, secondAt), RoomFields = new Dictionary<string, DateTimeOffset> { ["terrain"] = secondAt } });
        var restored = new SqliteWorldKnowledgeStore(Database).Load("example.test.", 4000)!;
        Assert.Equal(TelnetOptionState.Enabled, restored.Gmcp); Assert.Equal(TelnetOptionState.Disabled, restored.Msdp);
        Assert.Equal(first.GmcpObservedAt, restored.GmcpObservedAt);
        Assert.NotNull(restored.MsdpObservedAt);
        Assert.Equal(3, restored.RoomFields.Count); Assert.Contains("terrain", restored.RoomFields.Keys);
        Assert.Null(store.Load("example.test", 5000));
        Assert.Equal(firstAt, restored.RoomFields["id"]);
        Assert.Equal(secondAt, restored.UpdatedAt);
    }

    [Fact]
    public void DelayedOlderSessionCannotReplaceNewerProtocolOrFieldEvidence()
    {
        var store = new SqliteWorldKnowledgeStore(Database);
        var older = new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.Zero);
        var newer = older.AddHours(1);
        store.Record("host", 4000, new KnowledgeObservation
        { Gmcp = new(TelnetOptionState.Disabled, newer), RoomFields = new Dictionary<string, DateTimeOffset> { ["id"] = newer } });
        new SqliteWorldKnowledgeStore(Database).Record("host", 4000, new KnowledgeObservation
        { Gmcp = new(TelnetOptionState.Enabled, older), RoomFields = new Dictionary<string, DateTimeOffset> { ["id"] = older, ["coordinates"] = older } });
        var history = store.Load("host", 4000)!;
        Assert.Equal(TelnetOptionState.Disabled, history.Gmcp);
        Assert.Equal(newer, history.GmcpObservedAt);
        Assert.Equal(newer, history.RoomFields["id"]);
        Assert.Equal(older, history.RoomFields["coordinates"]);
        Assert.Equal(newer, history.UpdatedAt);
        store.Record("host", 4000, new KnowledgeObservation { RoomFields = new Dictionary<string, DateTimeOffset> { ["name"] = newer.AddMinutes(1) } });
        history = store.Load("host", 4000)!;
        Assert.Equal(newer, history.GmcpObservedAt);
        Assert.Equal(older, history.RoomFields["coordinates"]);
    }

    [Fact]
    public void MalformedKnowledgePayloadIsPreservedAndReportedAsAStorageError()
    {
        Database.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO world_capabilities VALUES('host:4000','{broken','2026-09-17')";
            return command.ExecuteNonQuery();
        });
        var store = new SqliteWorldKnowledgeStore(Database);
        Assert.Throws<IOException>(() => store.Load("host", 4000));
        Assert.Throws<IOException>(() => store.Record("host", 4000, new KnowledgeObservation { Gmcp = new(TelnetOptionState.Enabled, DateTimeOffset.UtcNow) }));
        var payload = Database.Read(connection =>
        {
            using var command = connection.CreateCommand(); command.CommandText = "SELECT payload FROM world_capabilities";
            return command.ExecuteScalar();
        });
        Assert.Equal("{broken", payload);
    }
}
