using System.Text.Json;
using Wandur.Core.Mapping;

namespace Wandur.Core.Tests;

public sealed class ExpandedMapTests
{
    private static MapRoom Room(string id) => new(id, id, "", "Keep", 0, 0, 0, false);
    private static MapSnapshot Map(params MapRoom[] rooms) => new(rooms, [], [], null, MapTrackingState.Unknown, RoomDataSource.Gmcp, 0);

    [Fact]
    public void RenamingTextRoomPreservesRecognitionEvidenceAfterReload()
    {
        var tracker = new RoomMapTracker();
        var observed = new RoomObservation(null, "Hall", "Stone walls.", new Dictionary<string, string?>());
        tracker.Observe(observed);
        var id = tracker.Snapshot.CurrentRoomId;
        tracker.UpsertRoom(tracker.Snapshot.Rooms[0] with { Name = "My camp", Description = "Rest here", Area = "My area" });
        tracker = new RoomMapTracker(MapFileFormat.Deserialize(MapFileFormat.Serialize(tracker.Snapshot)));
        tracker.Observe(observed);
        Assert.Equal(id, tracker.Snapshot.CurrentRoomId);
        Assert.Single(tracker.Snapshot.Rooms);
        Assert.Equal("My camp", tracker.Snapshot.Rooms[0].Name);
    }

    [Fact]
    public void OversizedOptionalProtocolFieldsCannotPoisonTheCache()
    {
        var tracker = new RoomMapTracker();
        tracker.Observe(new("1", "Hall", "", Enumerable.Range(0, 100).ToDictionary(i => "exit" + i, _ => (string?)null), new string('a', 1000))
        { Environment = new string('a', 2000), Symbol = new string('a', 100), X = double.PositiveInfinity });
        var loaded = MapFileFormat.Deserialize(MapFileFormat.Serialize(tracker.Snapshot));
        Assert.Single(loaded.Rooms);
        Assert.Null(loaded.Rooms[0].Environment);
        Assert.True(loaded.Rooms[0].KnownExits.Count <= 64);
    }

    [Fact]
    public void UndoExitDeletionAllowsLaterObservedDestinationChanges()
    {
        var tracker = new RoomMapTracker();
        tracker.Observe(new("1", "Hall", "", new Dictionary<string, string?> { ["north"] = "2" }));
        tracker.RemoveLink("s:1", "north");
        tracker.Undo();
        tracker.Observe(new("1", "Hall", "", new Dictionary<string, string?> { ["north"] = "3" }));
        Assert.Equal("s:3", Assert.Single(tracker.Snapshot.Links).ToId);
    }

    [Fact]
    public void UnassignedAreaGridSettingRoundTrips()
    {
        var tracker = new RoomMapTracker();
        tracker.SetAreaSettings(new("", true));
        var settings = Assert.Single(MapFileFormat.Deserialize(MapFileFormat.Serialize(tracker.Snapshot)).AreaSettings);
        Assert.Equal("", settings.Area);
        Assert.True(settings.GridMode);
    }

    [Fact]
    public void LegacyCacheWithoutNewFieldsRetainsSafeDefaults()
    {
        var map = MapFileFormat.Deserialize("""
            {"Rooms":[{"Id":"a","Name":"Hall","Description":"","Area":null,"X":0,"Y":0,"Z":0,"Provisional":true}],
             "Links":[],"CandidateRoomIds":[],"CurrentRoomId":null,"State":4,"Source":0,"ObservationCount":0}
            """);
        Assert.Equal(1, map.Rooms[0].Weight);
        Assert.Equal("", map.Rooms[0].Notes);
        Assert.Empty(map.AreaSettings);
        Assert.Empty(map.DeletedRooms);
    }

    [Fact]
    public void StaleRoomObservationsCannotOverwriteSavedManualEdits()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wandur-map-" + Guid.NewGuid());
        try
        {
            var store = new RoomMapStore(directory);
            store.Save("host", 4, Map(Room("s:1")));
            var edited = new RoomMapTracker(store.Load("host", 4));
            var stale = new RoomMapTracker(store.Load("host", 4));
            edited.UpsertRoom(edited.Snapshot.Rooms[0] with { Name = "My camp", Color = "#112233" });
            store.Save("host", 4, edited.Snapshot);
            stale.Observe(new("1", "Changed server hall", "", new Dictionary<string, string?>()));
            new RoomMapStore(directory).Save("host", 4, stale.Snapshot);
            var room = Assert.Single(store.Load("host", 4)!.Rooms);
            Assert.Equal("My camp", room.Name);
            Assert.Equal("#112233", room.Color);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void ManualRoomAndExitEditsSurviveObservation()
    {
        var tracker = new RoomMapTracker();
        tracker.Observe(new("1", "Hall", "", new Dictionary<string, string?> { ["n"] = "2" }));
        Assert.True(tracker.UpsertRoom(tracker.Snapshot.Rooms[0] with { Name = "My hall", X = 12, Environment = "forest", Notes = "home" }));
        Assert.True(tracker.UpsertLink(tracker.Snapshot.Links[0] with { Command = "enter portal", IsLocked = true }));
        tracker.Observe(new("1", "Server hall", "New prose", new Dictionary<string, string?> { ["n"] = "3" }) { X = 99, Environment = "city" });
        var room = Assert.Single(tracker.Snapshot.Rooms);
        Assert.Equal("My hall", room.Name);
        Assert.Equal(12, room.X);
        Assert.Equal("forest", room.Environment);
        Assert.Equal("home", room.Notes);
        var link = Assert.Single(tracker.Snapshot.Links);
        Assert.Equal("s:2", link.ToId);
        Assert.Equal("enter portal", link.Command);
        Assert.True(link.IsLocked);
    }

    [Fact]
    public void DeleteUndoRedoAndMergeMaintainGraph()
    {
        var tracker = new RoomMapTracker(Map(Room("a"), Room("b"), Room("c")) with { Links = [new("a", "b", "north", true), new("b", "c", "east", true)] });
        Assert.True(tracker.SetCurrentRoom("b"));
        Assert.True(tracker.MergeRooms("b", "a"));
        Assert.Equal("a", tracker.Snapshot.CurrentRoomId);
        Assert.Equal("c", Assert.Single(tracker.Snapshot.Links).ToId);
        Assert.True(tracker.Undo());
        Assert.Equal(3, tracker.Snapshot.Rooms.Count);
        Assert.True(tracker.Redo());
        Assert.Equal(2, tracker.Snapshot.Rooms.Count);
        Assert.True(tracker.RemoveRoom("a"));
        Assert.Empty(tracker.Snapshot.Links);
        Assert.Null(tracker.Snapshot.CurrentRoomId);
    }

    [Fact]
    public void DeletedRoomsAndExitsCannotBeResurrectedByStaleSessions()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wandur-map-" + Guid.NewGuid());
        try
        {
            var store = new RoomMapStore(directory);
            var snapshot = Map(Room("a"), Room("b"), Room("c")) with { Links = [new("a", "b", "north", true), new("b", "c", "east", true)] };
            store.Save("host", 4, snapshot);
            var active = new RoomMapTracker(store.Load("host", 4));
            var stale = new RoomMapTracker(store.Load("host", 4));
            Assert.True(active.RemoveLink("a", "north"));
            Assert.True(active.RemoveRoom("c"));
            store.Save("host", 4, active.Snapshot);
            store.Save("host", 4, stale.Snapshot);
            var saved = store.Load("host", 4)!;
            Assert.Equal(2, saved.Rooms.Count);
            Assert.Empty(saved.Links);
            Assert.True(active.Undo());
            store.Save("host", 4, active.Snapshot);
            Assert.Equal(3, store.Load("host", 4)!.Rooms.Count);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void RouteUsesDirectedWeightedSafeExits()
    {
        var map = Map(Room("a"), Room("b") with { Weight = 8 }, Room("c"), Room("d")) with
        { Links = [new("a", "b", "north", true), new("b", "d", "east", true), new("a", "c", "east", true), new("c", "d", "north", true)] };
        var route = MapRoutePlanner.FindRoute(map, "a", "d")!;
        Assert.Equal(2, route.Cost);
        Assert.Equal("c", route.Steps[0].ToId);
        Assert.Null(MapRoutePlanner.FindRoute(map, "d", "a"));
        map = map with { Links = map.Links.Select(l => l.FromId == "c" ? l with { DoorState = MapDoorState.Locked } : l).ToArray() };
        Assert.Equal(9, MapRoutePlanner.FindRoute(map, "a", "d")!.Cost);
        map = map with { Rooms = map.Rooms.Select(r => r.Id == "b" ? r with { IsLocked = true } : r).ToArray() };
        Assert.Null(MapRoutePlanner.FindRoute(map, "a", "d"));
    }

    [Fact]
    public void InferredRoutesRequireExplicitOptIn()
    {
        var map = Map(Room("a"), Room("b")) with { Links = [new("a", "b", "east", false)] };
        Assert.Null(MapRoutePlanner.FindRoute(map, "a", "b"));
        Assert.NotNull(MapRoutePlanner.FindRoute(map, "a", "b", allowInferred: true));
    }

    [Fact]
    public void MapFilesRoundTripAndAcceptLegacySnapshots()
    {
        var map = Map(Room("a") with { Environment = "forest", Symbol = "♣", Notes = "camp" }) with { AreaSettings = [new("Keep", true)] };
        var loaded = MapFileFormat.Deserialize(MapFileFormat.Serialize(map));
        Assert.Equal("camp", loaded.Rooms[0].Notes);
        Assert.True(loaded.AreaSettings[0].GridMode);
        Assert.Single(MapFileFormat.Deserialize(JsonSerializer.Serialize(map)).Rooms);
        Assert.Throws<FormatException>(() => MapFileFormat.Deserialize("{\"Version\":999,\"Map\":{}}"));
        Assert.Throws<FormatException>(() => MapFileFormat.Deserialize(JsonSerializer.Serialize(map with { Rooms = [Room("a") with { Weight = -1 }] })));
    }

    [Fact]
    public void RenamingAnExitAndAddingReturnIsAtomicAndUndoable()
    {
        var tracker = new RoomMapTracker(Map(Room("a"), Room("b")) with { Links = [new("a", "b", "east", true)] });
        Assert.False(tracker.EditLink(new("a", "b", "north", true), "east", new("b", "a", "south", true) { Weight = -1 }));
        Assert.Equal("east", Assert.Single(tracker.Snapshot.Links).Direction);
        Assert.True(tracker.EditLink(new("a", "b", "north", true), "east", new("b", "a", "south", true)));
        Assert.Equal(2, tracker.Snapshot.Links.Count);
        Assert.True(tracker.Undo());
        Assert.Equal("east", Assert.Single(tracker.Snapshot.Links).Direction);
        Assert.True(tracker.Redo());
        Assert.Equal(2, tracker.Snapshot.Links.Count);
    }

    [Fact]
    public void ImportIsOneUndoableReplacementAndTombstonesOldData()
    {
        var tracker = new RoomMapTracker(Map(Room("a"), Room("b")));
        Assert.True(tracker.ReplaceMap(Map(Room("c"))));
        Assert.Equal("c", Assert.Single(tracker.Snapshot.Rooms).Id);
        Assert.Equal(2, tracker.Snapshot.DeletedRooms.Count);
        Assert.True(tracker.Undo());
        Assert.Equal(new[] { "a", "b" }, tracker.Snapshot.Rooms.Select(r => r.Id).Order());
        Assert.True(tracker.Redo());
        Assert.Equal("c", Assert.Single(tracker.Snapshot.Rooms).Id);
    }

    [Fact]
    public void UndoPreservesRoomsDiscoveredAfterTheManualEdit()
    {
        var tracker = new RoomMapTracker(Map(Room("a")));
        tracker.UpsertRoom(Room("a") with { Notes = "camp" });
        tracker.Observe(new("2", "New discovery", "", new Dictionary<string, string?>()));
        Assert.True(tracker.Undo());
        Assert.Equal(2, tracker.Snapshot.Rooms.Count);
        Assert.Equal("", tracker.Snapshot.Rooms.Single(r => r.Id == "a").Notes);
    }

    [Fact]
    public void MergeRemapsSubsequentAuthoritativeObservationsAfterReload()
    {
        var tracker = new RoomMapTracker(Map(Room("s:1"), Room("s:2")));
        Assert.True(tracker.MergeRooms("s:1", "s:2"));
        tracker = new RoomMapTracker(MapFileFormat.Deserialize(MapFileFormat.Serialize(tracker.Snapshot)));
        tracker.Observe(new("1", "Hall", "", new Dictionary<string, string?>()));
        Assert.Single(tracker.Snapshot.Rooms);
        Assert.Equal("s:2", tracker.Snapshot.CurrentRoomId);
    }

    [Theory]
    [InlineData("{\"Rooms\":[null],\"Links\":[],\"CandidateRoomIds\":[]}")]
    [InlineData("{\"Rooms\":[],\"Links\":[],\"CandidateRoomIds\":[],\"AreaSettings\":null}")]
    [InlineData("[]")]
    [InlineData("null")]
    public void MalformedMapImportsAreRejected(string json) => Assert.Throws<FormatException>(() => MapFileFormat.Deserialize(json));

    [Fact]
    public void MsdpCoordinateAndTerrainUpdatesNeverStitchAcrossRooms()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("\u0001ROOM_VNUM\u000242\u0001ROOM_NAME\u0002Hall\u0001ROOM_TERRAIN\u0002forest\u0001ROOM_X\u000212\u0001ROOM_Y\u0002-4\u0001ROOM_Z\u00022");
        var room = RoomProtocolDecoder.FromMsdp(bytes)!;
        Assert.Equal("forest", room.Environment);
        Assert.Equal(12, room.X);
        Assert.Equal(-4, room.Y);
        Assert.Null(RoomProtocolDecoder.FromMsdp(System.Text.Encoding.UTF8.GetBytes("\u0001ROOM_X\u000299")));
        room = RoomProtocolDecoder.FromMsdp(System.Text.Encoding.UTF8.GetBytes("\u0001ROOM_VNUM\u000243\u0001ROOM_NAME\u0002Other"))!;
        Assert.Null(room.X);
        Assert.Null(room.Environment);
    }

    [Fact]
    public void ProtocolSuppliedCoordinatesAndTerrainAreCoherentAndFinite()
    {
        var room = RoomProtocolDecoder.FromGmcp("Room.Info {\"num\":1,\"name\":\"Hall\",\"environment\":\"forest\",\"coord\":{\"x\":12,\"y\":-3,\"z\":2}}")!;
        Assert.Equal("forest", room.Environment);
        Assert.Equal(12, room.X);
        Assert.Equal(-3, room.Y);
        Assert.Equal(2, room.Z);
        var malformed = RoomProtocolDecoder.FromGmcp("Room.Info {\"num\":2,\"name\":\"Hall\",\"x\":\"NaN\",\"y\":1e999}")!;
        Assert.Null(malformed.X);
        Assert.Null(malformed.Y);
        Assert.Null(malformed.Environment);
        var tracker = new RoomMapTracker();
        tracker.Observe(room);
        Assert.Equal(12, tracker.Snapshot.Rooms[0].X);
    }
}
