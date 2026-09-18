namespace Wandur.Core.Mapping;

public enum RoomDataSource { Text, Gmcp, Msdp }
public enum MapTrackingState { Waiting, Confirmed, Inferred, Ambiguous, Unknown }
public enum MapDoorState { None, Open, Closed, Locked }

public sealed record RoomObservation(
    string? ServerId, string Name, string Description,
    IReadOnlyDictionary<string, string?> Exits, string? Area = null,
    RoomDataSource Source = RoomDataSource.Text)
{
    public bool ExitsProvided { get; init; }
    public string? Environment { get; init; }
    public double? X { get; init; }
    public double? Y { get; init; }
    public double? Z { get; init; }
    public string? Symbol { get; init; }
}

public sealed record MapRoom(string Id, string Name, string Description, string? Area,
    double X, double Y, double Z, bool Provisional, string? ServerId = null)
{
    // Recognition evidence is separate from user-visible labels.
    public string? ObservedName { get; init; }
    public string? ObservedDescription { get; init; }
    public string? ObservedArea { get; init; }
    public IReadOnlyList<string> KnownExits { get; init; } = [];
    public string? Environment { get; init; }
    public string? Color { get; init; }
    public string? Symbol { get; init; }
    // Classifier hint; presentation only. Environment (server/manual) always wins.
    public string? InferredEnvironment { get; init; }
    public double? InferredConfidence { get; init; }
    public string? InferredKey { get; init; }
    public string Notes { get; init; } = "";
    public double Weight { get; init; } = 1;
    public bool IsLocked { get; init; }
    public bool IsManuallyEdited { get; init; }
    public long Revision { get; init; }
}
public sealed record MapLinePoint(double X, double Y);
public sealed record MapLink(string FromId, string ToId, string Direction, bool Confirmed)
{
    /// <summary>Null uses Direction as the movement command.</summary>
    public string? Command { get; init; }
    /// <summary>Zero uses the destination room's positive traversal cost.</summary>
    public double Weight { get; init; }
    public bool IsLocked { get; init; }
    public bool IsManuallyEdited { get; init; }
    public MapDoorState DoorState { get; init; }
    public IReadOnlyList<MapLinePoint> LinePoints { get; init; } = [];
    public long Revision { get; init; }
}
public sealed record MapAreaSettings(string Area, bool GridMode = false)
{
    public long Revision { get; init; }
}
public sealed record MapRoomAlias(string SourceId, string TargetId, long Revision);
public sealed record MapRoomDeletion(string Id, long Revision);
public sealed record MapLinkDeletion(string FromId, string Direction, long Revision);
public sealed record MapSnapshot(IReadOnlyList<MapRoom> Rooms, IReadOnlyList<MapLink> Links,
    IReadOnlyList<string> CandidateRoomIds, string? CurrentRoomId, MapTrackingState State,
    RoomDataSource Source, int ObservationCount)
{
    public IReadOnlyList<MapRoomAlias> RoomAliases { get; init; } = [];
    public IReadOnlyList<MapAreaSettings> AreaSettings { get; init; } = [];
    public IReadOnlyList<MapRoomDeletion> DeletedRooms { get; init; } = [];
    public IReadOnlyList<MapLinkDeletion> DeletedLinks { get; init; } = [];
}
