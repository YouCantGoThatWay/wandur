using Wandur.Core.Storage;
using Wandur.Core.Mapping;
using Wandur.Core.Sessions;
using Wandur.Core.Settings;
using Wandur.Core.Protocol;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop;

/// <summary>Evidence observed during this session, independent of imported/cached map contents.</summary>
public sealed record MapProtocolEvidence(
    TelnetOptionState Gmcp = TelnetOptionState.Unknown,
    TelnetOptionState Msdp = TelnetOptionState.Unknown,
    RoomDataSource? LastRoomSource = null,
    bool ReceivedRoomId = false,
    bool ReceivedExits = false,
    bool ReceivedTerrain = false,
    bool ReceivedCoordinates = false,
    bool ReceivedName = false,
    bool ReceivedDescription = false,
    bool ReceivedArea = false,
    bool ReceivedSymbol = false);

public sealed partial class WorkspaceController
{
    private readonly IRoomMapStore _maps;
    private readonly IWorldKnowledgeStore? _knowledge;
    private readonly object _knowledgeGate = new();
    private ProtocolObservation? _pendingGmcpKnowledge;
    private ProtocolObservation? _pendingMsdpKnowledge;
    private readonly Dictionary<string, DateTimeOffset> _pendingRoomKnowledge = [];
    private DateTimeOffset _knowledgeSavedAt;
    private readonly TextRoomObserver _roomText = new();
    private ConnectionProfile? _mapProfile;
    private string? _pendingMapDirection;
    private DateTimeOffset _mapCommandAt;
    private bool _hasStructuredRooms;
    private (string Id, int ObservationCount, DateTimeOffset ReceivedAt)? _tentativeTextRoom;
    private bool _mapDirty;
    private DateTimeOffset _mapSavedAt;
    public RoomMapTracker Map { get; private set; } = new();
    public MapProtocolEvidence ProtocolEvidence { get; private set; } = new();

    private void StartMapping(ConnectionProfile? profile, IMudSession session)
    {
        StopMapWalk("MapWalkDisconnected");
        SetMapWalkStatus("MapWalkReady");
        ProtocolEvidence = new();
        lock (_knowledgeGate)
        {
            _pendingGmcpKnowledge = _pendingMsdpKnowledge = null;
            _pendingRoomKnowledge.Clear();
        }
        _mapProfile = profile;
        Map = new(profile is null ? null : _maps.Load(profile.Host, profile.Port));
        _roomText.Reset();
        _pendingMapDirection = null;
        _hasStructuredRooms = false;
        _tentativeTextRoom = null;
        _mapDirty = false;
        Map.Changed += () => { _mapDirty = true; if (_mapWalk is { } walk) CanContinueMapWalk(walk); };
        CancelInference();
        ScheduleInference();
        if (session is TelnetSession telnet)
        {
            var previousProtocols = telnet.ProtocolState;
            telnet.ProtocolStateChanged += state =>
            {
                // Stamp only changed protocols, on receipt. A later MSDP event is not new GMCP evidence.
                var observedAt = DateTimeOffset.UtcNow;
                if (_knowledge is not null && ReferenceEquals(_session, session))
                {
                    lock (_knowledgeGate)
                    {
                        if (state.Gmcp != previousProtocols.Gmcp && state.Gmcp != TelnetOptionState.Unknown)
                            _pendingGmcpKnowledge = new(state.Gmcp, observedAt);
                        if (state.Msdp != previousProtocols.Msdp && state.Msdp != TelnetOptionState.Unknown)
                            _pendingMsdpKnowledge = new(state.Msdp, observedAt);
                    }
                }
                previousProtocols = state;
                Dispatch(session, () =>
                {
                    ProtocolEvidence = ProtocolEvidence with { Gmcp = state.Gmcp, Msdp = state.Msdp };
                    _ = RefreshMappedProtocolSubscriptionAsync();
                    Changed?.Invoke();
                });
            };
            telnet.PrivateIntervalReceived += () =>
            {
                Interlocked.Increment(ref _mapPrivacyEpoch);
                // Cancel a queued socket write immediately; UI dispatch may run after it acquires the send lock.
                var walking = _mapWalk;
                if (walking is not null && ReferenceEquals(walking.Session, session))
                {
                    try { walking.Cancellation.Cancel(); }
                    catch (ObjectDisposedException) { } // The walk completed concurrently.
                }
                Dispatch(session, () => StopMapWalk("MapWalkPrivate"));
            };
            telnet.RoomReceived += room =>
            {
                if (_knowledge is not null && ReferenceEquals(_session, session)) RecordRoomKnowledge(room, DateTimeOffset.UtcNow);
                lock (_pendingLock)
                {
                    var length = room.Name.Length + room.Description.Length + (room.ServerId?.Length ?? 0) +
                        (room.Area?.Length ?? 0) + (room.Environment?.Length ?? 0) + room.Exits.Sum(exit => exit.Key.Length + (exit.Value?.Length ?? 0));
                    if (_pendingCharacters + length > 524_288 || _pending.Count >= 2048)
                    { _pending.Clear(); _pendingCharacters = 0; _droppedOutput = true; }
                    _pending.Enqueue((session, "", -1, null, room));
                    _pendingCharacters += length;
                }
            };
        }
    }

    private void ObserveRoom(RoomObservation room)
    {
        var direction = DateTimeOffset.UtcNow - _mapCommandAt < TimeSpan.FromSeconds(10) ? _pendingMapDirection : null;
        var originServerId = Map.Snapshot.Rooms.FirstOrDefault(r => r.Id == Map.Snapshot.CurrentRoomId)?.ServerId;
        // GMCP/MSDP can repeat the origin while a move is in flight. Such an update
        // refreshes metadata; it does not acknowledge arrival or consume the move.
        var repeatsOrigin = direction is not null && originServerId is not null && room.ServerId == originServerId;
        var observations = Map.Snapshot.ObservationCount;
        var roomCount = Map.Snapshot.Rooms.Count;
        // A server can send the text and metadata portions of one response in separate reads.
        // Only revise a newly-created guess, promptly, with no intervening command/observation.
        var refined = !_hasStructuredRooms && room.Source != RoomDataSource.Text &&
            _tentativeTextRoom is { } tentative && DateTimeOffset.UtcNow - tentative.ReceivedAt < TimeSpan.FromSeconds(2) &&
            Map.RefineProvisionalRoom(tentative.Id, tentative.ObservationCount, room);
        if (!refined) Map.Observe(room, direction);
        if (Map.Snapshot.ObservationCount == observations) return;
        _tentativeTextRoom = room.Source == RoomDataSource.Text && Map.Snapshot.Rooms.Count > roomCount && Map.Snapshot.CurrentRoomId is { } id
            ? (id, Map.Snapshot.ObservationCount, DateTimeOffset.UtcNow) : null;
        // Structured names/exits remain better evidence than a title guessed from login
        // banners or tutorial prose, even when a server does not expose room IDs.
        if (room.Source != RoomDataSource.Text) _hasStructuredRooms = true;
        if (room.Source != RoomDataSource.Text) ProtocolEvidence = ProtocolEvidence with
        {
            LastRoomSource = room.Source,
            ReceivedRoomId = ProtocolEvidence.ReceivedRoomId || room.ServerId is not null,
            ReceivedExits = ProtocolEvidence.ReceivedExits || room.ExitsProvided || room.Exits.Count > 0,
            ReceivedTerrain = ProtocolEvidence.ReceivedTerrain || !string.IsNullOrWhiteSpace(room.Environment),
            ReceivedCoordinates = ProtocolEvidence.ReceivedCoordinates || room.X is not null || room.Y is not null || room.Z is not null,
            ReceivedName = ProtocolEvidence.ReceivedName || !string.IsNullOrWhiteSpace(room.Name),
            ReceivedDescription = ProtocolEvidence.ReceivedDescription || !string.IsNullOrWhiteSpace(room.Description),
            ReceivedArea = ProtocolEvidence.ReceivedArea || !string.IsNullOrWhiteSpace(room.Area),
            ReceivedSymbol = ProtocolEvidence.ReceivedSymbol || !string.IsNullOrWhiteSpace(room.Symbol)
        };
        if (!repeatsOrigin)
        {
            _pendingMapDirection = null;
            ObserveMapWalkArrival(room);
        }
        if (Map.Snapshot.CurrentRoomId is { } observedId) ScheduleInference(observedId);
    }

    private void TrackMapCommand(string command, bool isPrivate)
    {
        _tentativeTextRoom = null;
        if (isPrivate) { _pendingMapDirection = null; return; }
        var direction = RoomMapTracker.NormalizeDirection(command);
        if (direction is not null)
        {
            // Multiple unacknowledged moves cannot be assigned safely to a room response.
            if (_pendingMapDirection is not null) { Map.LosePosition(); _pendingMapDirection = null; }
            else _pendingMapDirection = direction;
            _mapCommandAt = DateTimeOffset.UtcNow;
            _roomText.Reset();
        }
        else
        {
            // A subsequent command (enter, board, teleport, etc.) breaks the
            // pairing with an older direction. The next protocol room still sets
            // our location, but cannot be attributed to that earlier move.
            _pendingMapDirection = null;
            if (command.Trim().Equals("recall", StringComparison.OrdinalIgnoreCase)) Map.LosePosition();
            _roomText.Reset();
        }
    }

    private void TrackMapOutput(string text)
    {
        var rooms = _roomText.Feed(text);
        if (_roomText.MovementFailed)
        {
            StopMapWalk("MapWalkBlocked");
            _pendingMapDirection = null;
            Map.MovementFailed();
        }
        if (_hasStructuredRooms) return;
        foreach (var room in rooms) ObserveRoom(room);
    }

    private void SaveMap(bool force = false)
    {
        SaveProtocolKnowledge(force);
        if (!_mapDirty || _mapProfile is null || (!force && DateTimeOffset.UtcNow - _mapSavedAt < TimeSpan.FromSeconds(2))) return;
        _mapSavedAt = DateTimeOffset.UtcNow;
        try
        {
            _maps.Save(_mapProfile.Host, _mapProfile.Port, Map.Snapshot);
            _mapDirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _mapDirty = false;
            ShowNotice(L.MapCacheFailed);
        }
    }
    private void SaveProtocolKnowledge(bool force)
    {
        if (_knowledge is null || _mapProfile is null || (!force && DateTimeOffset.UtcNow - _knowledgeSavedAt < TimeSpan.FromSeconds(2))) return;
        KnowledgeObservation observation;
        lock (_knowledgeGate)
        {
            if (_pendingGmcpKnowledge is null && _pendingMsdpKnowledge is null && _pendingRoomKnowledge.Count == 0) return;
            observation = new() { Gmcp = _pendingGmcpKnowledge, Msdp = _pendingMsdpKnowledge, RoomFields = new Dictionary<string, DateTimeOffset>(_pendingRoomKnowledge) };
        }
        _knowledgeSavedAt = DateTimeOffset.UtcNow;
        try
        {
            _knowledge.Record(_mapProfile.Host, _mapProfile.Port, observation);
            lock (_knowledgeGate)
            {
                // New socket events may have arrived while SQLite committed this batch.
                if (_pendingGmcpKnowledge == observation.Gmcp) _pendingGmcpKnowledge = null;
                if (_pendingMsdpKnowledge == observation.Msdp) _pendingMsdpKnowledge = null;
                foreach (var field in observation.RoomFields)
                    if (_pendingRoomKnowledge.GetValueOrDefault(field.Key) == field.Value) _pendingRoomKnowledge.Remove(field.Key);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { ShowNotice(L.MapCacheFailed); }
    }

    private void RecordRoomKnowledge(RoomObservation room, DateTimeOffset observedAt)
    {
        if (room.Source == RoomDataSource.Text) return;
        lock (_knowledgeGate)
        {
            if (room.ServerId is not null) _pendingRoomKnowledge["id"] = observedAt;
            if (!string.IsNullOrWhiteSpace(room.Name)) _pendingRoomKnowledge["name"] = observedAt;
            if (!string.IsNullOrWhiteSpace(room.Description)) _pendingRoomKnowledge["description"] = observedAt;
            if (!string.IsNullOrWhiteSpace(room.Area)) _pendingRoomKnowledge["area"] = observedAt;
            if (room.ExitsProvided || room.Exits.Count > 0) _pendingRoomKnowledge["exits"] = observedAt;
            if (!string.IsNullOrWhiteSpace(room.Environment)) _pendingRoomKnowledge["terrain"] = observedAt;
            if (room.X is not null || room.Y is not null || room.Z is not null) _pendingRoomKnowledge["coordinates"] = observedAt;
            if (!string.IsNullOrWhiteSpace(room.Symbol)) _pendingRoomKnowledge["symbol"] = observedAt;
        }
    }
}
