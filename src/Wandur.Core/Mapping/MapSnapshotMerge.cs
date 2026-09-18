namespace Wandur.Core.Mapping;

/// <summary>Revision-aware graph merge shared by legacy and SQLite stores.</summary>
internal static class MapSnapshotMerge
{
    public static MapSnapshot Combine(MapSnapshot previous, MapSnapshot snapshot)
    {
        var deletedRooms = previous.DeletedRooms.Concat(snapshot.DeletedRooms).GroupBy(d => d.Id)
            .Select(g => g.MaxBy(d => d.Revision)!).ToArray();
        var deletedLinks = previous.DeletedLinks.Concat(snapshot.DeletedLinks).GroupBy(d => (d.FromId, d.Direction))
            .Select(g => g.MaxBy(d => d.Revision)!).ToArray();
        var roomDeletes = deletedRooms.ToDictionary(d => d.Id, d => d.Revision);
        var linkDeletes = deletedLinks.ToDictionary(d => (d.FromId, d.Direction), d => d.Revision);
        var rooms = previous.Rooms.Concat(snapshot.Rooms).GroupBy(r => r.Id)
            .Select(g => g.OrderBy(r => r.Revision).ThenBy(r => r.IsManuallyEdited).Last())
            .Where(r => !roomDeletes.TryGetValue(r.Id, out var deleted) || r.Revision > deleted).TakeLast(10000).ToArray();
        var ids = rooms.Select(r => r.Id).ToHashSet();
        var links = previous.Links.Concat(snapshot.Links).GroupBy(l => (l.FromId, l.Direction))
            .Select(g => g.OrderBy(l => l.Revision).ThenBy(l => l.IsManuallyEdited).Last())
            .Where(l => ids.Contains(l.FromId) && (!roomDeletes.ContainsKey(l.ToId) || ids.Contains(l.ToId)) &&
                (!linkDeletes.TryGetValue((l.FromId, l.Direction), out var deleted) || l.Revision > deleted)).TakeLast(60000).ToArray();
        var areas = previous.AreaSettings.Concat(snapshot.AreaSettings).GroupBy(a => a.Area)
            .Select(g => g.OrderBy(a => a.Revision).Last()).ToArray();
        var aliases = previous.RoomAliases.Concat(snapshot.RoomAliases).GroupBy(a => a.SourceId)
            .Select(g => g.OrderBy(a => a.Revision).Last()).ToArray();
        return snapshot with { RoomAliases = aliases, Rooms = rooms, Links = links, AreaSettings = areas, DeletedRooms = deletedRooms, DeletedLinks = deletedLinks };
    }
}
