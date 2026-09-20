using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wandur.Core.Mapping;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.ViewModels;

public sealed partial class MapViewModel
{
    [ObservableProperty] private string _selectedArea = "";
    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private MapRoom? _selectedSearchResult;
    [ObservableProperty] private bool _isEditMode;
    [ObservableProperty] private bool _isAddingRoom;
    [ObservableProperty] private string? _editMessage;
    [ObservableProperty] private MapRoom? _mergeTarget;
    [ObservableProperty] private MapLink? _selectedExit;
    [ObservableProperty] private bool _allowInferredRoutes;
    [ObservableProperty] private MapRoute? _plannedRoute;
    private string? _routeDestinationId;
    private string? _lastCurrentArea;

    public string Legend => IsGridMode ? L.MapGridLegend : L.MapNodeLegend;
    public string Gestures => IsEditMode ? L.MapEditGestures : L.MapGestures;
    public RoomMapTracker Tracker => _exercise ?? _live;
    public MapRoomEditorViewModel RoomEditor { get; } = new();
    public MapExitEditorViewModel ExitEditor { get; } = new();
    public IReadOnlyList<MapDoorChoice> DoorChoices { get; } =
    [new(MapDoorState.None, L.MapDoorNone), new(MapDoorState.Open, L.MapDoorOpen),
     new(MapDoorState.Closed, L.MapDoorClosed), new(MapDoorState.Locked, L.MapDoorLocked)];
    public IReadOnlyList<MapAreaChoice> Areas => Snapshot.Rooms.Select(r => r.Area ?? "")
        .Concat(Snapshot.AreaSettings.Select(a => a.Area)).Append("").Distinct().Order(StringComparer.CurrentCultureIgnoreCase)
        .Select(a => new MapAreaChoice(a, a.Length == 0 ? L.MapUnassignedArea : a)).ToArray();
    public MapAreaChoice SelectedAreaChoice
    {
        get => Areas.FirstOrDefault(a => a.Key == SelectedArea) ?? new(SelectedArea, SelectedArea);
        set { if (value is not null) SelectedArea = value.Key; }
    }
    public bool IsGridMode
    {
        get => Snapshot.AreaSettings.FirstOrDefault(a => a.Area == SelectedArea)?.GridMode ?? false;
        set { if (value == IsGridMode) return; Tracker.SetAreaSettings(new MapAreaSettings(SelectedArea, value)); Refresh(); }
    }
    public IReadOnlyList<MapRoom> SearchResults => string.IsNullOrWhiteSpace(SearchQuery) ? [] : Snapshot.Rooms
        .Where(r => new[] { r.Name, r.Id, r.ServerId, r.Description, r.Notes }.Any(t => t?.Contains(SearchQuery.Trim(), StringComparison.CurrentCultureIgnoreCase) == true))
        .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).Take(100).ToArray();
    public bool HasSearchQuery => !string.IsNullOrWhiteSpace(SearchQuery);
    public bool HasNoSearchResults => HasSearchQuery && SearchResults.Count == 0;
    public MapRoom? SelectedRoom => Snapshot.Rooms.FirstOrDefault(r => r.Id == SelectedRoomId);
    public IReadOnlyList<MapRoom> OtherRooms => Snapshot.Rooms.Where(r => r.Id != SelectedRoomId).OrderBy(r => r.Name).ToArray();
    public IReadOnlyList<MapLink> SelectedRoomExits => Snapshot.Links.Where(l => l.FromId == SelectedRoomId).OrderBy(l => l.Direction).ToArray();
    public bool HasRoomSelection => SelectedRoom is not null;
    public bool ShowRoomEditor => IsEditMode && (HasRoomSelection || IsAddingRoom);
    public bool ShowExitEditor => IsEditMode && HasRoomSelection && !IsAddingRoom;
    public bool CanUndo => Tracker.CanUndo;
    public bool CanRedo => Tracker.CanRedo;
    public string RouteStatus => PlannedRoute is { } route ? L.Format(L.MapRouteReady, route.Steps.Count, route.Cost)
        : _routeDestinationId is null ? L.MapRouteSelectRoom : L.MapRouteUnavailable;
    public string RouteCommands => PlannedRoute is { } route ? string.Join(" → ", route.Steps.Select(s => s.Command ?? s.Direction)) : "";

    public void SelectRoom(string id)
    {
        if (Snapshot.Rooms.FirstOrDefault(r => r.Id == id) is not { } room) return;
        SelectedArea = room.Area ?? "";
        SelectedFloor = room.Z;
        SelectedRoomId = id;
        _fitFloor = false;
        CenterOnFloor(room);
    }
    public void MoveRoom(string id, double x, double y)
    {
        if (!IsEditMode || !double.IsFinite(x) || !double.IsFinite(y) || Snapshot.Rooms.FirstOrDefault(r => r.Id == id) is not { } room) return;
        if (Tracker.UpsertRoom(room with { X = x, Y = y, IsManuallyEdited = true }))
        {
            Refresh();
            if (SelectedRoomId == id) RoomEditor.Load(Tracker.Snapshot.Rooms.First(r => r.Id == id));
        }
    }
    partial void OnSelectedAreaChanged(string value)
    {
        SelectedRoomId = null;
        if (!Floors.Contains(SelectedFloor)) SelectedFloor = Floors.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedAreaChoice)); OnPropertyChanged(nameof(IsGridMode)); OnPropertyChanged(nameof(Legend));
        OnPropertyChanged(nameof(Floors)); OnPropertyChanged(nameof(VisibleRooms));
        FloorUpCommand.NotifyCanExecuteChanged(); FloorDownCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(RoomSearchOtherFloorMatches)); OnPropertyChanged(nameof(HasRoomSearchOtherFloorMatches));
        FitFloor();
    }
    partial void OnSearchQueryChanged(string value)
    { OnPropertyChanged(nameof(SearchResults)); OnPropertyChanged(nameof(HasSearchQuery)); OnPropertyChanged(nameof(HasNoSearchResults)); }
    partial void OnSelectedSearchResultChanged(MapRoom? value) { if (value is not null) SelectRoom(value.Id); }
    partial void OnIsEditModeChanged(bool value) { EditMessage = null; OnPropertyChanged(nameof(Gestures)); NotifyEditorCommands(); }
    partial void OnIsAddingRoomChanged(bool value) => NotifyEditorCommands();
    partial void OnMergeTargetChanged(MapRoom? value) => MergeRoomCommand.NotifyCanExecuteChanged();
    partial void OnSelectedExitChanged(MapLink? value)
    { ExitEditor.Load(value, Snapshot.Rooms, DoorChoices); DeleteExitCommand.NotifyCanExecuteChanged(); }
    partial void OnAllowInferredRoutesChanged(bool value) { if (_routeDestinationId is not null) UpdateRoute(); }
    partial void OnPlannedRouteChanged(MapRoute? value)
    { OnPropertyChanged(nameof(RouteStatus)); OnPropertyChanged(nameof(RouteCommands)); ClearRouteCommand.NotifyCanExecuteChanged(); RefreshNavigationState(); }

    private void ResetEditingState()
    {
        IsAddingRoom = false; IsEditMode = false; SelectedRoomId = null; SelectedExit = null;
        MergeTarget = null; SelectedSearchResult = null; SearchQuery = ""; EditMessage = null;
        RoomEditor.Load(new MapRoom("draft", "", "", null, 0, 0, 0, false));
        ExitEditor.Load(null, [], DoorChoices);
    }
    private void SelectionChanged()
    {
        IsAddingRoom = false; EditMessage = null; MergeTarget = null;
        if (SelectedRoom is { } room) RoomEditor.Load(room);
        SelectedExit = null; ExitEditor.Load(null, Snapshot.Rooms, DoorChoices);
        NotifyEditorCommands();
    }
    private void RefreshEditorState()
    {
        foreach (var property in new[] { nameof(Areas), nameof(SelectedAreaChoice), nameof(IsGridMode), nameof(Legend), nameof(SearchResults), nameof(HasNoSearchResults),
                     nameof(OtherRooms), nameof(SelectedRoomExits), nameof(CanUndo), nameof(CanRedo), nameof(Tracker) }) OnPropertyChanged(property);
        NotifyEditorCommands();
        if (_routeDestinationId is not null) UpdateRoute();
    }
    private void NotifyEditorCommands()
    {
        foreach (var property in new[] { nameof(SelectedRoom), nameof(HasRoomSelection), nameof(ShowRoomEditor), nameof(ShowExitEditor), nameof(SelectedRoomExits), nameof(OtherRooms) }) OnPropertyChanged(property);
        SaveRoomCommand.NotifyCanExecuteChanged(); DeleteRoomCommand.NotifyCanExecuteChanged(); MergeRoomCommand.NotifyCanExecuteChanged();
        SetCurrentRoomCommand.NotifyCanExecuteChanged(); SaveExitCommand.NotifyCanExecuteChanged(); DeleteExitCommand.NotifyCanExecuteChanged();
        UndoMapCommand.NotifyCanExecuteChanged(); RedoMapCommand.NotifyCanExecuteChanged(); PlanRouteCommand.NotifyCanExecuteChanged();
    }
    private bool CanEditSelection() => IsEditMode && HasRoomSelection && !IsAddingRoom;
    private bool CanSaveRoom() => IsEditMode && (IsAddingRoom || HasRoomSelection);
    private bool CanMergeRoom() => CanEditSelection() && MergeTarget is not null && MergeTarget.Id != SelectedRoomId;
    private bool CanDeleteExit() => CanEditSelection() && SelectedExit is not null;
    private bool CanPlanRoute() => HasRoomSelection && Snapshot.CurrentRoomId is not null;
    private bool CanClearRoute() => _routeDestinationId is not null;

    [RelayCommand] private void AddRoom()
    {
        IsEditMode = true;
        SelectedRoomId = null; IsAddingRoom = true; EditMessage = null;
        RoomEditor.Load(new MapRoom("manual:" + Guid.NewGuid().ToString("N"), "", "", MapRoomEditorViewModel.Optional(SelectedArea),
            Math.Round(CenterX), Math.Round(CenterY), SelectedFloor, false) { IsManuallyEdited = true });
    }
    [RelayCommand(CanExecute = nameof(CanSaveRoom))] private void SaveRoom()
    {
        if (!RoomEditor.TryBuild(out var room)) { EditMessage = L.MapRoomInvalid; return; }
        if (!Tracker.UpsertRoom(room!)) { EditMessage = L.MapEditRejected; return; }
        Refresh(); SelectRoom(room!.Id); RoomEditor.Load(room); IsAddingRoom = false; EditMessage = L.MapRoomSaved;
    }
    [RelayCommand(CanExecute = nameof(CanEditSelection))] private void DeleteRoom()
    {
        if (SelectedRoomId is not { } id) return;
        if (!Tracker.RemoveRoom(id)) EditMessage = L.MapEditRejected;
        Refresh();
    }
    [RelayCommand(CanExecute = nameof(CanMergeRoom))] private void MergeRoom()
    {
        if (SelectedRoomId is not { } id || MergeTarget is not { } target) return;
        if (!Tracker.MergeRooms(id, target.Id)) { EditMessage = L.MapEditRejected; return; }
        Refresh(); SelectRoom(target.Id);
    }
    [RelayCommand(CanExecute = nameof(CanEditSelection))] private void SetCurrentRoom()
    { if (SelectedRoomId is { } id) { Tracker.SetCurrentRoom(id); Refresh(); } }
    [RelayCommand] private void NewExit() { SelectedExit = null; ExitEditor.Load(null, Snapshot.Rooms, DoorChoices); }
    [RelayCommand(CanExecute = nameof(CanEditSelection))] private void SaveExit()
    {
        if (SelectedRoomId is not { } from || !ExitEditor.TryBuild(from, out var link, out var reverse)) { EditMessage = L.MapExitInvalid; return; }
        if (!Tracker.EditLink(link!, SelectedExit?.Direction, reverse)) { EditMessage = L.MapEditRejected; return; }
        Refresh(); SelectedExit = Snapshot.Links.FirstOrDefault(l => l.FromId == from && l.Direction == link!.Direction); EditMessage = L.MapExitSaved;
    }
    [RelayCommand(CanExecute = nameof(CanDeleteExit))] private void DeleteExit()
    {
        if (SelectedExit is { } exit) Tracker.RemoveLink(exit.FromId, exit.Direction);
        SelectedExit = null; Refresh();
    }
    [RelayCommand(CanExecute = nameof(CanUndo))] private void UndoMap() { Tracker.Undo(); Refresh(); if (SelectedRoom is { } room) { SelectRoom(room.Id); RoomEditor.Load(room); } }
    [RelayCommand(CanExecute = nameof(CanRedo))] private void RedoMap() { Tracker.Redo(); Refresh(); if (SelectedRoom is { } room) { SelectRoom(room.Id); RoomEditor.Load(room); } }
    [RelayCommand(CanExecute = nameof(CanPlanRoute))] private void PlanRoute() { _routeDestinationId = SelectedRoomId; UpdateRoute(); }
    [RelayCommand(CanExecute = nameof(CanClearRoute))] private void ClearRoute() { _routeDestinationId = null; PlannedRoute = null; OnPropertyChanged(nameof(RouteStatus)); ClearRouteCommand.NotifyCanExecuteChanged(); }
    private void UpdateRoute()
    {
        PlannedRoute = Snapshot.CurrentRoomId is { } from && _routeDestinationId is { } to
            ? MapRoutePlanner.FindRoute(Snapshot, from, to, AllowInferredRoutes) : null;
        OnPropertyChanged(nameof(RouteStatus)); ClearRouteCommand.NotifyCanExecuteChanged();
    }
}
