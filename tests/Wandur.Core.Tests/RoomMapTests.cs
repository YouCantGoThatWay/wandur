using Wandur.Core.Mapping;

namespace Wandur.Core.Tests;

public sealed class RoomMapTests
{
    [Theory]
    [InlineData(RoomDataSource.Gmcp)]
    [InlineData(RoomDataSource.Msdp)]
    public void UncommandedRoomChangeStartsASeparateComponentAndMovementContinuesThere(RoomDataSource source)
    {
        RoomObservation At(string id) => new(id, "Room " + id, "", new Dictionary<string, string?>(), Source: source);
        var tracker = new RoomMapTracker();
        tracker.Observe(At("1"));
        tracker.Observe(At("2"), "east");
        var originalLinks = tracker.Snapshot.Links.ToArray();
        tracker.Observe(At("3")); // teleport, transport, follow, or a server-driven move
        Assert.Equal("s:3", tracker.Snapshot.CurrentRoomId);
        Assert.Equal(MapTrackingState.Confirmed, tracker.Snapshot.State);
        Assert.Equal(originalLinks, tracker.Snapshot.Links);
        var destination = tracker.Snapshot.Rooms.Single(r => r.Id == "s:3");
        Assert.All(tracker.Snapshot.Rooms.Where(r => r.Id != "s:3"), room =>
            Assert.True(Math.Max(Math.Abs(room.X - destination.X), Math.Abs(room.Y - destination.Y)) > 1));
        tracker.Observe(At("4"), "north");
        Assert.NotNull(MapRoutePlanner.FindRoute(tracker.Snapshot, "s:3", "s:4"));
        Assert.Null(MapRoutePlanner.FindRoute(tracker.Snapshot, "s:1", "s:4"));
        tracker.Observe(At("1")); // revisiting a known room keeps its original topology
        Assert.Equal("s:1", tracker.Snapshot.CurrentRoomId);
        Assert.Equal(4, tracker.Snapshot.Rooms.Count);
        Assert.Equal(2, tracker.Snapshot.Links.Count);
    }

    [Fact]
    public void TextGuessBeforeMetadataRelocalizesToTheSavedRoom()
    {
        var tracker = new RoomMapTracker();
        tracker.Observe(Room("Passenger Bunks", "north"));
        var saved = tracker.Snapshot;
        var actual = saved.CurrentRoomId;
        tracker = new RoomMapTracker(saved);
        tracker.Observe(Room("Welcome tutorial", "north"));
        var guess = tracker.Snapshot.CurrentRoomId!;
        Assert.True(tracker.RefineProvisionalRoom(guess, tracker.Snapshot.ObservationCount,
            Room("Passenger Bunks", "north") with { Source = RoomDataSource.Gmcp }));
        Assert.Single(tracker.Snapshot.Rooms);
        Assert.Equal(actual, tracker.Snapshot.CurrentRoomId);
        Assert.Contains(tracker.Snapshot.DeletedRooms, deletion => deletion.Id == guess);
        Assert.Empty(tracker.Snapshot.Links);
    }

    [Fact]
    public void ProvisionalRefinementRejectsStaleOrInvalidEvidenceAndPreservesManualEdits()
    {
        var tracker = new RoomMapTracker();
        tracker.Observe(Room("Guessed heading", "north"));
        var id = tracker.Snapshot.CurrentRoomId!;
        var observation = new RoomObservation(null, "Actual room", "", new Dictionary<string, string?>(), Source: RoomDataSource.Gmcp);
        Assert.False(tracker.RefineProvisionalRoom(id, 0, observation));
        Assert.False(tracker.RefineProvisionalRoom(id, 1, observation with { Name = new string('x', 513) }));
        Assert.Equal("Guessed heading", Assert.Single(tracker.Snapshot.Rooms).Name);
        Assert.True(tracker.RefineProvisionalRoom(id, 1, observation with { Area = "bad\0area" }));
        Assert.Null(Assert.Single(tracker.Snapshot.Rooms).Area);
        Assert.Equal(id, tracker.Snapshot.CurrentRoomId);
        MapFileFormat.Serialize(tracker.Snapshot);
        tracker.UpsertRoom(tracker.Snapshot.Rooms[0] with { Name = "My room name" });
        Assert.False(tracker.RefineProvisionalRoom(id, tracker.Snapshot.ObservationCount, observation));
        Assert.Equal("My room name", tracker.Snapshot.Rooms[0].Name);
    }

    [Fact]
    public void ObservedReturnToADistinctiveRoomRecordsTheReturnExit()
    {
        var tracker = new RoomMapTracker();
        tracker.Observe(Room("Passenger Bunks", "north"));
        var bunks = tracker.Snapshot.CurrentRoomId;
        tracker.Observe(Room("Cockpit", "south"), "north");
        var cockpit = tracker.Snapshot.CurrentRoomId;
        Assert.Single(tracker.Snapshot.Links); // No return edge until the return is observed.
        for (var i = 0; i < 3; i++)
        {
            tracker.Observe(Room("Passenger Bunks", "north"), "south");
            Assert.Equal(bunks, tracker.Snapshot.CurrentRoomId);
            tracker.Observe(Room("Cockpit", "south"), "north");
            Assert.Equal(cockpit, tracker.Snapshot.CurrentRoomId);
        }
        Assert.Equal(2, tracker.Snapshot.Rooms.Count);
        Assert.Equal(2, tracker.Snapshot.Links.Count);
        Assert.All(tracker.Snapshot.Links, link => Assert.False(link.Confirmed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IdenticalRoomsOrLostPositionCannotProveAReturnExit(bool manualPlacement)
    {
        var tracker = new RoomMapTracker();
        tracker.Observe(Room("Corridor", "north", "south"));
        tracker.Observe(Room("Corridor", "north", "south"), "north");
        tracker.Observe(Room("Corridor", "north", "south"), "south");
        Assert.Null(tracker.Snapshot.CurrentRoomId);
        Assert.Single(tracker.Snapshot.Links);

        tracker = new RoomMapTracker();
        tracker.Observe(Room("Bunks", "north"));
        var bunks = tracker.Snapshot.CurrentRoomId!;
        tracker.Observe(Room("Cockpit", "south"), "north");
        if (manualPlacement)
        {
            var cockpit = tracker.Snapshot.CurrentRoomId!;
            tracker.SetCurrentRoom(bunks); tracker.SetCurrentRoom(cockpit);
        }
        else
        {
            tracker.LosePosition();
            tracker.Observe(Room("Cockpit", "south"));
        }
        tracker.Observe(Room("Bunks", "north"), "south");
        Assert.Null(tracker.Snapshot.CurrentRoomId);
        Assert.Single(tracker.Snapshot.Links);
    }

    private static RoomObservation Room(string name, params string[] exits) => new(null, name, "Stone walls and a worn floor.", exits.ToDictionary(x => x, _ => (string?)null));
    private static MapSnapshot CorridorMap() => new(
        [new("a", "Corridor", "Stone walls and a worn floor.", null, 0, 0, 0, true),
         new("b", "Corridor", "Stone walls and a worn floor.", null, 0, 1, 0, true),
         new("c", "Corridor", "Stone walls and a worn floor.", null, 0, 2, 0, true),
         new("d", "Corridor", "Stone walls and a worn floor.", null, 3, 0, 0, true),
         new("shrine", "Shrine", "A marble altar.", null, 0, 3, 0, true)],
        [new("a", "b", "north", false), new("b", "c", "north", false), new("c", "shrine", "north", false), new("d", "shrine", "east", false)],
        [], null, MapTrackingState.Unknown, RoomDataSource.Text, 0);

    [Fact]
    public void ConsecutiveRoomsNarrowIdenticalCandidatesWithoutChangingTheGraph()
    {
        var tracker = new RoomMapTracker(CorridorMap());
        tracker.Observe(Room("Corridor"));
        Assert.Equal(4, tracker.Snapshot.CandidateRoomIds.Count);
        Assert.Null(tracker.Snapshot.CurrentRoomId);
        tracker.Observe(Room("Corridor"), "north");
        Assert.Equal(new[] { "b", "c" }, tracker.Snapshot.CandidateRoomIds.Order());
        tracker.Observe(Room("Corridor"), "north");
        Assert.Equal("c", tracker.Snapshot.CurrentRoomId);
        Assert.Equal(MapTrackingState.Inferred, tracker.Snapshot.State);
        Assert.Equal(5, tracker.Snapshot.Rooms.Count);
        Assert.Equal(4, tracker.Snapshot.Links.Count);
    }

    [Fact]
    public void IdenticalAdjacentNewRoomsAreNotCollapsedAndFailedMovesDoNotAdvance()
    {
        var tracker = new RoomMapTracker();
        tracker.Observe(Room("Corridor", "north", "south"));
        var first = tracker.Snapshot.CurrentRoomId;
        tracker.MovementFailed();
        Assert.Equal(first, tracker.Snapshot.CurrentRoomId);
        tracker.Observe(Room("Corridor", "north", "south"), "north");
        Assert.Equal(2, tracker.Snapshot.Rooms.Count);
        Assert.NotEqual(first, tracker.Snapshot.CurrentRoomId);
        var link = Assert.Single(tracker.Snapshot.Links);
        Assert.Equal("north", link.Direction);
        Assert.Equal(first, link.FromId);
        Assert.False(link.Confirmed);
    }

    [Fact]
    public void ServerIdentifiersWinOverIdenticalDescriptionsAndDoNotAssumeReverseExits()
    {
        var tracker = new RoomMapTracker();
        tracker.Observe(new("101", "Forest", "Trees.", new Dictionary<string, string?> { ["e"] = "102" }, Source: RoomDataSource.Gmcp));
        tracker.Observe(new("102", "Forest", "Trees.", new Dictionary<string, string?>(), Source: RoomDataSource.Gmcp), "e");
        Assert.Equal(2, tracker.Snapshot.Rooms.Count);
        Assert.Equal("s:102", tracker.Snapshot.CurrentRoomId);
        Assert.Equal(MapTrackingState.Confirmed, tracker.Snapshot.State);
        Assert.Single(tracker.Snapshot.Links);
        Assert.Equal("east", tracker.Snapshot.Links[0].Direction);
        tracker.Observe(new("101", "Changed forest", "Night falls.", new Dictionary<string, string?> { ["e"] = "102" }, Source: RoomDataSource.Gmcp));
        Assert.Equal(2, tracker.Snapshot.Rooms.Count);
        Assert.Equal("s:101", tracker.Snapshot.CurrentRoomId);
    }

    [Fact]
    public void UnknownRoomAfterTeleportDoesNotCreateAnInventedConnection()
    {
        var tracker = new RoomMapTracker(CorridorMap());
        tracker.Observe(new(null, "Shrine", "A marble altar.", new Dictionary<string, string?>()));
        tracker.LosePosition();
        tracker.Observe(Room("Desert"));
        Assert.Equal(6, tracker.Snapshot.Rooms.Count);
        Assert.Equal(4, tracker.Snapshot.Links.Count);
    }

    [Fact]
    public void LotjTagsRefineTheSameRoomInsteadOfCreatingDisconnectedDuplicates()
    {
        var tracker = new RoomMapTracker();
        tracker.Observe(new(null, "The Cockpit | Academy Transport Shuttle", "", new Dictionary<string, string?> { ["south"] = null }, Source: RoomDataSource.Gmcp));
        tracker.Observe(new(null, "The Cockpit | Academy Transport Shuttle  [Hotel]", "Panels glow.", new Dictionary<string, string?> { ["south"] = null }));
        Assert.Single(tracker.Snapshot.Rooms);
        var cockpit = tracker.Snapshot.CurrentRoomId;
        tracker.Observe(new(null, "Passenger Bunks | Academy Transport Shuttle", "", new Dictionary<string, string?> { ["north"] = null }, Source: RoomDataSource.Gmcp), "south");
        tracker.Observe(new(null, "Passenger Bunks | Academy Transport Shuttle [HOSPITAL][Hotel][ENGINE]", "Rows of beds.", new Dictionary<string, string?> { ["north"] = null }));
        Assert.Equal(2, tracker.Snapshot.Rooms.Count);
        Assert.Single(tracker.Snapshot.Links);
        var north = tracker.Snapshot.Rooms.Single(r => r.Id == cockpit);
        var south = tracker.Snapshot.Rooms.Single(r => r.Id == tracker.Snapshot.CurrentRoomId);
        Assert.Equal(north.X, south.X);
        Assert.True(south.Y < north.Y);
        Assert.Equal("south", tracker.Snapshot.Links[0].Direction);
    }

    [Fact]
    public void BracketedRoomIdentifiersRemainDistinct()
    {
        var tracker = new RoomMapTracker();
        tracker.Observe(Room("Cabin [101]", "north"));
        tracker.Observe(Room("Cabin [102]", "north"));
        Assert.Equal(2, tracker.Snapshot.Rooms.Count);
    }

    [Fact]
    public void ExitEvidenceRejectsAContradictoryLookalike()
    {
        var map = CorridorMap();
        var rooms = map.Rooms.Select(r => r with { KnownExits = r.Id == "a" ? ["east"] : ["north"] }).ToArray();
        var tracker = new RoomMapTracker(map with { Rooms = rooms });
        tracker.Observe(Room("Corridor", "east"));
        Assert.Equal("a", tracker.Snapshot.CurrentRoomId);
    }

    [Fact]
    public void TooManyCandidatesRemainUnknownInsteadOfChoosingFromATruncatedSet()
    {
        var rooms = Enumerable.Range(0, 300).Select(i => new MapRoom(i.ToString(), "Corridor", "Stone walls and a worn floor.", null, i, 0, 0, true)).ToArray();
        var tracker = new RoomMapTracker(new(rooms, [], [], null, MapTrackingState.Unknown, RoomDataSource.Text, 0));
        tracker.Observe(Room("Corridor"));
        Assert.Equal(MapTrackingState.Unknown, tracker.Snapshot.State);
        Assert.Null(tracker.Snapshot.CurrentRoomId);
        Assert.Empty(tracker.Snapshot.CandidateRoomIds);
    }

    [Fact]
    public void TwoSessionSavesKeepBothDiscoveredBranches()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wandur-map-" + Guid.NewGuid());
        try
        {
            var store = new RoomMapStore(directory);
            var first = new RoomMapTracker();
            var second = new RoomMapTracker();
            first.Observe(new("1", "Hall", "Stone.", new Dictionary<string, string?>(), Source: RoomDataSource.Gmcp));
            second.Observe(new("2", "Tower", "Stone.", new Dictionary<string, string?>(), Source: RoomDataSource.Gmcp));
            store.Save("host", 4000, first.Snapshot);
            store.Save("host", 4000, second.Snapshot);
            Assert.Equal(2, store.Load("host", 4000)!.Rooms.Count);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void MapCacheIsScopedToServerAndRestoresGraphWithoutAssumingPlayerPosition()
    {
        var path = Path.Combine(Path.GetTempPath(), "wandur-map-" + Guid.NewGuid());
        try
        {
            var store = new RoomMapStore(path);
            store.Save("mud.example", 4000, CorridorMap());
            var loaded = store.Load("MUD.EXAMPLE", 4000);
            Assert.NotNull(loaded);
            Assert.Equal(5, loaded.Rooms.Count);
            Assert.Null(store.Load("mud.example", 4001));
            var tracker = new RoomMapTracker(loaded);
            Assert.Null(tracker.Snapshot.CurrentRoomId);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
}
