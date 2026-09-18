namespace Wandur.Core.Mapping;

public sealed partial class RoomMapTracker
{
    /// <summary>Replace graph contents as one undoable manual action. Session position is cleared.</summary>
    public bool ReplaceMap(MapSnapshot map)
    {
        try { MapFileFormat.Serialize(map); }
        catch (FormatException) { return false; }
        var before = Snapshot;
        var incoming = map.Rooms.Select(r => r.Id).ToHashSet();
        foreach (var id in _rooms.Keys.Where(id => !incoming.Contains(id)).ToArray()) DeleteRoom(id);
        var incomingLinks = map.Links.Select(l => (l.FromId, l.Direction)).ToHashSet();
        foreach (var link in _links.Where(l => !incomingLinks.Contains((l.FromId, l.Direction))).ToArray())
            _deletedLinks[(link.FromId, link.Direction)] = NextRevision();
        _rooms.Clear();
        foreach (var room in map.Rooms) _rooms[room.Id] = room with { IsManuallyEdited = true, Revision = NextRevision() };
        _links.Clear();
        foreach (var link in map.Links) _links.Add(link with { IsManuallyEdited = true, Revision = NextRevision() });
        // Reset removed area settings to their default, with a revision newer than stale saves.
        foreach (var area in _areas.Keys.ToArray()) _areas[area] = new(area) { Revision = NextRevision() };
        foreach (var area in map.AreaSettings) _areas[area.Area] = area with { Revision = NextRevision() };
        foreach (var source in _aliases.Keys.ToArray()) _aliases[source] = new(source, source, NextRevision());
        foreach (var alias in map.RoomAliases) _aliases[alias.SourceId] = alias with { Revision = NextRevision() };
        _current = null;
        _state = MapTrackingState.Unknown;
        RecordEdit(before);
        return true;
    }

    public bool UpsertRoom(MapRoom room)
    {
        if (!MapFileFormat.ValidRoom(room) || (!_rooms.ContainsKey(room.Id) && _rooms.Count >= 10000)) return false;
        var before = Snapshot;
        _rooms.TryGetValue(room.Id, out var existing);
        _rooms[room.Id] = room with { IsManuallyEdited = true, Revision = NextRevision(),
            ObservedName = room.ObservedName ?? existing?.ObservedName ?? existing?.Name,
            ObservedDescription = room.ObservedDescription ?? existing?.ObservedDescription ?? existing?.Description,
            ObservedArea = room.ObservedName is not null ? room.ObservedArea : existing?.ObservedName is not null ? existing.ObservedArea : existing?.Area };
        RecordEdit(before);
        return true;
    }

    public bool RemoveRoom(string id)
    {
        if (!_rooms.ContainsKey(id)) return false;
        var before = Snapshot;
        DeleteRoom(id);
        RecordEdit(before);
        return true;
    }

    public bool MergeRooms(string sourceId, string targetId)
        => MergeRooms(sourceId, targetId, manual: true);

    private bool MergeRooms(string sourceId, string targetId, bool manual)
    {
        if (sourceId == targetId || !_rooms.ContainsKey(sourceId) || !_rooms.ContainsKey(targetId)) return false;
        var before = Snapshot;
        var wasCurrent = _current == sourceId;
        var affectedAliases = _aliases.Values.Where(a => a.TargetId == sourceId).ToArray();
        var rewired = _links.Where(l => l.FromId == sourceId || l.ToId == sourceId)
            .Select(l => l with { FromId = l.FromId == sourceId ? targetId : l.FromId, ToId = l.ToId == sourceId ? targetId : l.ToId }).ToArray();
        DeleteRoom(sourceId);
        foreach (var link in rewired)
        {
            // Manual merges keep the chosen target's exits. Automatic identity upgrades
            // must instead retain edited exits over automatically discovered duplicates.
            if (link.FromId == link.ToId) continue;
            var conflict = _links.FindIndex(l => l.FromId == link.FromId && l.Direction == link.Direction);
            if (conflict >= 0)
            {
                var existing = _links[conflict];
                if (!manual && link.IsManuallyEdited && !existing.IsManuallyEdited)
                    _links[conflict] = link with { Confirmed = link.Confirmed || (existing.ToId == link.ToId && existing.Confirmed),
                        Revision = NextRevision() };
                continue;
            }
            _links.Add(link with { IsManuallyEdited = manual || link.IsManuallyEdited, Revision = NextRevision() });
        }
        foreach (var alias in affectedAliases)
            _aliases[alias.SourceId] = alias with { TargetId = targetId, Revision = NextRevision() };
        _aliases[sourceId] = new(sourceId, targetId, NextRevision());
        if (wasCurrent) _current = targetId;
        if (manual) RecordEdit(before);
        return true;
    }

    public bool UpsertLink(MapLink link)
    {
        if (!MapFileFormat.ValidLink(link) || !_rooms.ContainsKey(link.FromId)) return false;
        var index = _links.FindIndex(l => l.FromId == link.FromId && l.Direction == link.Direction);
        if (index < 0 && _links.Count >= 60000) return false;
        var before = Snapshot;
        var edited = link with { IsManuallyEdited = true, Revision = NextRevision() };
        if (index >= 0) _links[index] = edited; else _links.Add(edited);
        RecordEdit(before);
        return true;
    }

    /// <summary>Rename/edit an exit and optionally create its return exit as one undoable action.</summary>
    public bool EditLink(MapLink link, string? previousDirection = null, MapLink? returnLink = null)
    {
        if (!MapFileFormat.ValidLink(link) || !_rooms.ContainsKey(link.FromId) ||
            (returnLink is not null && (!MapFileFormat.ValidLink(returnLink) || !_rooms.ContainsKey(returnLink.FromId)))) return false;
        var proposed = _links.ToDictionary(l => (l.FromId, l.Direction));
        var previousKey = (link.FromId, previousDirection ?? link.Direction);
        proposed.Remove(previousKey);
        proposed[(link.FromId, link.Direction)] = link;
        if (returnLink is not null) proposed[(returnLink.FromId, returnLink.Direction)] = returnLink;
        if (proposed.Count > 60000) return false;
        var before = Snapshot;
        if (previousKey != (link.FromId, link.Direction)) _deletedLinks[previousKey] = NextRevision();
        proposed[(link.FromId, link.Direction)] = link with { IsManuallyEdited = true, Revision = NextRevision() };
        if (returnLink is not null) proposed[(returnLink.FromId, returnLink.Direction)] = returnLink with { IsManuallyEdited = true, Revision = NextRevision() };
        _links.Clear(); _links.AddRange(proposed.Values);
        RecordEdit(before);
        return true;
    }

    public bool RemoveLink(string fromId, string direction)
    {
        direction = NormalizeDirection(direction) ?? direction.Trim().ToLowerInvariant();
        var index = _links.FindIndex(l => l.FromId == fromId && l.Direction == direction);
        if (index < 0) return false;
        var before = Snapshot;
        _links.RemoveAt(index);
        _deletedLinks[(fromId, direction)] = NextRevision();
        RecordEdit(before);
        return true;
    }

    public void SetAreaSettings(MapAreaSettings settings)
    {
        if (!MapFileFormat.Text(settings.Area, 512)) return;
        if (_areas.TryGetValue(settings.Area, out var old) && old.GridMode == settings.GridMode) return;
        if (!_areas.ContainsKey(settings.Area) && _areas.Count >= 10000) return;
        var before = Snapshot;
        _areas[settings.Area] = settings with { Revision = NextRevision() };
        RecordEdit(before);
    }

    public bool SetCurrentRoom(string id)
    {
        if (!_rooms.ContainsKey(id)) return false;
        _current = id;
        ClearCandidates();
        // Manual placement is useful for navigation but is not authoritative server evidence.
        _state = MapTrackingState.Inferred;
        Changed?.Invoke();
        return true;
    }

    public bool Undo()
    {
        if (!CanUndo) return false;
        var edit = _undo[^1]; _undo.RemoveAt(_undo.Count - 1);
        ApplyHistory(edit.After, edit.Before);
        _redo.Add(edit);
        Changed?.Invoke();
        return true;
    }

    public bool Redo()
    {
        if (!CanRedo) return false;
        var edit = _redo[^1]; _redo.RemoveAt(_redo.Count - 1);
        ApplyHistory(edit.Before, edit.After);
        _undo.Add(edit);
        Changed?.Invoke();
        return true;
    }

    private string ResolveAlias(string id)
    {
        var seen = new HashSet<string>();
        while (_aliases.TryGetValue(id, out var alias) && alias.TargetId != id && seen.Add(id)) id = alias.TargetId;
        return id;
    }

    private void DeleteRoom(string id)
    {
        foreach (var alias in _aliases.Values.Where(a => a.TargetId == id).ToArray())
            _aliases[alias.SourceId] = new(alias.SourceId, alias.SourceId, NextRevision());
        _rooms.Remove(id);
        _deletedRooms[id] = NextRevision();
        foreach (var link in _links.Where(l => l.FromId == id || l.ToId == id).ToArray())
        {
            _links.Remove(link);
            _deletedLinks[(link.FromId, link.Direction)] = NextRevision();
        }
        if (_current == id) { _current = null; _state = MapTrackingState.Unknown; }
        ClearCandidates();
    }

    private void RecordEdit(MapSnapshot before)
    {
        _undo.Add((before, Snapshot));
        // History holds only manual actions, bounded independently of observations.
        if (_undo.Count > 50) _undo.RemoveAt(0);
        _redo.Clear();
        ClearCandidates();
        Changed?.Invoke();
    }

    private void ApplyHistory(MapSnapshot from, MapSnapshot to)
    {
        // Replay only the changed entities so observations made after an edit are retained.
        var oldRooms = from.Rooms.ToDictionary(r => r.Id);
        var newRooms = to.Rooms.ToDictionary(r => r.Id);
        foreach (var id in oldRooms.Keys.Union(newRooms.Keys))
        {
            oldRooms.TryGetValue(id, out var old); newRooms.TryGetValue(id, out var desired);
            if (old == desired) continue;
            if (desired is null) DeleteRoom(id);
            else _rooms[id] = desired with { Revision = NextRevision() };
        }
        var oldLinks = from.Links.ToDictionary(l => (l.FromId, l.Direction));
        var newLinks = to.Links.ToDictionary(l => (l.FromId, l.Direction));
        foreach (var key in oldLinks.Keys.Union(newLinks.Keys))
        {
            oldLinks.TryGetValue(key, out var old); newLinks.TryGetValue(key, out var desired);
            if (old == desired) continue;
            _links.RemoveAll(l => (l.FromId, l.Direction) == key);
            if (desired is null) _deletedLinks[key] = NextRevision();
            else if (_rooms.ContainsKey(desired.FromId)) _links.Add(desired with { Revision = NextRevision() });
        }
        var oldAreas = from.AreaSettings.ToDictionary(a => a.Area);
        var newAreas = to.AreaSettings.ToDictionary(a => a.Area);
        foreach (var area in oldAreas.Keys.Union(newAreas.Keys))
        {
            oldAreas.TryGetValue(area, out var old); newAreas.TryGetValue(area, out var desired);
            if (old != desired) _areas[area] = (desired ?? new MapAreaSettings(area)) with { Revision = NextRevision() };
        }
        var oldAliases = from.RoomAliases.ToDictionary(a => a.SourceId);
        var newAliases = to.RoomAliases.ToDictionary(a => a.SourceId);
        foreach (var source in oldAliases.Keys.Union(newAliases.Keys))
        {
            oldAliases.TryGetValue(source, out var old); newAliases.TryGetValue(source, out var desired);
            if (old != desired) _aliases[source] = (desired ?? new MapRoomAlias(source, source, 0)) with { Revision = NextRevision() };
        }
        if (from.CurrentRoomId != to.CurrentRoomId)
            _current = to.CurrentRoomId is not null && _rooms.ContainsKey(to.CurrentRoomId) ? to.CurrentRoomId : null;
        ClearCandidates();
        _state = _current is null ? MapTrackingState.Unknown : MapTrackingState.Inferred;
    }
}
