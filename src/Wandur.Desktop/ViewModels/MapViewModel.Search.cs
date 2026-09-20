using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wandur.Core.Mapping;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.ViewModels;

/// <summary>
/// Toolbar room search: highlights rooms already seen whose observed name or description matches every
/// typed term, never sends anything to the world, and steps through matches (including across floors)
/// without disturbing the live session. See <see cref="Wandur.Core.Mapping.RoomSearch"/> for the matching rule.
/// </summary>
public sealed partial class MapViewModel
{
    [ObservableProperty] private string _roomSearchQuery = "";
    [ObservableProperty] private bool _isRoomSearchVisible;
    [ObservableProperty] private MapRoom? _selectedRoomSearchMatch;
    private int _roomSearchIndex = -1;

    /// <summary>Filtering starts only once there is enough text to be meaningful.</summary>
    public bool IsRoomSearchActive => RoomSearchQuery.Trim().Length >= 2;
    public IReadOnlyList<MapRoom> RoomSearchMatches => IsRoomSearchActive
        ? RoomSearch.Search(Snapshot.Rooms, RoomSearchQuery).OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToArray()
        : [];
    public int RoomSearchMatchCount => RoomSearchMatches.Count;
    public string RoomSearchCountLabel => L.Format(L.MapRoomSearchCount, RoomSearchMatchCount);
    public IReadOnlySet<string> RoomSearchMatchIds => RoomSearchMatches.Select(r => r.Id).ToHashSet();
    /// <summary>Matches not on the floor currently shown, reachable from the dropdown without stepping.</summary>
    public IReadOnlyList<MapRoom> RoomSearchOtherFloorMatches => RoomSearchMatches
        .Where(r => (r.Area ?? "") != SelectedArea || r.Z != SelectedFloor).ToArray();
    public bool HasRoomSearchOtherFloorMatches => RoomSearchOtherFloorMatches.Count > 0;
    private bool CanStepRoomSearch() => RoomSearchMatches.Count > 0;

    partial void OnRoomSearchQueryChanged(string value)
    {
        _roomSearchIndex = -1;
        NotifyRoomSearchChanged();
    }
    partial void OnIsRoomSearchVisibleChanged(bool value)
    {
        if (!value) { RoomSearchQuery = ""; _roomSearchIndex = -1; }
        NotifyRoomSearchChanged();
    }

    [RelayCommand] private void ToggleRoomSearch() => IsRoomSearchVisible = !IsRoomSearchVisible;

    [RelayCommand(CanExecute = nameof(CanStepRoomSearch))]
    private void NextRoomSearchMatch()
    {
        var matches = RoomSearchMatches;
        if (matches.Count == 0) return;
        _roomSearchIndex = (_roomSearchIndex + 1) % matches.Count;
        SelectRoom(matches[_roomSearchIndex].Id);
    }

    /// <summary>Picking an other-floor match from the dropdown jumps straight to it, without disturbing stepping order.</summary>
    partial void OnSelectedRoomSearchMatchChanged(MapRoom? value) { if (value is not null) SelectRoom(value.Id); }

    [RelayCommand]
    private void ClearRoomSearch()
    {
        RoomSearchQuery = "";
        _roomSearchIndex = -1;
        NotifyRoomSearchChanged();
    }

    private void NotifyRoomSearchChanged()
    {
        OnPropertyChanged(nameof(IsRoomSearchActive)); OnPropertyChanged(nameof(RoomSearchMatches));
        OnPropertyChanged(nameof(RoomSearchMatchCount)); OnPropertyChanged(nameof(RoomSearchCountLabel));
        OnPropertyChanged(nameof(RoomSearchMatchIds)); OnPropertyChanged(nameof(RoomSearchOtherFloorMatches));
        OnPropertyChanged(nameof(HasRoomSearchOtherFloorMatches));
        NextRoomSearchMatchCommand.NotifyCanExecuteChanged();
    }
}
