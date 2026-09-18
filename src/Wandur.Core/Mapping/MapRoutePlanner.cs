namespace Wandur.Core.Mapping;

public sealed record MapRoute(IReadOnlyList<MapLink> Steps, double Cost);

public static class MapRoutePlanner
{
    /// <summary>Directed shortest path. Inferred links/provisional rooms require explicit opt-in.
    /// Closed doors are excluded because opening them requires game-specific commands.</summary>
    public static MapRoute? FindRoute(MapSnapshot map, string fromId, string toId, bool allowInferred = false)
    {
        var rooms = map.Rooms.ToDictionary(r => r.Id);
        if (!rooms.TryGetValue(fromId, out var start) || !rooms.TryGetValue(toId, out var end) ||
            start.IsLocked || end.IsLocked || (!allowInferred && (start.Provisional || end.Provisional))) return null;
        var exits = map.Links.ToLookup(l => l.FromId);
        var distances = new Dictionary<string, double> { [fromId] = 0 };
        var previous = new Dictionary<string, MapLink>();
        var queue = new PriorityQueue<string, double>();
        queue.Enqueue(fromId, 0);
        while (queue.TryDequeue(out var id, out var cost))
        {
            if (cost > distances[id]) continue;
            if (id == toId)
            {
                var steps = new List<MapLink>();
                while (id != fromId) { var link = previous[id]; steps.Add(link); id = link.FromId; }
                steps.Reverse();
                return new(steps, cost);
            }
            foreach (var link in exits[id])
            {
                if (link.IsLocked || link.DoorState is MapDoorState.Closed or MapDoorState.Locked ||
                    (!allowInferred && !link.Confirmed) || !rooms.TryGetValue(link.ToId, out var room) ||
                    room.IsLocked || (!allowInferred && room.Provisional)) continue;
                var weight = link.Weight == 0 ? room.Weight : link.Weight;
                if (!double.IsFinite(weight) || weight <= 0) continue;
                var next = cost + weight;
                if (!double.IsFinite(next) || (distances.TryGetValue(room.Id, out var known) && next >= known)) continue;
                distances[room.Id] = next;
                previous[room.Id] = link;
                queue.Enqueue(room.Id, next);
            }
        }
        return null;
    }
}
