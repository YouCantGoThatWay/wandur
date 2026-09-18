using System.Text;
using System.Text.Json;

namespace Wandur.Core.Mapping;

/// <summary>Versioned, bounded JSON interchange. Legacy raw MapSnapshot caches are accepted.</summary>
public static class MapFileFormat
{
    public const int MaximumBytes = 16_777_216;
    private sealed record MapDocument(int Version, MapSnapshot Map);
    private static readonly JsonSerializerOptions Options = new() { MaxDepth = 32 };

    public static string Serialize(MapSnapshot map)
    {
        Validate(map);
        var json = JsonSerializer.Serialize(new MapDocument(1, map), Options);
        if (Encoding.UTF8.GetByteCount(json) > MaximumBytes) throw new FormatException("Map file exceeds the size limit.");
        return json;
    }

    public static MapSnapshot Deserialize(string json)
    {
        if (json is null || json.Length > MaximumBytes || Encoding.UTF8.GetByteCount(json) > MaximumBytes)
            throw new FormatException("Map file exceeds the size limit.");
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException("Expected a map object.");
            MapSnapshot? map;
            if (root.TryGetProperty("Version", out var version))
            {
                if (!version.TryGetInt32(out var value) || value != 1 || !root.TryGetProperty("Map", out var data))
                    throw new FormatException("Unsupported map version.");
                map = data.Deserialize<MapSnapshot>(Options);
            }
            else map = root.Deserialize<MapSnapshot>(Options);
            if (map is null) throw new FormatException("Missing map data.");
            Validate(map);
            return map;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        { throw new FormatException("Invalid map file.", ex); }
    }

    internal static bool ValidRoom(MapRoom? r) => r is not null && Text(r.Id, 256, required: true) && Text(r.Name, 512, required: true) &&
        Text(r.ObservedName, 512, optional: true) && Text(r.ObservedDescription, 16000, optional: true, multiline: true) && Text(r.ObservedArea, 512, optional: true) &&
        Text(r.Description, 16000, multiline: true) && Text(r.Area, 512, optional: true) && Coordinate(r.X) && Coordinate(r.Y) && Coordinate(r.Z) &&
        Text(r.ServerId, 128, optional: true) && Text(r.Environment, 128, optional: true) && Text(r.Symbol, 32, optional: true) &&
        Text(r.Notes, 16000, multiline: true) && double.IsFinite(r.Weight) && r.Weight > 0 && r.Weight <= 1e9 && r.Revision >= 0 &&
        (r.Color is null || (r.Color.Length == 7 && r.Color[0] == '#' && r.Color.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0)) &&
        Text(r.InferredEnvironment, 128, optional: true) && Text(r.InferredKey, 128, optional: true) &&
        (r.InferredConfidence is null || (double.IsFinite(r.InferredConfidence.Value) && r.InferredConfidence is >= 0 and <= 1)) &&
        r.KnownExits is { Count: <= 64 } && r.KnownExits.All(e => Text(e, 64, required: true));

    internal static bool ValidLink(MapLink? l) => l is not null && Text(l.FromId, 256, required: true) && Text(l.ToId, 256, required: true) &&
        Text(l.Direction, 64, required: true) && Text(l.Command, 512, optional: true) && double.IsFinite(l.Weight) && l.Weight >= 0 && l.Weight <= 1e9 &&
        Enum.IsDefined(l.DoorState) && l.Revision >= 0 && l.LinePoints is { Count: <= 128 } && l.LinePoints.All(p => p is not null && Coordinate(p.X) && Coordinate(p.Y));

    internal static bool Coordinate(double value) => double.IsFinite(value) && Math.Abs(value) <= 1e9;
    internal static bool Text(string? value, int maximum, bool required = false, bool optional = false, bool multiline = false) =>
        value is null ? optional : value.Length <= maximum && (!required || !string.IsNullOrWhiteSpace(value)) &&
        !value.Any(c => char.IsControl(c) && !(multiline && c is '\r' or '\n' or '\t'));

    private static void Validate(MapSnapshot map)
    {
        if (map.Rooms is not { Count: <= 10000 } || map.Links is not { Count: <= 60000 } || map.AreaSettings is not { Count: <= 10000 } ||
            map.RoomAliases is not { Count: <= 100000 } || map.DeletedRooms is not { Count: <= 100000 } || map.DeletedLinks is not { Count: <= 100000 } ||
            map.CandidateRoomIds is not { Count: <= 256 } || map.ObservationCount < 0 || !Enum.IsDefined(map.State) || !Enum.IsDefined(map.Source) ||
            map.Rooms.Any(r => !ValidRoom(r)) || map.Links.Any(l => !ValidLink(l))) throw new FormatException("Invalid map data or limits exceeded.");
        var ids = map.Rooms.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        if (ids.Count != map.Rooms.Count || map.Links.Select(l => (l.FromId, l.Direction)).Distinct().Count() != map.Links.Count ||
            map.Links.Any(l => !ids.Contains(l.FromId)) || map.CandidateRoomIds.Any(id => id is null || !ids.Contains(id)) ||
            (map.CurrentRoomId is not null && !ids.Contains(map.CurrentRoomId)) ||
            map.RoomAliases.Any(a => a is null || !Text(a.SourceId, 256, required: true) || !Text(a.TargetId, 256, required: true) || a.Revision < 0) ||
            map.RoomAliases.Select(a => a.SourceId).Distinct().Count() != map.RoomAliases.Count ||
            map.AreaSettings.Any(a => a is null || !Text(a.Area, 512) || a.Revision < 0) ||
            map.AreaSettings.Select(a => a.Area).Distinct().Count() != map.AreaSettings.Count ||
            map.DeletedRooms.Any(d => d is null || !Text(d.Id, 256, required: true) || d.Revision <= 0) ||
            map.DeletedLinks.Any(d => d is null || !Text(d.FromId, 256, required: true) || !Text(d.Direction, 64, required: true) || d.Revision <= 0))
            throw new FormatException("Invalid map references or metadata.");
    }
}
