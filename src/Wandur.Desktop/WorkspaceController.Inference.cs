using Avalonia.Threading;
using Wandur.Core.Classification;
using Wandur.Core.Mapping;

namespace Wandur.Desktop;

public sealed partial class WorkspaceController
{
    private const int InferenceQueueLimit = 512;
    private readonly object _inferenceGate = new();
    private readonly LinkedList<(string Id, string Name, string Description)> _inferenceQueue = new();
    private readonly HashSet<string> _inferenceQueued = [];
    private CancellationTokenSource? _inferenceCancellation;
    private Task? _inferenceWorker;
    private RoomMapTracker? _inferenceMap;
    private bool _rescanPending;

    internal Task? InferenceWorkerForTests => _inferenceWorker;
    internal void CancelInferenceForTests() => CancelInference();

    /// <summary>Queues every room lacking terrain whose inference is missing or stale. UI thread only.</summary>
    internal void ScheduleInference()
    {
        if (!CanInfer(out var modelVersion)) return;
        var map = Map;
        var candidates = map.RoomsNeedingInference(modelVersion);
        if (candidates.Count == 0) return;
        EnqueueInference(map, candidates, modelVersion);
    }

    /// <summary>Queues one room without scanning the map; used on every room observation. UI thread only.</summary>
    internal void ScheduleInference(string roomId)
    {
        if (!CanInfer(out var modelVersion)) return;
        var map = Map;
        // Terrain from the server or the user always wins, so those rooms are never classified.
        if (map.Snapshot.Rooms.FirstOrDefault(room => room.Id == roomId) is not { } candidate ||
            !string.IsNullOrWhiteSpace(candidate.Environment) ||
            candidate.InferredKey == RoomTextPreprocessor.InferenceKey(candidate.Name, candidate.Description, modelVersion)) return;
        EnqueueInference(map, [candidate], modelVersion);
    }

    /// <summary>The model version to plan with, taken without paying for a model load on the UI thread.</summary>
    private bool CanInfer(out string modelVersion)
    {
        modelVersion = "";
        // A walk compares map revisions between steps, and applied inference bumps them.
        if (_disposed || _mapWalk is not null || Classification is null || !Settings.ClassifyRoomsLocally) return false;
        if (Classification.ModelVersion is not { } version) return false;
        modelVersion = version;
        return true;
    }

    private void EnqueueInference(RoomMapTracker map, IReadOnlyList<MapRoom> candidates, string modelVersion)
    {
        var current = map.Snapshot.CurrentRoomId;
        lock (_inferenceGate)
        {
            if (!ReferenceEquals(_inferenceMap, map)) { _inferenceQueue.Clear(); _inferenceQueued.Clear(); _inferenceMap = map; }
            foreach (var room in candidates)
            {
                if (_inferenceQueue.Count >= InferenceQueueLimit) break;
                if (!_inferenceQueued.Add(room.Id)) continue;
                // The room the player is standing in is the one whose colour they are waiting for.
                if (room.Id == current) _inferenceQueue.AddFirst((room.Id, room.Name, room.Description));
                else _inferenceQueue.AddLast((room.Id, room.Name, room.Description));
            }
            if (_inferenceQueue.Count == 0) return;
            // A worker whose token was already cancelled is winding down and cannot take new work.
            if (_inferenceWorker is { IsCompleted: false } && _inferenceCancellation is not null) return;
            _inferenceCancellation ??= new CancellationTokenSource();
            var token = _inferenceCancellation.Token;
            var threshold = Settings.RoomClassificationThreshold;
            _inferenceWorker = Task.Run(() => RunInference(map, modelVersion, threshold, token), token);
        }
    }

    private void RunInference(RoomMapTracker map, string modelVersion, double threshold, CancellationToken token)
    {
        // Creating the classifier loads the model, which is why it happens here and not on the UI thread.
        if (Classification?.TryGetClassifier() is not { } classifier || classifier.ModelVersion != modelVersion)
        {
            // Only this map's queue: a newer map may already have work waiting for its own worker.
            lock (_inferenceGate)
            {
                if (ReferenceEquals(_inferenceMap, map)) { _inferenceQueue.Clear(); _inferenceQueued.Clear(); }
            }
            return;
        }
        var progressed = false;
        while (!token.IsCancellationRequested)
        {
            if (!TryDequeueInference(map, progressed, out var item, out var rescan))
            {
                if (rescan) Dispatcher.UIThread.Post(RescanInference);
                return;
            }
            try
            {
                var key = RoomTextPreprocessor.InferenceKey(item.Name, item.Description, classifier.ModelVersion);
                var prediction = classifier.Classify(item.Name, item.Description, threshold);
                progressed = true;
                Dispatcher.UIThread.Post(() =>
                {
                    lock (_inferenceGate) _inferenceQueued.Remove(item.Id);
                    // A walk in progress would read the revision bump as a changed graph and stop.
                    if (_disposed || _mapWalk is not null || token.IsCancellationRequested || !ReferenceEquals(Map, map)) return;
                    if (map.ApplyInference(item.Id, key, classifier.ModelVersion, prediction)) _mapDirty = true;
                });
            }
            // One bad room must not fault the worker or leave its id stuck in the queued set.
            catch (Exception)
            {
                lock (_inferenceGate) _inferenceQueued.Remove(item.Id);
            }
        }
    }

    private bool TryDequeueInference(RoomMapTracker map, bool progressed, out (string Id, string Name, string Description) item, out bool rescan)
    {
        item = default;
        rescan = false;
        lock (_inferenceGate)
        {
            if (!ReferenceEquals(_inferenceMap, map)) return false;
            if (_inferenceQueue.Count == 0)
            {
                // Sweep up whatever the per-room fast path missed: once per drain, and only after real work.
                if (progressed && !_rescanPending) { _rescanPending = true; rescan = true; }
                return false;
            }
            item = _inferenceQueue.First!.Value;
            _inferenceQueue.RemoveFirst();
            return true;
        }
    }

    /// <summary>Inference is held back while walking, so the map catches up once the walk ends.</summary>
    private void ScheduleInferenceAfterWalk() => Dispatcher.UIThread.Post(ScheduleInference);

    private void RescanInference()
    {
        lock (_inferenceGate) _rescanPending = false;
        ScheduleInference();
    }

    private void CancelInference()
    {
        lock (_inferenceGate)
        {
            _inferenceCancellation?.Cancel();
            _inferenceCancellation?.Dispose();
            _inferenceCancellation = null;
            _inferenceQueue.Clear(); _inferenceQueued.Clear();
            _inferenceMap = null;
            _rescanPending = false;
        }
    }
}
