using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wandur.Core.Classification;
using Wandur.Core.Mapping;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.ViewModels;

/// <summary>Session map presentation and a completely isolated local recognition exercise.</summary>
public sealed partial class MapViewModel : ObservableObject
{
    private RoomMapTracker _live;
    private readonly WorkspaceController? _controller;
    private RoomMapTracker? _exercise;
    private bool _attached;
    [ObservableProperty] private MapSnapshot _snapshot;
    [ObservableProperty] private string? _selectedRoomId;
    [ObservableProperty] private double _centerX;
    [ObservableProperty] private double _centerY;
    [ObservableProperty] private double _panX;
    [ObservableProperty] private double _panY;
    [ObservableProperty] private double _selectedFloor;
    [ObservableProperty] private double _zoom = 1;
    [ObservableProperty] private int _exerciseStep;
    private double? _lastCurrentFloor;
    private bool _hasCentered;
    private double _viewportWidth = 320;
    private double _viewportHeight = 300;
    private bool _fitFloor;

    public MapViewModel(RoomMapTracker live) { _live = live; _snapshot = live.Snapshot; }
    public MapViewModel(WorkspaceController controller) : this(controller.Map) => _controller = controller;
    public WorkspaceController? Controller => _controller;
    public bool IsExercise => _exercise is not null;
    public bool IsLive => !IsExercise;
    public bool CanOpenEditor => IsLive && _controller?.HasSession == true;
    public string ExerciseButtonLabel => IsExercise ? L.MapReturnLive : L.MapTryExercise;
    public string ModeLabel => IsExercise ? L.MapLocalExercise : L.MapLiveSession;
    public string Counts => L.Format(L.MapCounts, Snapshot.Rooms.Count, Snapshot.Links.Count);
    public bool IsEmpty => Snapshot.Rooms.Count == 0;
    public IReadOnlyList<double> Floors => Snapshot.Rooms.Where(r => (r.Area ?? "") == SelectedArea).Select(r => r.Z).Distinct().Order().ToArray();
    public IReadOnlyList<MapRoom> VisibleRooms => Snapshot.Rooms.Where(r => (r.Area ?? "") == SelectedArea && r.Z == SelectedFloor).ToArray();
    public MapViewport CreateViewport(double width, double height) => new(CenterX, CenterY, PanX, PanY,
        Zoom, width, height);
    public string FloorLabel => L.Format(L.MapFloor, SelectedFloor);
    public string SelectionDescription => Snapshot.Rooms.FirstOrDefault(r => r.Id == SelectedRoomId)?.Description ?? "";
    public string Status => Snapshot.State switch
    {
        MapTrackingState.Confirmed => L.MapConfirmed,
        MapTrackingState.Inferred => L.MapInferred,
        MapTrackingState.Ambiguous => L.Format(L.MapAmbiguous, Snapshot.CandidateRoomIds.Count),
        MapTrackingState.Unknown => L.MapUnknown,
        _ => L.MapWaiting
    };
    public string Evidence => Snapshot.Source switch { RoomDataSource.Gmcp => L.MapSourceGmcp, RoomDataSource.Msdp => L.MapSourceMsdp, _ => L.MapSourceText };
    public string Selection => Snapshot.Rooms.FirstOrDefault(r => r.Id == SelectedRoomId)?.Name
        ?? Snapshot.Rooms.FirstOrDefault(r => r.Id == Snapshot.CurrentRoomId)?.Name ?? L.MapSelectRoom;
    public string ExerciseProgress => L.Format(L.MapExerciseProgress, ExerciseStep + 1, 4);
    public string ExerciseInstruction => ExerciseStep switch
    {
        0 => L.MapExerciseStart,
        1 => L.MapExerciseNorthOne,
        2 => L.MapExerciseNorthTwo,
        _ => L.MapExerciseLandmark
    };

    public RoomClassificationService? Classification => _controller?.Classification;
    public bool HasClassification => Classification is not null && IsLive;
    public Func<Task<string?>>? PickModelFile { get; set; }
    public string ClassificationStatus => Classification?.Status switch
    {
        { State: RoomClassificationState.Ready, Version: var version } => L.Format(L.MapInferenceReady, version ?? ""),
        { State: RoomClassificationState.Downloading, Progress: var progress } => L.Format(L.MapInferenceDownloading, Math.Round(progress * 100)),
        { State: RoomClassificationState.Failed, Message: var message } => L.Format(L.MapInferenceFailed, message ?? ""),
        _ => L.MapInferenceNotInstalled
    };
    public bool CanDownloadModel => Classification is { Status.State: RoomClassificationState.NotInstalled or RoomClassificationState.Failed };
    public bool ClassifyRoomsLocally
    {
        get => _controller?.Settings.ClassifyRoomsLocally ?? false;
        set { if (_controller is null || value == _controller.Settings.ClassifyRoomsLocally) return; _controller.SaveSettings(_controller.Settings with { ClassifyRoomsLocally = value }); OnPropertyChanged(); }
    }
    [RelayCommand(CanExecute = nameof(CanDownloadModel))]
    private async Task DownloadModel() { if (Classification is { } service) { await service.DownloadAsync(CancellationToken.None); _controller?.ScheduleInference(); } }
    [RelayCommand]
    private async Task InstallModelFromFile()
    {
        if (Classification is not { } service || PickModelFile is null) return;
        if (await PickModelFile() is { } path) { await service.InstallFromFileAsync(path, CancellationToken.None); _controller?.ScheduleInference(); }
    }
    private void ClassificationChanged() => Dispatcher.UIThread.Post(() =>
    {
        OnPropertyChanged(nameof(ClassificationStatus)); OnPropertyChanged(nameof(CanDownloadModel)); DownloadModelCommand.NotifyCanExecuteChanged();
    });

    public void Attach()
    {
        if (_attached) return;
        _attached = true;
        Wandur.Core.Localization.UiLanguage.Changed += RefreshLanguage;
        _live.Changed += Refresh;
        if (_controller is not null) _controller.Changed += RebindSession;
        if (Classification is not null) Classification.Changed += ClassificationChanged;
        RebindSession(); Refresh();
    }
    public void Detach()
    {
        if (!_attached) return;
        Wandur.Core.Localization.UiLanguage.Changed -= RefreshLanguage;
        _attached = false; _live.Changed -= Refresh;
        if (_controller is not null) _controller.Changed -= RebindSession;
        if (Classification is not null) Classification.Changed -= ClassificationChanged;
    }
    private void RefreshLanguage() => OnPropertyChanged(string.Empty);
    private void RebindSession()
    {
        if (!_attached) return;
        RefreshNavigationState();
        OnPropertyChanged(nameof(ClassifyRoomsLocally));
        if (_controller is null || ReferenceEquals(_live, _controller.Map)) return;
        _live.Changed -= Refresh;
        _exercise = null;
        ResetEditingState();
        OnPropertyChanged(nameof(IsExercise)); OnPropertyChanged(nameof(IsLive)); OnPropertyChanged(nameof(ExerciseButtonLabel)); OnPropertyChanged(nameof(ModeLabel));
        NextExerciseCommand.NotifyCanExecuteChanged(); RecheckPositionCommand.NotifyCanExecuteChanged();
        _live = _controller.Map;
        _live.Changed += Refresh;
        _hasCentered = false;
        _lastCurrentFloor = null; _lastCurrentArea = null;
        ClearRoute();
        Refresh();
    }
    private void Refresh()
    {
        OnPropertyChanged(nameof(CanOpenEditor));
        Snapshot = Tracker.Snapshot;
        var current = Snapshot.Rooms.FirstOrDefault(r => r.Id == Snapshot.CurrentRoomId);
        if (!_hasCentered && Snapshot.Rooms.Count > 0)
        {
            SelectedArea = current?.Area ?? Snapshot.Rooms[0].Area ?? "";
            SelectedFloor = current?.Z ?? Floors.FirstOrDefault();
            CenterOnFloor(current);
            _hasCentered = true;
            if (current is null) FitFloor();
        }
        else if (current is not null && (current.Z != _lastCurrentFloor || (current.Area ?? "") != _lastCurrentArea))
        {
            SelectedArea = current.Area ?? "";
            SelectedFloor = current.Z;
            CenterOnFloor(current);
        }
        else if (Floors.Count > 0 && !Floors.Contains(SelectedFloor))
        {
            SelectedFloor = Floors.First();
            CenterOnFloor(null);
        }
        _lastCurrentFloor = current?.Z; _lastCurrentArea = current?.Area ?? (current is null ? null : "");
        if (_fitFloor) FitFloor();
        if (SelectedRoomId is not null && !Snapshot.Rooms.Any(r => r.Id == SelectedRoomId)) SelectedRoomId = null;
        OnPropertyChanged(nameof(Counts)); OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(Evidence)); OnPropertyChanged(nameof(Selection));
        OnPropertyChanged(nameof(VisibleRooms)); OnPropertyChanged(nameof(Floors)); OnPropertyChanged(nameof(SelectionDescription));
        FloorUpCommand.NotifyCanExecuteChanged(); FloorDownCommand.NotifyCanExecuteChanged();
        RefreshEditorState();
        RefreshNavigationState();
    }
    partial void OnSelectedRoomIdChanged(string? value)
    {
        SelectionChanged();
        OnPropertyChanged(nameof(Selection)); OnPropertyChanged(nameof(SelectionDescription));
    }
    partial void OnSelectedFloorChanged(double value)
    {
        SelectedRoomId = null;
        OnPropertyChanged(nameof(VisibleRooms)); OnPropertyChanged(nameof(FloorLabel));
        FloorUpCommand.NotifyCanExecuteChanged(); FloorDownCommand.NotifyCanExecuteChanged();
    }
    partial void OnExerciseStepChanged(int value)
    {
        OnPropertyChanged(nameof(ExerciseProgress)); OnPropertyChanged(nameof(ExerciseInstruction));
        NextExerciseCommand.NotifyCanExecuteChanged();
    }
    public void Pan(double x, double y) { _fitFloor = false; PanX += x; PanY += y; }
    public void ChangeZoom(double delta) => ZoomAt(Zoom * Math.Pow(1.12, Math.Clamp(delta, -100, 100)), new(_viewportWidth / 2, _viewportHeight / 2));
    public void ZoomAt(double zoom, Avalonia.Point anchor)
    {
        if (!double.IsFinite(zoom) || zoom <= 0 || !double.IsFinite(anchor.X) || !double.IsFinite(anchor.Y)) return;
        var location = CreateViewport(_viewportWidth, _viewportHeight).Unproject(anchor);
        _fitFloor = false;
        Zoom = Math.Clamp(zoom, 0.05, 4);
        var projected = CreateViewport(_viewportWidth, _viewportHeight).Project(location.X, location.Y);
        PanX += anchor.X - projected.X; PanY += anchor.Y - projected.Y;
    }
    [RelayCommand] private void ZoomIn() => ChangeZoom(2);
    [RelayCommand] private void ZoomOut() => ChangeZoom(-2);
    public void SetViewportSize(double width, double height)
    {
        _viewportWidth = Math.Max(1, width); _viewportHeight = Math.Max(1, height);
        if (_fitFloor) FitFloor();
    }
    [RelayCommand] private void FitFloor()
    {
        _fitFloor = true;
        CenterOnFloor(null);
        var rooms = VisibleRooms;
        if (rooms.Count == 0) { Zoom = 1; return; }
        var x = Math.Max(1, rooms.Max(r => r.X) - rooms.Min(r => r.X) + (IsGridMode ? 1 : 0));
        var y = Math.Max(1, rooms.Max(r => r.Y) - rooms.Min(r => r.Y) + (IsGridMode ? 1 : 0));
        Zoom = Math.Clamp(Math.Min((_viewportWidth - 72) / (96 * x), (_viewportHeight - 72) / (96 * y)), 0.05, 1);
    }
    private bool CanFloorUp() => Floors.Any(f => f > SelectedFloor);
    private bool CanFloorDown() => Floors.Any(f => f < SelectedFloor);
    [RelayCommand(CanExecute = nameof(CanFloorUp))] private void FloorUp() => ShowFloor(Floors.First(f => f > SelectedFloor));
    [RelayCommand(CanExecute = nameof(CanFloorDown))] private void FloorDown() => ShowFloor(Floors.Last(f => f < SelectedFloor));
    public void ShowFloor(double floor)
    {
        if (!Floors.Contains(floor)) return;
        SelectedFloor = floor;
        FitFloor();
    }
    private void CenterOnFloor(MapRoom? room)
    {
        var rooms = VisibleRooms;
        CenterX = room?.X ?? (rooms.Count == 0 ? 0 : (rooms.Min(r => r.X) + rooms.Max(r => r.X)) / 2);
        CenterY = room?.Y ?? (rooms.Count == 0 ? 0 : (rooms.Min(r => r.Y) + rooms.Max(r => r.Y)) / 2);
        PanX = PanY = 0;
    }
    private bool CanRecheckPosition() => !IsExercise;
    [RelayCommand(CanExecute = nameof(CanRecheckPosition))] private void RecheckPosition() { _live.LosePosition(); Refresh(); }
    [RelayCommand] private void Center()
    {
        _fitFloor = false;
        var current = Snapshot.Rooms.FirstOrDefault(r => r.Id == Snapshot.CurrentRoomId);
        if (current is not null) { SelectedArea = current.Area ?? ""; SelectedFloor = current.Z; }
        CenterOnFloor(current);
        Zoom = 1;
    }
    [RelayCommand] private void ToggleExercise()
    {
        Controller?.StopMapWalk();
        if (_exercise is null)
        {
            var rooms = new List<MapRoom>
            {
                new("a", L.MapExerciseCorridor, L.MapExerciseCorridorDescription, null, 0, 0, 0, false),
                new("b", L.MapExerciseCorridor, L.MapExerciseCorridorDescription, null, 0, 1, 0, false),
                new("c", L.MapExerciseCorridor, L.MapExerciseCorridorDescription, null, 0, 2, 0, false),
                new("d", L.MapExerciseCorridor, L.MapExerciseCorridorDescription, null, 2, 0, 0, false),
                new("landmark", L.MapExerciseBeacon, L.MapExerciseBeaconDescription, null, 0, 3, 0, false),
                new("stairs", L.MapExerciseStairs, L.MapExerciseStairsDescription, null, 2, -1, 1, false)
            };
            var links = new List<MapLink>
            {
                new("a", "b", "north", true), new("b", "a", "south", true),
                new("b", "c", "north", true), new("c", "b", "south", true),
                new("c", "landmark", "north", true), new("landmark", "c", "south", true),
                new("d", "stairs", "south", true), new("stairs", "d", "north", true)
            };
            _exercise = new RoomMapTracker(new MapSnapshot(rooms, links, [], null, MapTrackingState.Unknown, RoomDataSource.Text, 0));
            _exercise.LosePosition();
            _exercise.Observe(Corridor());
            ExerciseStep = 0;
        }
        else _exercise = null;
        ResetEditingState();
        _hasCentered = false;
        _lastCurrentFloor = null; _lastCurrentArea = null;
        ClearRoute();
        SelectedRoomId = null;
        OnPropertyChanged(nameof(IsExercise)); OnPropertyChanged(nameof(IsLive)); OnPropertyChanged(nameof(ExerciseButtonLabel)); OnPropertyChanged(nameof(ModeLabel));
        NextExerciseCommand.NotifyCanExecuteChanged(); RecheckPositionCommand.NotifyCanExecuteChanged();
        Refresh();
    }
    private static RoomObservation Corridor() => new(null, L.MapExerciseCorridor, L.MapExerciseCorridorDescription, new Dictionary<string, string?>());
    private bool CanNextExercise() => IsExercise && ExerciseStep < 3;
    [RelayCommand(CanExecute = nameof(CanNextExercise))] private void NextExercise()
    {
        if (_exercise is null) return;
        ExerciseStep++;
        _exercise.Observe(ExerciseStep == 3
            ? new RoomObservation(null, L.MapExerciseBeacon, L.MapExerciseBeaconDescription, new Dictionary<string, string?>())
            : Corridor(), "north");
        Refresh();
    }
}
