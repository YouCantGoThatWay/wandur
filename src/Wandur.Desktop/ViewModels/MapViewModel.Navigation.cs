using CommunityToolkit.Mvvm.Input;
using Wandur.Core.Mapping;
using Wandur.Core.Protocol;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.ViewModels;

public sealed partial class MapViewModel
{
    private bool _hasWalkFeedback;
    private bool _routeUnavailable;
    public bool HasWalkFeedback => _hasWalkFeedback || IsWalking;
    public bool IsWalking => Controller?.IsMapWalking == true;
    public string NavigationFeedback => _routeUnavailable ? L.MapRouteUnavailable : WalkStatus;
    [RelayCommand]
    private async Task WalkToRoomAsync(string? roomId)
    {
        if (IsEditMode || !IsLive || IsWalking || roomId is null || !Snapshot.Rooms.Any(r => r.Id == roomId)) return;
        SelectedRoomId = roomId;
        _routeDestinationId = roomId;
        PlannedRoute = Snapshot.CurrentRoomId is { } from ? MapRoutePlanner.FindRoute(Snapshot, from, roomId, false) : null;
        _hasWalkFeedback = true; _routeUnavailable = PlannedRoute is null;
        RefreshNavigationState();
        if (PlannedRoute is not null) await WalkRouteAsync();
    }
    public string WalkStatus => Controller?.MapWalkStatus ?? L.MapWalkUnavailable;
    public string ProtocolStatus
    {
        get
        {
            var evidence = Controller?.ProtocolEvidence;
            static string State(TelnetOptionState state) => state switch
            {
                TelnetOptionState.Enabled => L.MapProtocolEnabled,
                TelnetOptionState.Disabled => L.MapProtocolDisabled,
                _ => L.MapProtocolUnknown
            };
            return L.Format(L.MapProtocolSummary, State(evidence?.Gmcp ?? TelnetOptionState.Unknown), State(evidence?.Msdp ?? TelnetOptionState.Unknown));
        }
    }
    public string RoomFieldsStatus
    {
        get
        {
            var evidence = Controller?.ProtocolEvidence;
            var fields = new List<string>();
            if (evidence?.ReceivedRoomId == true) fields.Add(L.MapFieldId);
            if (evidence?.ReceivedName == true) fields.Add(L.MapFieldName);
            if (evidence?.ReceivedDescription == true) fields.Add(L.MapFieldDescription);
            if (evidence?.ReceivedArea == true) fields.Add(L.MapFieldArea);
            if (evidence?.ReceivedSymbol == true) fields.Add(L.MapFieldSymbol);
            if (evidence?.ReceivedExits == true) fields.Add(L.MapFieldExits);
            if (evidence?.ReceivedTerrain == true) fields.Add(L.MapFieldEnvironment);
            if (evidence?.ReceivedCoordinates == true) fields.Add(L.MapFieldCoordinates);
            return fields.Count == 0 ? L.MapRoomFieldsNone : L.Format(L.MapRoomFields, string.Join(", ", fields));
        }
    }
    private bool CanWalkRoute() => IsLive && Controller is { IsConnected: true, IsMapWalking: false } && PlannedRoute is { Steps.Count: > 0 };
    [RelayCommand(CanExecute = nameof(CanWalkRoute))]
    private async Task WalkRouteAsync()
    {
        _hasWalkFeedback = true; _routeUnavailable = false;
        if (Controller is not null && PlannedRoute is { } route && IsLive) await Controller.StartMapWalkAsync(route);
        RefreshNavigationState();
    }
    private bool CanStopWalking() => Controller?.IsMapWalking == true;
    [RelayCommand(CanExecute = nameof(CanStopWalking))]
    private void StopWalking() { Controller?.StopMapWalk(); RefreshNavigationState(); }
    private void RefreshNavigationState()
    {
        OnPropertyChanged(nameof(IsWalking)); OnPropertyChanged(nameof(HasWalkFeedback)); OnPropertyChanged(nameof(NavigationFeedback));
        OnPropertyChanged(nameof(WalkStatus)); OnPropertyChanged(nameof(ProtocolStatus)); OnPropertyChanged(nameof(RoomFieldsStatus));
        WalkRouteCommand.NotifyCanExecuteChanged(); StopWalkingCommand.NotifyCanExecuteChanged();
    }
    public void ImportMap(string json)
    {
        var map = MapFileFormat.Deserialize(json);
        Controller?.StopMapWalk();
        _hasCentered = false; _lastCurrentFloor = null; _lastCurrentArea = null;
        ResetEditingState();
        Tracker.ReplaceMap(map);
        ClearRoute(); Refresh();
        EditMessage = L.MapImportComplete;
    }
    public string ExportMap() => MapFileFormat.Serialize(Tracker.Snapshot);
}
