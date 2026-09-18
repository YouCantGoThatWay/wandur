using Wandur.Core.Classification;
using Wandur.Core.Mapping;

namespace Wandur.Core.Tests;

public sealed class RoomInferenceTests
{
    private static RoomObservation At(string id, string name = "Room", string description = "Tall pines crowd the trail.", string? environment = null) =>
        new(id, name, description, new Dictionary<string, string?>(), Source: RoomDataSource.Gmcp) { Environment = environment };

    [Fact]
    public void RoomsNeedingInferenceSkipsServerTerrainAndFreshKeys()
    {
        var tracker = new RoomMapTracker();
        tracker.Observe(At("1"));
        tracker.Observe(At("2", environment: "forest"), "north");
        tracker.Observe(At("3"), "north");
        var needing = tracker.RoomsNeedingInference("0.1.1");
        Assert.Equal(["s:3", "s:1"], needing.Select(r => r.Id)); // current room first
        var key = RoomTextPreprocessor.InferenceKey("Room", "Tall pines crowd the trail.", "0.1.1");
        Assert.True(tracker.ApplyInference("s:3", key, "0.1.1", new("forest", 0.91, "0.1.1")));
        Assert.Equal(["s:1"], tracker.RoomsNeedingInference("0.1.1").Select(r => r.Id));
        Assert.Equal(2, tracker.RoomsNeedingInference("0.2.0").Count); // new model version invalidates
    }

    [Fact]
    public void ApplyInferenceSetsFieldsWithoutManualFlagAndRejectsStaleKeys()
    {
        var tracker = new RoomMapTracker();
        tracker.Observe(At("1"));
        var changed = 0; tracker.Changed += () => changed++;
        var key = RoomTextPreprocessor.InferenceKey("Room", "Tall pines crowd the trail.", "0.1.1");
        Assert.True(tracker.ApplyInference("s:1", key, "0.1.1", new("forest", 0.91, "0.1.1")));
        var room = tracker.Snapshot.Rooms.Single();
        Assert.Equal("forest", room.InferredEnvironment); Assert.Equal(0.91, room.InferredConfidence); Assert.Equal(key, room.InferredKey);
        Assert.False(room.IsManuallyEdited); Assert.Null(room.Environment); Assert.Equal(1, changed);
        Assert.False(tracker.ApplyInference("s:1", "stale", "0.1.1", new("cave", 0.95, "0.1.1")));
        Assert.False(tracker.ApplyInference("missing", key, "0.1.1", new("cave", 0.95, "0.1.1")));
        Assert.Equal("forest", tracker.Snapshot.Rooms.Single().InferredEnvironment);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void AbstentionRecordsKeyAndClearsEnvironment()
    {
        var tracker = new RoomMapTracker();
        tracker.Observe(At("1"));
        var key = RoomTextPreprocessor.InferenceKey("Room", "Tall pines crowd the trail.", "0.1.1");
        tracker.ApplyInference("s:1", key, "0.1.1", new("forest", 0.91, "0.1.1"));
        Assert.True(tracker.ApplyInference("s:1", key, "0.1.1", null));
        var room = tracker.Snapshot.Rooms.Single();
        Assert.Null(room.InferredEnvironment); Assert.Null(room.InferredConfidence); Assert.Equal(key, room.InferredKey);
        Assert.Empty(tracker.RoomsNeedingInference("0.1.1"));
    }

    [Fact]
    public void ReobservationWithNewTextKeepsHintButNeedsInferenceAgain()
    {
        var tracker = new RoomMapTracker();
        tracker.Observe(At("1"));
        var key = RoomTextPreprocessor.InferenceKey("Room", "Tall pines crowd the trail.", "0.1.1");
        tracker.ApplyInference("s:1", key, "0.1.1", new("forest", 0.91, "0.1.1"));
        tracker.Observe(At("1", description: "Endless dunes roll away."));
        var room = tracker.Snapshot.Rooms.Single();
        Assert.Equal("forest", room.InferredEnvironment);
        Assert.Equal(["s:1"], tracker.RoomsNeedingInference("0.1.1").Select(r => r.Id));
    }

    [Fact]
    public void InferredFieldsSurviveSerializationAndAreValidated()
    {
        var room = new MapRoom("r", "Room", "Desc", null, 0, 0, 0, false) { InferredEnvironment = "cave", InferredConfidence = 0.5, InferredKey = "k" };
        var snapshot = new MapSnapshot([room], [], [], null, MapTrackingState.Unknown, RoomDataSource.Gmcp, 0);
        var back = MapFileFormat.Deserialize(MapFileFormat.Serialize(snapshot));
        var loaded = back.Rooms.Single();
        // KnownExits round-trips as List<string> rather than the source string[]; record equality
        // is reference/type-sensitive for that field, so it is normalized before comparing everything else.
        Assert.Equal(room with { KnownExits = loaded.KnownExits }, loaded);
        Assert.Throws<FormatException>(() => MapFileFormat.Serialize(snapshot with { Rooms = [room with { InferredConfidence = 1.5 }] }));
        Assert.Throws<FormatException>(() => MapFileFormat.Serialize(snapshot with { Rooms = [room with { InferredKey = new string('k', 129) }] }));
        Assert.Throws<FormatException>(() => MapFileFormat.Serialize(snapshot with { Rooms = [room with { InferredEnvironment = new string('e', 129) }] }));
    }
}
