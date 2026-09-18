using System.Text.RegularExpressions;

namespace Wandur.Core.Mapping;

/// <summary>Tracks directed room topology independently of its illustrative coordinates.</summary>
public sealed partial class RoomMapTracker
{
    private readonly Dictionary<string, MapRoom> _rooms = [];
    private readonly List<MapLink> _links = [];
    private readonly Dictionary<string, MapRoomAlias> _aliases = [];
    private readonly Dictionary<string, MapAreaSettings> _areas = [];
    private readonly Dictionary<string, long> _deletedRooms = [];
    private readonly Dictionary<(string From, string Direction), long> _deletedLinks = [];
    private readonly List<(MapSnapshot Before, MapSnapshot After)> _undo = [], _redo = [];
    private long _revision;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    private long NextRevision() => _revision = Math.Max(DateTime.UtcNow.Ticks, _revision + 1);
    private List<string[]> _paths = [];
    private string? _current;
    private (string FromId, string ToId, string Direction)? _lastTraversal;
    private string? _pendingOrigin;
    private string? _pendingDirection;
    private string? _firstFingerprint;
    private bool _distinctObservation;
    private int _steps;
    private int _observations;
    private RoomDataSource _source;
    private MapTrackingState _state = MapTrackingState.Waiting;
    public event Action? Changed;
    public MapSnapshot Snapshot => new(_rooms.Values.ToArray(), _links.ToArray(),
        _paths.Select(p => p[^1]).Distinct().ToArray(), _current, _state, _source, _observations)
    {
        AreaSettings = _areas.Values.ToArray(), RoomAliases = _aliases.Values.ToArray(),
        DeletedRooms = _deletedRooms.Select(d => new MapRoomDeletion(d.Key, d.Value)).ToArray(),
        DeletedLinks = _deletedLinks.Select(d => new MapLinkDeletion(d.Key.From, d.Key.Direction, d.Value)).ToArray()
    };

    public RoomMapTracker(MapSnapshot? saved = null)
    {
        if (saved is null) return;
        foreach (var alias in saved.RoomAliases) _aliases[alias.SourceId] = alias;
        foreach (var area in saved.AreaSettings) _areas[area.Area] = area;
        foreach (var deleted in saved.DeletedRooms) _deletedRooms[deleted.Id] = deleted.Revision;
        foreach (var deleted in saved.DeletedLinks) _deletedLinks[(deleted.FromId, deleted.Direction)] = deleted.Revision;
        _revision = saved.Rooms.Select(r => r.Revision).Concat(saved.Links.Select(l => l.Revision))
            .Concat(saved.RoomAliases.Select(a => a.Revision)).Concat(saved.AreaSettings.Select(a => a.Revision)).Concat(_deletedRooms.Values).Concat(_deletedLinks.Values).DefaultIfEmpty().Max();
        foreach (var room in saved.Rooms.Take(10000)) _rooms.TryAdd(room.Id, room);
        _links.AddRange(saved.Links.Take(60000).Where(l => _rooms.ContainsKey(l.FromId)));
        _state = _rooms.Count == 0 ? MapTrackingState.Waiting : MapTrackingState.Unknown;
    }

    public void LosePosition()
    {
        _lastTraversal = null;
        _current = null;
        ClearCandidates();
        _state = _rooms.Count == 0 ? MapTrackingState.Waiting : MapTrackingState.Unknown;
        Changed?.Invoke();
    }

    public void MovementFailed() => Changed?.Invoke();

    /// <summary>Replace a just-created text guess with metadata from the same command response.</summary>
    public bool RefineProvisionalRoom(string id, int observationCount, RoomObservation observation)
    {
        if (_current != id || _observations != observationCount || observation.Source == RoomDataSource.Text ||
            observation.ServerId is not null || !_rooms.TryGetValue(id, out var room) ||
            !room.Provisional || room.IsManuallyEdited || room.ServerId is not null || !ValidObservation(observation)) return false;
        if (_rooms.Values.Any(candidate => candidate.Id != id && Matches(candidate, observation)))
        {
            // Relocalize against the saved graph instead of renaming the guess into a duplicate.
            // Its short-lived incoming edge is withdrawn with it; room tombstones also prevent
            // a previously saved copy of that guess (and links targeting it) from returning.
            var traversal = _lastTraversal is { } previous && previous.ToId == id ? previous : ((string FromId, string ToId, string Direction)?)null;
            _rooms.Remove(id);
            _links.RemoveAll(link => link.FromId == id || link.ToId == id);
            _deletedRooms[id] = NextRevision();
            ClearCandidates();
            _current = traversal?.FromId;
            Observe(observation, traversal?.Direction);
            return true;
        }
        // Observe normally after removing the guessed title/description as recognition evidence.
        // Coordinates and witnessed movement remain attached to this same provisional room.
        _rooms[id] = room with { Name = observation.Name, Description = observation.Description,
            Area = null, KnownExits = [] };
        Observe(observation);
        return true;
    }

    private static bool ValidObservation(RoomObservation observation) =>
        MapFileFormat.Text(observation.Name, 512, required: true) &&
        MapFileFormat.Text(observation.Description, 16000, multiline: true) && observation.Exits is not null;

    public void Observe(RoomObservation observation, string? movement = null)
    {
        if (!ValidObservation(observation)) return;
        observation = observation with
        {
            ServerId = MapFileFormat.Text(observation.ServerId, 128, optional: true) ? observation.ServerId : null,
            Area = MapFileFormat.Text(observation.Area, 512, optional: true) ? observation.Area : null,
            Environment = MapFileFormat.Text(observation.Environment, 128, optional: true) ? observation.Environment : null,
            Symbol = MapFileFormat.Text(observation.Symbol, 32, optional: true) ? observation.Symbol : null,
            Exits = observation.Exits.Where(e => MapFileFormat.Text(e.Key, 64, required: true)).Take(64)
                .ToDictionary(e => e.Key, e => MapFileFormat.Text(e.Value, 128, optional: true) ? e.Value : null)
        };
        if (observation.ServerId is not null && _deletedRooms.ContainsKey(ResolveAlias("s:" + observation.ServerId)) && !_rooms.ContainsKey(ResolveAlias("s:" + observation.ServerId))) { LosePosition(); return; }
        var direction = NormalizeDirection(movement);
        _observations++;
        _source = observation.Source;
        if (!string.IsNullOrWhiteSpace(observation.ServerId) && observation.ServerId is not "-1" and not "0")
        {
            ObserveIdentified(observation, direction);
            Changed?.Invoke();
            return;
        }

        if (_paths.Count > 0)
        {
            // Advance every plausible path, not merely the first matching room.
            var next = direction is null
                ? _paths.Where(p => Matches(_rooms[p[^1]], observation)).ToList()
                : _paths.SelectMany(p => _links.Where(l => l.FromId == p[^1] && l.Direction == direction && _rooms.ContainsKey(l.ToId))
                    .Where(l => Matches(_rooms[l.ToId], observation)).Select(l => p.Append(l.ToId).ToArray())).Take(257).ToList();
            if (next.Count > 256) { LosePosition(); return; }
            if (next.Count > 0)
            {
                _paths = next;
                if (direction is not null) _steps++;
                _distinctObservation |= Fingerprint(observation) != _firstFingerprint;
                ResolveCandidates();
                Changed?.Invoke();
                return;
            }
            // Contradictory evidence invalidates the old hypotheses. No guessed edge survives.
            ClearCandidates();
            _current = null;
        }

        if (_current is not null && direction is null && Matches(_rooms[_current], observation))
        {
            UpdateRoom(_current, observation);
            Changed?.Invoke();
            return;
        }
        if (_current is not null && direction is not null)
        {
            var link = _links.FirstOrDefault(l => l.FromId == _current && l.Direction == direction);
            if (link is not null && _rooms.TryGetValue(link.ToId, out var target) && Matches(target, observation))
            {
                _lastTraversal = (_current, target.Id, direction);
                _current = target.Id;
                UpdateRoom(_current, observation);
                _state = MapTrackingState.Inferred;
                Changed?.Invoke();
                return;
            }
        }

        var origin = direction is not null ? _current : null;
        var matches = _rooms.Values.Where(r => r.Id != origin && Matches(r, observation)).Take(257).ToArray();
        if (matches.Length > 256) { LosePosition(); return; }
        // A witnessed out-and-back between distinctive rooms is itself a sequence.
        // Do not manufacture a reverse exit merely because the outward exit exists.
        if (matches.Length == 1 && origin is not null && direction is not null &&
            _lastTraversal is { } traversal && traversal.ToId == origin && traversal.FromId == matches[0].Id &&
            Opposite(traversal.Direction) == direction && !Matches(_rooms[origin], observation))
        {
            _current = matches[0].Id;
            AddLink(origin, _current, direction, false);
            _lastTraversal = (origin, _current, direction);
            UpdateRoom(_current, observation);
            _state = MapTrackingState.Inferred;
            Changed?.Invoke();
            return;
        }
        _lastTraversal = null;
        if (matches.Length > 0)
        {
            _paths = matches.Select(r => new[] { r.Id }).ToList();
            _pendingOrigin = origin;
            _pendingDirection = direction;
            _firstFingerprint = Fingerprint(observation);
            _steps = 0;
            _distinctObservation = false;
            _current = null;
            ResolveCandidates();
        }
        else
        {
            if (_rooms.Count >= 10000) { LosePosition(); return; }
            var id = "t:" + Guid.NewGuid().ToString("N");
            var (x, y, z) = Position(origin, direction);
            _rooms[id] = new(id, observation.Name, observation.Description, observation.Area, x, y, z, true) { KnownExits = observation.Exits.Keys.Select(e => NormalizeDirection(e) ?? e).ToArray() };
            UpdateRoom(id, observation);
            if (origin is not null && direction is not null)
            {
                AddLink(origin, id, direction, false);
                _lastTraversal = (origin, id, direction);
            }
            _current = id;
            _state = MapTrackingState.Inferred;
        }
        Changed?.Invoke();
    }

    private void ResolveCandidates()
    {
        var locations = _paths.Select(p => p[^1]).Distinct().ToArray();
        // A new connection needs a distinctive sequence; pure relocalization can resolve sooner.
        if (locations.Length == 1 && (_pendingOrigin is null || (_steps >= 2 && _distinctObservation)))
        {
            if (_pendingOrigin is not null && _pendingDirection is not null && _paths.Select(p => p[0]).Distinct().Count() == 1)
                AddLink(_pendingOrigin, _paths[0][0], _pendingDirection, false);
            _current = locations[0];
            ClearCandidates();
            _state = MapTrackingState.Inferred;
        }
        else _state = MapTrackingState.Ambiguous;
    }

    private void ObserveIdentified(RoomObservation observation, string? direction)
    {
        var id = ResolveAlias("s:" + observation.ServerId);
        ReconcileProvisionalIdentity(id, observation, direction);
        var previous = _current;
        if (!_rooms.ContainsKey(id))
        {
            if (_rooms.Count >= 10000) { LosePosition(); return; }
            var incoming = _links.FirstOrDefault(l => l.ToId == id && _rooms.ContainsKey(l.FromId));
            // An unsolicited room change is authoritative location evidence, not
            // evidence of an adjacent exit from the previous room.
            var (x, y, z) = Position(incoming?.FromId ?? (direction is not null ? previous : null), incoming?.Direction ?? direction);
            _rooms[id] = new(id, observation.Name, observation.Description, observation.Area, x, y, z, false, observation.ServerId);
        }
        UpdateRoom(id, observation);
        if (previous is not null && direction is not null && previous != id) AddLink(previous, id, direction, true);
        foreach (var exit in observation.Exits.Take(32))
        {
            var name = NormalizeDirection(exit.Key) ?? exit.Key.Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(exit.Value) && exit.Value is not "-1" and not "0")
                AddLink(id, ResolveAlias("s:" + exit.Value), name, true);
        }
        _current = id;
        ClearCandidates();
        _state = MapTrackingState.Confirmed;
    }

    private void ReconcileProvisionalIdentity(string id, RoomObservation observation, string? direction)
    {
        // A newly supported room ID must not leave a second, disconnected copy of
        // an old text room. Require a unique compatible name/area/description AND
        // the complete same nonempty exit set. Ambiguous corridors stay separate.
        var exits = observation.Exits.Keys.Select(e => NormalizeDirection(e) ?? e).ToHashSet();
        if (exits.Count == 0) return;
        var matches = _rooms.Values.Where(r => r.Id != id && Matches(r, observation) &&
            exits.SetEquals(r.KnownExits)).Take(2).ToArray();
        if (matches.Length != 1 || !matches[0].Provisional || matches[0].ServerId is not null) return;
        var legacy = matches[0];
        if (direction is not null && legacy.Id == _current) return;
        // Preserve user labels, coordinates, locks and notes. Protocol identity is
        // evidence, not a manual edit; upgrading it must not enter the undo stack.
        _rooms.TryGetValue(id, out var identified);
        if (identified?.IsManuallyEdited == true) return;
        _rooms[id] = legacy with { Id = id, ServerId = observation.ServerId, Provisional = false,
            Revision = NextRevision() };
        MergeRooms(legacy.Id, id, manual: false);
    }

    private void UpdateRoom(string id, RoomObservation observation)
    {
        var old = _rooms[id];
        if (old.IsManuallyEdited)
        {
            _rooms[id] = old with { ObservedName = observation.Name,
                ObservedDescription = string.IsNullOrWhiteSpace(observation.Description) ? old.ObservedDescription : observation.Description,
                ObservedArea = observation.Area ?? old.ObservedArea,
                KnownExits = observation.Exits.Keys.Select(e => NormalizeDirection(e) ?? e).ToArray() };
            return;
        }
        _rooms[id] = old with { Name = observation.Name, Description = string.IsNullOrWhiteSpace(observation.Description) ? old.Description : observation.Description,
            Area = observation.Area ?? old.Area, Environment = observation.Environment ?? old.Environment, Symbol = observation.Symbol ?? old.Symbol,
            X = observation.X is { } x && MapFileFormat.Coordinate(x) ? x : old.X,
            Y = observation.Y is { } y && MapFileFormat.Coordinate(y) ? y : old.Y,
            Z = observation.Z is { } z && MapFileFormat.Coordinate(z) ? z : old.Z,
            KnownExits = observation.Exits.Keys.Select(e => NormalizeDirection(e) ?? e).ToArray(), Provisional = observation.ServerId is null && old.Provisional };
    }

    private void AddLink(string from, string to, string direction, bool confirmed)
    {
        var existing = _links.FindIndex(l => l.FromId == from && l.Direction == direction);
        if ((_deletedLinks.TryGetValue((from, direction), out var deleted) && (existing < 0 || _links[existing].Revision <= deleted)) ||
            (_deletedRooms.ContainsKey(to) && !_rooms.ContainsKey(to))) return;
        if (existing >= 0 && _links[existing].IsManuallyEdited)
        {
            // Observing this exact edge can strengthen its evidence without
            // replacing a user's destination, command, locks or drawing settings.
            var edited = _links[existing];
            if (confirmed && !edited.Confirmed && edited.ToId == to)
                _links[existing] = edited with { Confirmed = true, Revision = NextRevision() };
            return;
        }
        var value = new MapLink(from, to, direction, confirmed) { Revision = existing >= 0 ? _links[existing].Revision : 0 };
        if (existing >= 0) _links[existing] = value;
        else if (_links.Count < 60000) _links.Add(value);
    }

    private static string Normalize(string value) => Regex.Replace(value.Trim().ToLowerInvariant(), @"\s+", " ", RegexOptions.None, TimeSpan.FromMilliseconds(50));
    // LOTJ appends room flags to text titles but omits them from Room.Info.
    // Keep arbitrary bracketed names intact: they may distinguish rooms or instances.
    private static string NormalizeTitle(string value) => Normalize(Regex.Replace(value,
        @"(?:\s*\[(?:hotel|hospital|engine)\])+\s*$", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(50)));
    private static string Fingerprint(RoomObservation room) => NormalizeTitle(room.Name) + "|" + Normalize(room.Description);
    private bool Matches(MapRoom room, RoomObservation observation)
    {
        var name = room.ObservedName ?? room.Name;
        var description = room.ObservedDescription ?? room.Description;
        var area = room.ObservedName is not null ? room.ObservedArea : room.Area;
        if (NormalizeTitle(name) != NormalizeTitle(observation.Name)) return false;
        var exits = observation.Exits.Keys.Select(e => NormalizeDirection(e) ?? e).ToHashSet();
        // Missing exits may be hidden/closed; wholly contradictory nonempty exit sets weaken identity enough to reject it.
        if (room.KnownExits.Count > 0 && exits.Count > 0 && !room.KnownExits.Any(exits.Contains)) return false;
        if (!string.IsNullOrWhiteSpace(area) && !string.IsNullOrWhiteSpace(observation.Area) && Normalize(area) != Normalize(observation.Area)) return false;
        if (string.IsNullOrWhiteSpace(description) || string.IsNullOrWhiteSpace(observation.Description)) return true;
        var first = Normalize(description);
        var second = Normalize(observation.Description);
        if (first == second) return true;
        // Small wording changes are evidence, not a new identity. Geography still constrains candidates.
        var a = first.Split(' ').Where(w => w.Length > 2).ToHashSet();
        var b = second.Split(' ').Where(w => w.Length > 2).ToHashSet();
        return a.Count > 0 && (double)a.Intersect(b).Count() / a.Union(b).Count() >= 0.8;
    }

    private (double X, double Y, double Z) Position(string? origin, string? direction)
    {
        if (origin is null || !_rooms.TryGetValue(origin, out var room)) return (_rooms.Count * 2, 0, 0);
        var offset = direction switch
        {
            "north" => (0, 1, 0), "south" => (0, -1, 0), "east" => (1, 0, 0), "west" => (-1, 0, 0),
            "northeast" => (1, 1, 0), "northwest" => (-1, 1, 0), "southeast" => (1, -1, 0), "southwest" => (-1, -1, 0),
            "up" => (0, 0, 1), "down" => (0, 0, -1), _ => (1, 0, 0)
        };
        var position = (X: room.X + offset.Item1, Y: room.Y + offset.Item2, Z: room.Z + offset.Item3);
        // Display overlap is not proof that two rooms are identical.
        while (_rooms.Values.Any(r => r.X == position.X && r.Y == position.Y && r.Z == position.Z)) position.X += 0.35;
        return position;
    }

    public static string? NormalizeDirection(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "n" or "north" => "north", "s" or "south" => "south", "e" or "east" => "east", "w" or "west" => "west",
        "ne" or "northeast" => "northeast", "nw" or "northwest" => "northwest", "se" or "southeast" => "southeast", "sw" or "southwest" => "southwest",
        "u" or "up" => "up", "d" or "down" => "down", "in" => "in", "out" => "out", _ => null
    };

    private static string? Opposite(string direction) => direction switch
    {
        "north" => "south", "south" => "north", "east" => "west", "west" => "east",
        "northeast" => "southwest", "southwest" => "northeast", "northwest" => "southeast", "southeast" => "northwest",
        "up" => "down", "down" => "up", _ => null
    };

    private void ClearCandidates()
    {
        _lastTraversal = null;
        _paths.Clear();
        _pendingOrigin = _pendingDirection = _firstFingerprint = null;
        _steps = 0;
        _distinctObservation = false;
    }
}
