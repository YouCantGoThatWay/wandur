using Wandur.Core.Classification;

namespace Wandur.Core.Mapping;

public sealed partial class RoomMapTracker
{
    /// <summary>Rooms without server/manual terrain whose inference is missing or stale; current room first.</summary>
    public IReadOnlyList<MapRoom> RoomsNeedingInference(string modelVersion) =>
        _rooms.Values
            .Where(r => string.IsNullOrWhiteSpace(r.Environment) &&
                        r.InferredKey != RoomTextPreprocessor.InferenceKey(r.Name, r.Description, modelVersion))
            .OrderByDescending(r => r.Id == _current)
            .ToArray();

    /// <summary>Applies a classifier result; ignored when the room is gone or <paramref name="key"/> no longer matches its text.</summary>
    public bool ApplyInference(string id, string key, string modelVersion, RoomEnvironmentPrediction? prediction)
    {
        if (!_rooms.TryGetValue(id, out var room)) return false;
        if (key != RoomTextPreprocessor.InferenceKey(room.Name, room.Description, modelVersion)) return false;
        _rooms[id] = room with
        {
            InferredEnvironment = prediction?.Environment,
            InferredConfidence = prediction?.Confidence,
            InferredKey = key,
            Revision = NextRevision()
        };
        Changed?.Invoke();
        return true;
    }
}
