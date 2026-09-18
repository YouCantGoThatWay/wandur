using System.Globalization;
using Avalonia.Threading;
using Wandur.Core.Mapping;
using Wandur.Core.Sessions;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop;

public sealed partial class WorkspaceController
{
    private sealed class MapWalk(IMudSession session, RoomMapTracker map, MapSnapshot graph, MapLink[] steps, long privacyEpoch)
    {
        public IMudSession Session { get; } = session;
        public RoomMapTracker Map { get; } = map;
        public MapSnapshot Graph { get; } = graph;
        public MapLink[] Steps { get; } = steps;
        public long PrivacyEpoch { get; } = privacyEpoch;
        public CancellationTokenSource Cancellation { get; } = new();
        public string? ExpectedServerId { get; set; }
        public string? ExpectedRoomId { get; set; }
        public TaskCompletionSource? Arrival { get; set; }
    }

    private volatile MapWalk? _mapWalk;
    private long _mapPrivacyEpoch;
    private string _mapWalkStatusKey = "MapWalkReady";
    private object[] _mapWalkStatusArguments = [];
    public bool IsMapWalking => _mapWalk is not null;
    public string MapWalkStatus => L.Format(L.ResourceManager.GetString(_mapWalkStatusKey, Wandur.Core.Localization.UiLanguage.Culture) ?? _mapWalkStatusKey, _mapWalkStatusArguments);
    internal TimeSpan MapWalkStepTimeout { get; set; } = TimeSpan.FromSeconds(10);

    private void SetMapWalkStatus(string key, params object[] arguments)
    {
        _mapWalkStatusKey = key;
        _mapWalkStatusArguments = arguments;
        Changed?.Invoke();
    }

    public void StopMapWalk() => StopMapWalk("MapWalkStopped");

    private void StopMapWalk(string reason)
    {
        var walk = _mapWalk;
        if (walk is null) return;
        _mapWalk = null;
        walk.Cancellation.Cancel();
        SetMapWalkStatus(reason);
    }

    public async Task StartMapWalkAsync(MapRoute route)
    {
        _agentRunner?.Stop("AgentManual");
        StopMapWalk();
        FlushOutput();
        var session = _session;
        var graph = Map.Snapshot;
        if (session?.IsConnected != true || IsConnecting || _disposed || IsPrivate || _login is not null ||
            _pendingMapDirection is not null ||
            session is TelnetSession { RemoteEcho: true } || graph.State != MapTrackingState.Confirmed || graph.CurrentRoomId is null)
        { SetMapWalkStatus("MapWalkUnavailable"); return; }
        var steps = route.Steps.ToArray();
        // Imported/custom commands need explicit manual handling. Validate every step before the first write.
        if (steps.Any(step => (step.Command ?? step.Direction).Any(char.IsControl) || RoomMapTracker.NormalizeDirection(step.Command ?? step.Direction) is null))
        { SetMapWalkStatus("MapWalkCustomCommand"); return; }
        if (!ValidateMapRoute(graph, steps)) { SetMapWalkStatus("MapWalkInvalidRoute"); return; }
        if (steps.Length == 0) { SetMapWalkStatus("MapWalkComplete"); return; }

        var walk = new MapWalk(session, Map, graph, steps, Interlocked.Read(ref _mapPrivacyEpoch));
        _mapWalk = walk;
        try
        {
            for (var index = 0; index < steps.Length; index++)
            {
                // Drain an entire pending burst, including failures/privacy prompts, before advancing.
                await Dispatcher.UIThread.InvokeAsync(FlushOutput, DispatcherPriority.Background);
                if (!CanContinueMapWalk(walk)) return;
                var step = steps[index];
                if (Map.Snapshot.CurrentRoomId != step.FromId || Map.Snapshot.State != MapTrackingState.Confirmed)
                { StopMapWalk("MapWalkWrongRoom"); return; }
                walk.ExpectedServerId = graph.Rooms.First(room => room.Id == step.ToId).ServerId;
                walk.ExpectedRoomId = step.ToId;
                walk.Arrival = new(TaskCreationOptions.RunContinuationsAsynchronously);
                SetMapWalkStatus("MapWalkProgress", index + 1, steps.Length);
                var command = RoomMapTracker.NormalizeDirection(step.Command ?? step.Direction)!;
                if (!await SendCoreAsync(command, fromMapWalk: true, cancellationToken: walk.Cancellation.Token))
                { if (ReferenceEquals(_mapWalk, walk)) StopMapWalk(walk.PrivacyEpoch != Interlocked.Read(ref _mapPrivacyEpoch) ? "MapWalkPrivate" : "MapWalkSendFailed"); return; }
                await walk.Arrival.Task.WaitAsync(MapWalkStepTimeout, walk.Cancellation.Token);
            }
            await Dispatcher.UIThread.InvokeAsync(FlushOutput, DispatcherPriority.Background);
            if (CanContinueMapWalk(walk)) { _mapWalk = null; SetMapWalkStatus("MapWalkComplete"); }
        }
        catch (OperationCanceledException) when (walk.Cancellation.IsCancellationRequested)
        { if (ReferenceEquals(_mapWalk, walk)) StopMapWalk(walk.PrivacyEpoch != Interlocked.Read(ref _mapPrivacyEpoch) ? "MapWalkPrivate" : "MapWalkStopped"); }
        catch (TimeoutException) { if (ReferenceEquals(_mapWalk, walk)) StopMapWalk("MapWalkTimeout"); }
        finally
        {
            if (ReferenceEquals(_mapWalk, walk)) { _mapWalk = null; Changed?.Invoke(); }
            walk.Cancellation.Dispose();
        }
    }

    private bool CanContinueMapWalk(MapWalk walk)
    {
        if (!ReferenceEquals(_mapWalk, walk)) return false;
        if (!ReferenceEquals(_session, walk.Session) || !ReferenceEquals(Map, walk.Map) || !IsConnected || _disposed)
        { StopMapWalk("MapWalkDisconnected"); return false; }
        if (IsPrivate || _login is not null || walk.Session is TelnetSession { RemoteEcho: true } ||
            walk.PrivacyEpoch != Interlocked.Read(ref _mapPrivacyEpoch))
        { StopMapWalk("MapWalkPrivate"); return false; }
        if (!SameWalkGraph(walk.Graph, Map.Snapshot)) { StopMapWalk("MapWalkGraphChanged"); return false; }
        if (Map.Snapshot.State != MapTrackingState.Confirmed) { StopMapWalk("MapWalkWrongRoom"); return false; }
        return true;
    }

    private void ObserveMapWalkArrival(RoomObservation room)
    {
        var walk = _mapWalk;
        if (walk is null || !CanContinueMapWalk(walk)) return;
        // Text matches, names and provisional IDs are never movement acknowledgements.
        if (room.Source == RoomDataSource.Text || string.IsNullOrWhiteSpace(room.ServerId)) return;
        if (walk.ExpectedServerId is null || room.ServerId != walk.ExpectedServerId || Map.Snapshot.State != MapTrackingState.Confirmed ||
            Map.Snapshot.CurrentRoomId != walk.ExpectedRoomId)
        { StopMapWalk("MapWalkWrongRoom"); return; }
        walk.Arrival?.TrySetResult();
    }

    private static bool ValidateMapRoute(MapSnapshot graph, IReadOnlyList<MapLink> steps)
    {
        if (steps.Count > 10000) return false;
        var rooms = graph.Rooms.ToDictionary(room => room.Id);
        var current = graph.CurrentRoomId;
        if (current is null || !rooms.TryGetValue(current, out var origin) || !WalkableRoom(origin)) return false;
        foreach (var step in steps)
        {
            var actual = graph.Links.FirstOrDefault(link => link.FromId == step.FromId && link.Direction == step.Direction);
            if (step.FromId != current || actual is null || LinkSignature(actual) != LinkSignature(step) ||
                !step.Confirmed || step.IsLocked || step.DoorState is MapDoorState.Closed or MapDoorState.Locked ||
                !rooms.TryGetValue(step.ToId, out var destination) || !WalkableRoom(destination)) return false;
            var weight = step.Weight == 0 ? destination.Weight : step.Weight;
            if (!double.IsFinite(weight) || weight <= 0) return false;
            current = step.ToId;
        }
        return true;
    }

    private static bool WalkableRoom(MapRoom room) => !room.Provisional && !room.IsLocked &&
        !string.IsNullOrWhiteSpace(room.ServerId) && room.ServerId is not ("0" or "-1");

    private static (string From, string To, string Direction, bool Confirmed, string? Command, bool Locked, MapDoorState Door, double Weight, long Revision) LinkSignature(MapLink link) =>
        (link.FromId, link.ToId, link.Direction, link.Confirmed, link.Command, link.IsLocked, link.DoorState, link.Weight, link.Revision);

    private static bool SameWalkGraph(MapSnapshot first, MapSnapshot second) =>
        first.Rooms.Count == second.Rooms.Count && first.Links.Count == second.Links.Count &&
        first.Rooms.OrderBy(room => room.Id).Select(room => (room.Id, room.ServerId, room.Provisional, room.IsLocked, room.Weight, room.Revision))
            .SequenceEqual(second.Rooms.OrderBy(room => room.Id).Select(room => (room.Id, room.ServerId, room.Provisional, room.IsLocked, room.Weight, room.Revision))) &&
        first.Links.Select(LinkSignature).OrderBy(link => link.From).ThenBy(link => link.Direction)
            .SequenceEqual(second.Links.Select(LinkSignature).OrderBy(link => link.From).ThenBy(link => link.Direction));
}
