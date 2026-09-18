using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wandur.Core.Settings;

namespace Wandur.Desktop.ViewModels;

public sealed partial class WorldLibraryViewModel : ObservableObject
{
    private readonly SessionWorkspace _sessions;
    private readonly Action<ConnectionProfile>? _editProfile;
    private bool _refreshing;
    [ObservableProperty] private IReadOnlyList<ConnectionProfile> _profiles = [];
    [ObservableProperty] private ConnectionProfile? _selectedProfile;
    public bool HasWorlds => Profiles.Count > 0;
    public bool IsEmpty => !HasWorlds;
    public bool CanBrowse { get; }
    public IRelayCommand AddCommand { get; }
    public IRelayCommand BrowseCommand { get; }

    public WorldLibraryViewModel(SessionWorkspace sessions, Action addWorld, Action? browseWorlds, Action<ConnectionProfile>? editProfile)
    {
        _sessions = sessions; _editProfile = editProfile; CanBrowse = browseWorlds is not null;
        AddCommand = new RelayCommand(addWorld);
        BrowseCommand = new RelayCommand(() => browseWorlds?.Invoke());
        Refresh();
    }

    public void Attach() { _sessions.Changed += Refresh; Refresh(); }
    public void Detach() { _sessions.Changed -= Refresh; _sessions.EndThemePreview(this); }
    private void Refresh()
    {
        if (ReferenceEquals(Profiles, _sessions.Active.Controller.Settings.Profiles)) return;
        var id = SelectedProfile?.Id;
        _refreshing = true;
        Profiles = _sessions.Active.Controller.Settings.Profiles;
        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == id) ?? Profiles.FirstOrDefault();
        _refreshing = false;
        _sessions.PreviewWorldProfile(this, SelectedProfile, activate: false);
        OnPropertyChanged(nameof(HasWorlds)); OnPropertyChanged(nameof(IsEmpty));
    }
    partial void OnSelectedProfileChanged(ConnectionProfile? value)
    {
        EditCommand.NotifyCanExecuteChanged(); ConnectCommand.NotifyCanExecuteChanged(); DeleteCommand.NotifyCanExecuteChanged();
        if (!_refreshing) PreviewSelectedTheme();
    }
    public void PreviewSelectedTheme() => _sessions.PreviewWorldProfile(this, SelectedProfile);
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
