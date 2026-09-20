using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wandur.Core.Settings;
using Wandur.Core.Storage;

namespace Wandur.Desktop.ViewModels;

public sealed partial class WorldLibraryViewModel : ObservableObject
{
    private readonly SessionWorkspace _sessions;
    private readonly Action<ConnectionProfile>? _editProfile;
    /// <summary>The saved list the shown order was built from, in the manual order the settings keep.</summary>
    private IReadOnlyList<ConnectionProfile>? _source;
    [ObservableProperty] private IReadOnlyList<ConnectionProfile> _profiles = [];
    [ObservableProperty] private ConnectionProfile? _selectedProfile;
    public bool HasWorlds => Profiles.Count > 0;
    public bool IsEmpty => !HasWorlds;
    public bool CanBrowse { get; }
    public IRelayCommand AddCommand { get; }
    public IRelayCommand BrowseCommand { get; }
    /// <summary>Set by the view: true while the pointer is over the list or a row's menu is open, when a reorder would jump under the user.</summary>
    internal Func<bool>? IsBusy { get; set; }
    /// <summary>A usage change arrived while the list was busy; the order is applied when the pointer leaves or the panel next opens.</summary>
    public bool ReorderPending { get; private set; }

    public WorldLibraryViewModel(SessionWorkspace sessions, Action addWorld, Action? browseWorlds, Action<ConnectionProfile>? editProfile)
    {
        _sessions = sessions; _editProfile = editProfile; CanBrowse = browseWorlds is not null;
        AddCommand = new RelayCommand(addWorld);
        BrowseCommand = new RelayCommand(() => browseWorlds?.Invoke());
        Refresh();
    }

    public void Attach()
    {
        _sessions.Changed += Refresh;
        _sessions.UsageChanged += UsageChanged;
        // Connections counted while the panel was closed are applied as it opens: nobody is looking yet.
        _source = null; ReorderPending = false;
        Refresh();
    }
    public void Detach() { _sessions.Changed -= Refresh; _sessions.UsageChanged -= UsageChanged; }

    private void Refresh()
    {
        var source = _sessions.Active.Controller.Settings.Profiles;
        if (ReferenceEquals(source, _source)) return;
        // Every tab loads the settings for itself, so a new list with the same worlds in the same order is
        // not a change: only an edit, an addition or a removal reorders the list at once.
        var same = _source is not null && source.SequenceEqual(_source);
        _source = source;
        if (same) return;
        ReorderPending = false;
        Show(Order(source));
    }

    /// <summary>
    /// The reorder rule: a connection just counted moves its world up at once unless the pointer is over the
    /// list or a row menu is open, in which case the new order waits for the pointer to leave.
    /// </summary>
    private void UsageChanged()
    {
        if (_source is null) return;
        if (IsBusy?.Invoke() == true) { ReorderPending = true; return; }
        ReorderPending = false;
        Show(Order(_source));
    }

    public void ApplyPendingOrder()
    {
        if (!ReorderPending || _source is null) return;
        ReorderPending = false;
        Show(Order(_source));
    }

    /// <summary>Most used first; a store that cannot be read leaves the manual order, which is also what a client without a database shows.</summary>
    private IReadOnlyList<ConnectionProfile> Order(IReadOnlyList<ConnectionProfile> source)
    {
        if (_sessions.Usage is not { } store || source.Count < 2) return source;
        try { return WorldUsage.Order(source, store.Load(), DateTimeOffset.UtcNow); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Data.Common.DbException) { return source; }
    }

    private void Show(IReadOnlyList<ConnectionProfile> ordered)
    {
        var id = SelectedProfile?.Id;
        if (!Profiles.SequenceEqual(ordered)) Profiles = ordered;
        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == id) ?? Profiles.FirstOrDefault();
        OnPropertyChanged(nameof(HasWorlds)); OnPropertyChanged(nameof(IsEmpty));
    }
    // Selecting a saved world only chooses what Connect will open; its theme arrives with the session.
    partial void OnSelectedProfileChanged(ConnectionProfile? value)
    {
        EditCommand.NotifyCanExecuteChanged(); ConnectCommand.NotifyCanExecuteChanged(); DeleteCommand.NotifyCanExecuteChanged();
    }
    private bool CanEdit() => _editProfile is not null && SelectedProfile is not null;
    private bool CanConnect() => SelectedProfile is not null;
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void Edit() { if (SelectedProfile is { } profile) _editProfile?.Invoke(profile); }
    [RelayCommand]
    private void EditProfile(ConnectionProfile? profile) { if (profile is null) return; SelectedProfile = profile; Edit(); }
    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync() { if (SelectedProfile is { } profile) await _sessions.OpenAsync(profile); }
    [RelayCommand(CanExecute = nameof(CanConnect))]
    private Task DeleteAsync() => DeleteProfileAsync(SelectedProfile);
    [RelayCommand]
    private async Task DeleteProfileAsync(ConnectionProfile? clickedProfile)
    {
        var controller = _sessions.Active.Controller;
        var profile = controller.Settings.Profiles.FirstOrDefault(p => p.Id == clickedProfile?.Id);
        if (profile is null) return;
        try { await controller.RemoveWorldAsync(profile); Refresh(); }
        catch (Exception ex) { controller.ShowNotice(ex.Message); }
    }
}
