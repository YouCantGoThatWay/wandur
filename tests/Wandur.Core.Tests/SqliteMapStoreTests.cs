using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Wandur.Core.Mapping;
using Wandur.Core.Storage;

namespace Wandur.Core.Tests;

public sealed class SqliteMapStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wandur-sqlite-map-" + Guid.NewGuid());
    private ClientDatabase Database => new(Path.Combine(_directory, "wandur.db"));
    private string Legacy => Path.Combine(_directory, "maps");
    private static MapRoom Room(string id) => new(id, id, "Description", "Keep", 1, 2, 3, false, id)
    { Environment = "forest", Symbol = "♣", Color = "#123456", Notes = "home", Weight = 3, IsManuallyEdited = true, Revision = 42, KnownExits = ["north"], ObservedName = "Observed" };
    private static MapSnapshot Map() => new([Room("a"), Room("b")], [new("a", "b", "north", true)
    { Command = "enter portal", Weight = 4, DoorState = MapDoorState.Closed, IsLocked = true, IsManuallyEdited = true, LinePoints = [new(2, 4)], Revision = 43 }], [], "a", MapTrackingState.Confirmed, RoomDataSource.Gmcp, 17)
    { AreaSettings = [new("Keep", true) { Revision = 44 }], RoomAliases = [new("old", "a", 45)], DeletedRooms = [new("gone", 46)], DeletedLinks = [new("a", "south", 47)] };

    [Fact]
    public void DatabaseProvidesForeignKeysTransactionsAndStableEndpointAliases()
    {
        var db = Database;
        var first = db.Write((c, t) => db.ResolveWorld(c, t, " MUD.Example:04000:True "));
        Assert.Equal(first, db.Write((c, t) => db.ResolveWorld(c, t, "mud.example:4000:False")));
        Assert.Equal(first, db.Write((c, t) => db.ResolveWorld(c, t, "new.example:5000", first)));
        var other = db.Write((c, t) => db.ResolveWorld(c, t, "other.example:4000"));
        Assert.NotEqual(first, other);
        Assert.Throws<IOException>(() => db.Write((c, t) => db.ResolveWorld(c, t, "other.example:4000", first)));
        Assert.Equal("[::1]:4000", ClientDatabase.CanonicalEndpointKey("[0:0:0:0:0:0:0:1]:4000:False"));
        Assert.Equal(1L, db.Read(c => { using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA foreign_keys"; return (long)cmd.ExecuteScalar()!; }));
        Assert.Throws<IOException>(() => db.Write<int>((c, t) => { using var cmd = c.CreateCommand(); cmd.Transaction = t; cmd.CommandText = "INSERT INTO endpoints VALUES('invalid','missing')"; return cmd.ExecuteNonQuery(); }));
    }

    [Fact]
    public void NormalizedRowsRoundTripAllMapMetadataWithoutPlayerPosition()
    {
        var store = new SqliteRoomMapStore(Database, Legacy);
        var map = Map();
        store.Save("mud.example", 4000, map);
        var loaded = store.Load("MUD.EXAMPLE", 4000)!;
        Assert.Equal(MapFileFormat.Serialize(map with { CurrentRoomId = null, State = MapTrackingState.Unknown }), MapFileFormat.Serialize(loaded));
        Assert.Equal(2L, Database.Read(c => { using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM map_rooms WHERE area='Keep' AND x=1 AND y=2 AND z=3"; return (long)cmd.ExecuteScalar()!; }));
        Assert.Null(store.Load("mud.example", 4001));
    }

    [Fact]
    public void SearchRoomsMatchesObservedTextOfTheStoredWorldOnly()
    {
        IRoomMapStore store = new SqliteRoomMapStore(Database, Legacy);
        var rooms = new[]
        {
            Room("a") with { ObservedName = "Ancient Temple", ObservedDescription = "A crumbling temple." },
            Room("b") with { ObservedName = "Market Square", ObservedDescription = "A busy square." },
            Room("c") with { ObservedName = null, ObservedDescription = null },
        };
        store.Save("host", 4, new MapSnapshot(rooms, [], [], null, MapTrackingState.Unknown, RoomDataSource.Text, 0));
        store.Save("other", 4, new MapSnapshot([Room("d") with { ObservedName = "Temple" }], [], [], null, MapTrackingState.Unknown, RoomDataSource.Text, 0));
        Assert.Equal(["a"], store.SearchRooms("host", 4, "temple").Select(r => r.Id));
        Assert.Empty(store.SearchRooms("host", 4, "cathedral"));
        Assert.Empty(store.SearchRooms("missing.example", 4, "temple"));
    }

    [Fact]
    public void ConcurrentStaleSessionsCannotReviveDeletedRoomsOrExits()
    {
        var firstStore = new SqliteRoomMapStore(Database, Legacy);
        firstStore.Save("host", 4, Map());
        var first = new RoomMapTracker(firstStore.Load("host", 4));
        var stale = new RoomMapTracker(firstStore.Load("host", 4));
        first.RemoveRoom("b");
        firstStore.Save("host", 4, first.Snapshot);
        new SqliteRoomMapStore(Database, Legacy).Save("host", 4, stale.Snapshot);
        var loaded = firstStore.Load("host", 4)!;
        Assert.Single(loaded.Rooms);
        Assert.Empty(loaded.Links);
        first.Undo();
        firstStore.Save("host", 4, first.Snapshot);
        Assert.Equal(2, firstStore.Load("host", 4)!.Rooms.Count);
    }

    [Fact]
    public async Task SimultaneousStoreInstancesSerializeMerges()
    {
        var store = new SqliteRoomMapStore(Database, Legacy);
        store.Save("host", 4, Map());
        var edited = new RoomMapTracker(store.Load("host", 4));
        var stale = new RoomMapTracker(store.Load("host", 4));
        edited.RemoveRoom("b");
        stale.UpsertRoom(Room("branch"));
        await Task.WhenAll(
            Task.Run(() => new SqliteRoomMapStore(Database, Legacy).Save("host", 4, edited.Snapshot)),
            Task.Run(() => new SqliteRoomMapStore(Database, Legacy).Save("host", 4, stale.Snapshot)));
        Assert.Equal(new[] { "a", "branch" }, store.Load("host", 4)!.Rooms.Select(r => r.Id).Order());
    }

    [Fact]
    public void TransactionRollsBackEarlierMutationsOnFailure()
    {
        var db = Database;
        Assert.Throws<IOException>(() => db.Write<int>((c, t) =>
        {
            using var command = c.CreateCommand(); command.Transaction = t;
            command.CommandText = "INSERT INTO worlds(id) VALUES('test'); INSERT INTO endpoints(endpoint_key,world_id) VALUES('bad','missing')";
            return command.ExecuteNonQuery();
        }));
        Assert.Equal(0L, db.Read(c => { using var command = c.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM worlds"; return (long)command.ExecuteScalar()!; }));
    }

    [Fact]
    public void NewerSchemaIsRejectedWithoutResettingItsVersion()
    {
        var db = Database;
        using (var connection = db.OpenConnection())
        using (var command = connection.CreateCommand()) { command.CommandText = "PRAGMA user_version=99"; command.ExecuteNonQuery(); }
        Assert.Throws<IOException>(() => Database.OpenConnection());
        using var raw = new SqliteConnection("Data Source=" + db.FilePath); raw.Open();
        using var version = raw.CreateCommand(); version.CommandText = "PRAGMA user_version";
        Assert.Equal(99L, version.ExecuteScalar());
    }

    [Fact]
    public void LegacyImportBeforeFirstSaveIsIdempotentAndLeavesOriginalUntouched()
    {
        var legacyStore = new RoomMapStore(Legacy);
        legacyStore.Save("host", 4, Map());
        var legacyPath = Directory.GetFiles(Legacy).Single();
        var original = File.ReadAllBytes(legacyPath);
        var store = new SqliteRoomMapStore(Database, Legacy);
        var tracker = new RoomMapTracker();
        tracker.UpsertRoom(Room("new"));
        store.Save("host", 4, tracker.Snapshot);
        Assert.Equal(3, store.Load("host", 4)!.Rooms.Count);
        tracker = new RoomMapTracker(store.Load("host", 4));
        tracker.RemoveRoom("a");
        store.Save("host", 4, tracker.Snapshot);
        Assert.Equal(2, new SqliteRoomMapStore(Database, Legacy).Load("host", 4)!.Rooms.Count);
        Assert.Equal(original, File.ReadAllBytes(legacyPath));
    }

    [Fact]
    public void RawLegacyEndpointCanBeImportedAfterCanonicalMissAndHostEdit()
    {
        new RoomMapStore(Legacy).Save("mud.example.", 4000, Map());
        var store = new SqliteRoomMapStore(Database, Legacy);
        Assert.Null(store.Load("mud.example", 4000));
        Assert.NotNull(store.Load("mud.example.", 4000));
        var db = Database;
        var world = db.Write((c, t) => db.ResolveWorld(c, t, "mud.example.:4000:True"));
        db.Write((c, t) => db.ResolveWorld(c, t, "new.example:5000", world));
        Assert.NotNull(new SqliteRoomMapStore(Database, Legacy).Load("new.example", 5000));
    }

    [Fact]
    public void HostEditBeforeFirstMapAccessKeepsRawLegacySource()
    {
        new RoomMapStore(Legacy).Save("original.example.", 4000, Map());
        var db = Database;
        var world = db.Write((c, t) => db.ResolveWorld(c, t, "original.example.:4000:False"));
        db.Write((c, t) => db.ResolveWorld(c, t, "new.example:5000", world));
        Assert.NotNull(new SqliteRoomMapStore(Database, Legacy).Load("new.example", 5000));
    }

    [Fact]
    public void MalformedLegacyMapBlocksMutationAndRetainsOriginal()
    {
        Directory.CreateDirectory(Legacy);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("host:4")));
        var path = Path.Combine(Legacy, hash + ".json");
        File.WriteAllText(path, "{invalid");
        Assert.Throws<IOException>(() => new SqliteRoomMapStore(Database, Legacy).Save("host", 4, Map()));
        Assert.Equal("{invalid", File.ReadAllText(path));
        Assert.Equal(0L, Database.Read(c => { using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM map_snapshots"; return (long)cmd.ExecuteScalar()!; }));
    }

    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
