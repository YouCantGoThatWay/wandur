using Wandur.Core.Mapping;
using Wandur.Core.Storage;

namespace Wandur.Core.Tests;

public sealed class MapIdentityTests
{
    private static RoomObservation Room(string id, string name, string exit) =>
        new(id, name, "", new Dictionary<string, string?> { [exit] = null }, "Ring of Kafrene", RoomDataSource.Gmcp);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ServerIdentityUpgradesLegacyRoomsAndWitnessedLinksWithoutLosingEdits(bool duplicatesExist)
    {
        var cockpit = new MapRoom("t:cockpit", "My cockpit", "Saved description", null, 0, 1, 0, true)
        { ObservedName = "The Cockpit [Hotel]", KnownExits = ["south"], Notes = "Keep this", IsManuallyEdited = true, Revision = 1 };
        var bunks = new MapRoom("t:bunks", "Passenger Bunks", "", null, 0, 0, 0, true)
        { KnownExits = ["north"], IsManuallyEdited = true, Revision = 1 };
        List<MapRoom> rooms = [cockpit, bunks];
        List<MapLink> links = [new(cockpit.Id, bunks.Id, "south", false) { IsManuallyEdited = true },
            new(bunks.Id, cockpit.Id, "north", false) { IsManuallyEdited = true }];
        if (duplicatesExist)
        {
            rooms.Add(new("s:561", "The Cockpit", "", "Ring of Kafrene", 4, 0, 0, false, "561") { KnownExits = ["south"] });
            rooms.Add(new("s:562", "Passenger Bunks", "", "Ring of Kafrene", 4, -1, 0, false, "562") { KnownExits = ["north"] });
            links.Add(new("s:561", "s:562", "south", true));
        }
        var tracker = new RoomMapTracker(new(rooms, links, [], null, MapTrackingState.Unknown, RoomDataSource.Gmcp, 0));
        tracker.Observe(Room("561", "The Cockpit", "south"));
        tracker.Observe(Room("562", "Passenger Bunks", "north"), "south");
        tracker.Observe(Room("561", "The Cockpit", "south"), "north");
        var map = tracker.Snapshot;
        Assert.Equal(2, map.Rooms.Count);
        Assert.All(map.Rooms, room => Assert.False(room.Provisional));
        Assert.Equal("Keep this", map.Rooms.Single(r => r.ServerId == "561").Notes);
        Assert.Equal("My cockpit", map.Rooms.Single(r => r.ServerId == "561").Name);
        Assert.Equal(1, map.Rooms.Single(r => r.ServerId == "561").Y);
        Assert.NotNull(MapRoutePlanner.FindRoute(map, "s:561", "s:562"));
        Assert.NotNull(MapRoutePlanner.FindRoute(map, "s:562", "s:561"));
        Assert.Equal(2, map.Links.Count);
        MapFileFormat.Serialize(map);
        var directory = Path.Combine(Path.GetTempPath(), "wandur-identity-" + Guid.NewGuid());
        try
        {
            var store = new SqliteRoomMapStore(new ClientDatabase(Path.Combine(directory, "map.db")), Path.Combine(directory, "legacy"));
            var stale = new MapSnapshot(rooms, links, [], null, MapTrackingState.Unknown, RoomDataSource.Gmcp, 0);
            store.Save("host", 4000, stale);
            store.Save("host", 4000, map);
            store.Save("host", 4000, stale);
            var restored = store.Load("host", 4000)!;
            Assert.Equal(2, restored.Rooms.Count);
            Assert.Equal(2, restored.Links.Count);
            Assert.NotNull(MapRoutePlanner.FindRoute(restored, "s:561", "s:562"));
            Assert.NotNull(MapRoutePlanner.FindRoute(restored, "s:562", "s:561"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void AmbiguousLegacyRoomsAreNotMergedByTitle()
    {
        MapRoom[] rooms = [new("t:one", "Corridor", "", null, 0, 0, 0, true) { KnownExits = ["north"] },
            new("t:two", "Corridor", "", null, 1, 0, 0, true) { KnownExits = ["north"] }];
        var tracker = new RoomMapTracker(new(rooms, [], [], null, MapTrackingState.Unknown, RoomDataSource.Text, 0));
        tracker.Observe(Room("123", "Corridor", "north"));
        Assert.Equal(3, tracker.Snapshot.Rooms.Count);
        Assert.Empty(tracker.Snapshot.RoomAliases);
    }

    [Fact]
    public void MovementToAnIdenticallyNamedRoomDoesNotAdoptTheOriginIdentity()
    {
        var tracker = new RoomMapTracker();
        tracker.Observe(Room("123", "Corridor", "north") with { ServerId = null });
        var origin = tracker.Snapshot.CurrentRoomId;
        tracker.Observe(Room("124", "Corridor", "north"), "north");
        Assert.Equal(2, tracker.Snapshot.Rooms.Count);
        Assert.Equal(origin, Assert.Single(tracker.Snapshot.Links).FromId);
        Assert.Empty(tracker.Snapshot.RoomAliases);
    }

    [Fact]
    public void ConfirmingAManualEdgePreservesRestrictionsAndDoesNotRedirectIt()
    {
        var tracker = new RoomMapTracker();
        tracker.Observe(Room("561", "The Cockpit", "south"));
        tracker.Observe(Room("562", "Passenger Bunks", "north"), "south");
        tracker.UpsertLink(new("s:561", "s:562", "south", false)
        { IsLocked = true, DoorState = MapDoorState.Closed, Command = "enter hatch", Weight = 5 });
        tracker.Observe(Room("561", "The Cockpit", "south"));
        tracker.Observe(Room("562", "Passenger Bunks", "north"), "south");
        var link = Assert.Single(tracker.Snapshot.Links);
        Assert.True(link.Confirmed);
        Assert.True(link.IsLocked);
        Assert.Equal(MapDoorState.Closed, link.DoorState);
        Assert.Equal("enter hatch", link.Command);
        Assert.Equal(5, link.Weight);
        Assert.Null(MapRoutePlanner.FindRoute(tracker.Snapshot, "s:561", "s:562"));
        tracker.Observe(Room("561", "The Cockpit", "south"));
        tracker.Observe(Room("563", "Elsewhere", "north"), "south");
        Assert.Equal("s:562", Assert.Single(tracker.Snapshot.Links).ToId);
    }

    [Fact]
    public void IdentityMergeKeepsEditedExitRestrictionsOverAutomaticDuplicateEdges()
    {
        MapRoom[] rooms = [new("t:old", "The Cockpit", "", null, 0, 1, 0, true) { KnownExits = ["south"] },
            new("s:561", "The Cockpit", "", null, 4, 0, 0, false, "561") { KnownExits = ["south"] },
            new("s:562", "Passenger Bunks", "", null, 4, -1, 0, false, "562") { KnownExits = ["north"] }];
        MapLink[] links = [new("t:old", "s:562", "south", false) { IsManuallyEdited = true, IsLocked = true, Weight = 9 },
            new("s:561", "s:562", "south", true)];
        var tracker = new RoomMapTracker(new(rooms, links, [], null, MapTrackingState.Unknown, RoomDataSource.Gmcp, 0));
        tracker.Observe(Room("561", "The Cockpit", "south"));
        var link = Assert.Single(tracker.Snapshot.Links);
        Assert.True(link.IsLocked);
        Assert.Equal(9, link.Weight);
        Assert.Null(MapRoutePlanner.FindRoute(tracker.Snapshot, "s:561", "s:562"));
    }
}
