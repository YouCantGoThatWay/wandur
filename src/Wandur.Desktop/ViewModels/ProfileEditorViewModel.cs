using L = Wandur.Core.Localization.Strings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wandur.Core.Discovery;
using Wandur.Core.Sessions;
using Wandur.Core.Settings;
using Wandur.Desktop.Services;

namespace Wandur.Desktop.ViewModels;

public sealed partial class ProfileEditorViewModel : ObservableObject, IDisposable
{
    private readonly IWorldProfileStore _store;
    private readonly IWorldDirectory _directory;
    private readonly IProfileAutomationFactory? _automationFactory;
    private readonly IAgentClientServices? _agents;
    private readonly Dictionary<Guid, AgentProfileViewModel> _agentEditors = [];
    private readonly Dictionary<Guid, ProfileAutomationEditor> _automationEditors = [];
    public IReadOnlyList<LocalizedChoiceViewModel> Sections { get; } = [new(nameof(L.ProfileConnectionSection)), new(nameof(L.ProfileLoginSection)), new(nameof(L.WorldScripts)), new(nameof(L.MacrosTab)), new(nameof(L.AgentSettings))];
    [ObservableProperty] private int _sectionIndex;
    [ObservableProperty] private bool _sectionsVisible = true;
    public bool IsConnection => SectionIndex == 0;
    public bool IsLogin => SectionIndex == 1;
    public bool IsScripts => SectionIndex == 2;
    public bool IsMacros => SectionIndex == 3;
    public bool IsAgent => SectionIndex == 4;
    public bool IsAutomation => IsScripts || IsMacros || IsAgent;
    public bool IsConnectionForm => !IsAutomation;
    public bool CanEditAutomation => IsAgent ? _agents is not null : _automationFactory is not null;
    public bool ShowAutomationPlaceholder => IsAutomation && !CanEditAutomation;
    public bool ShowAutomationEditors => IsAutomation && CanEditAutomation;
    public string SectionTitle => Sections[Math.Clamp(SectionIndex, 0, Sections.Count - 1)].Label;
    public ProfileAutomationEditor? Automation
    {
        get
        {
            if (_automationFactory is null) return null;
            var profile = EditingProfile;
            if (!_automationEditors.TryGetValue(profile.Id, out var editor))
            {
                _automationEditors.Add(profile.Id, editor = _automationFactory.Create(profile));
                editor.Changed += DraftChanged;
            }
            return editor;
        }
    }
    public AgentProfileViewModel? Agent
    {
        get
        {
            if (_agents is null) return null;
            var profile = EditingProfile;
            if (!_agentEditors.TryGetValue(profile.Id, out var editor))
            {
                _agentEditors.Add(profile.Id, editor = new(WorldKey(profile), _agents, SelectedProfile is null ? new Wandur.Core.Agents.AgentProfile() : null));
                editor.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(editor.HasUnsavedChanges)) DraftChanged(); };
            }
            return editor;
        }
    }
    partial void OnSectionIndexChanged(int value) => RefreshSections();
    private void RefreshSections()
    {
        foreach (var property in new[] { nameof(IsConnection), nameof(IsLogin), nameof(IsScripts), nameof(IsMacros), nameof(IsAgent), nameof(Agent), nameof(IsAutomation), nameof(IsConnectionForm), nameof(CanEditAutomation), nameof(ShowAutomationPlaceholder), nameof(ShowAutomationEditors), nameof(SectionTitle), nameof(Automation) }) OnPropertyChanged(property);
    }
    private void LanguageChanged()
    {
        foreach (var section in Sections) section.Refresh();
        OnPropertyChanged(nameof(CredentialHelp));
        PasswordWatermark = _passwordId.HasValue ? L.SavedLeaveBlankToKeep : L.Password;
        RefreshSections();
    }
    [RelayCommand] private void ToggleSections() => SectionsVisible = !SectionsVisible;
    private Guid _newProfileId = Guid.NewGuid();
    private ConnectionProfile EditingProfile => SelectedProfile ?? new ConnectionProfile { Id = _newProfileId, Host = "" };
    private static string WorldKey(ConnectionProfile profile) => $"{profile.Host.Trim().ToLowerInvariant()}:{profile.Port}:{profile.UseTls}";
    private sealed record FormState(string Name, string Host, decimal? Port, string Encoding, bool Tls,
        string Username, string Password, bool Remember, bool AutoLogin, string UsernamePrompt, string PasswordPrompt);
    private FormState? _baseline;
    private FormState Capture() => new(WorldName, Host, Port, Encoding, UseTls, Username, Password, RememberPassword, AutoLogin, UsernamePrompt, PasswordPrompt);
    public bool HasUnsavedChanges => (_baseline is not null && Capture() != _baseline)
        || _pendingRemoval || _automationEditors.Values.Any(e => e.HasUnsavedChanges) || _agentEditors.Values.Any(e => e.HasUnsavedChanges);
    private void DraftChanged() => OnPropertyChanged(nameof(HasUnsavedChanges));
    public Func<Task<bool>>? ConfirmDiscardAsync { get; set; }
    public async Task<bool> CanCloseAsync() => !IsBusy && (!HasUnsavedChanges || (ConfirmDiscardAsync is not null && await ConfirmDiscardAsync()));
    public async Task<bool> SelectProfileAsync(ConnectionProfile? profile, bool createNew = false)
    {
        if (!createNew && SelectedProfile == profile) return true;
        if (!await CanCloseAsync()) return false;
        ClearEditors(); _newProfileId = Guid.NewGuid(); _pendingRemoval = false;
        SelectedProfile = profile;
        OnSelectedProfileChanged(profile);
        return true;
    }
    private bool _pendingRemoval;
    public bool PendingRemoval => _pendingRemoval;
    private void ClearEditors()
    {
        foreach (var editor in _automationEditors.Values) { editor.Changed -= DraftChanged; editor.Dispose(); }
        foreach (var editor in _agentEditors.Values) editor.Dispose();
        _automationEditors.Clear(); _agentEditors.Clear();
    }
    private CancellationTokenSource? _lookup;
    private bool _updating;
    private bool _disposed;
    private string? _suggestedName;
    private Guid? _passwordId;
    public IReadOnlyList<ConnectionProfile> Profiles { get; private set; }
    public string[] Encodings { get; } = ["utf-8", "latin1"];
    public string CredentialHelp => L.Format(L.PasswordsAreStoredInAutoLoginWaitsForEach, _store.CredentialStoreName);
    [ObservableProperty] private ConnectionProfile? _selectedProfile;
    [ObservableProperty] private string _worldName = "";
    [ObservableProperty] private string _host = "";
    [ObservableProperty] private decimal? _port = 4000;
    [ObservableProperty] private string _encoding = "utf-8";
    [ObservableProperty] private bool _useTls;
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _passwordWatermark = L.Password;
    [ObservableProperty] private bool _rememberPassword;
    [ObservableProperty] private bool _autoLogin;
    [ObservableProperty] private string _usernamePrompt = AutoLoginSequence.DefaultUsernamePrompt;
    [ObservableProperty] private string _passwordPrompt = AutoLoginSequence.DefaultPasswordPrompt;
    [ObservableProperty] private string _directoryStatus = L.PasteAnAddressToFindItsNameInThe;
    [ObservableProperty] private string _directoryTip = "";
    [ObservableProperty] private string _error = "";
    public bool HasError => !string.IsNullOrEmpty(Error);
    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));
    [ObservableProperty] private bool _isBusy;
    public bool IsEditable => !IsBusy;
    public event Action? CloseRequested;

    public ProfileEditorViewModel(IWorldProfileStore store, IWorldDirectory directory, ConnectionProfile? selectedProfile = null, IProfileAutomationFactory? automationFactory = null, IAgentClientServices? agents = null)
    {
        _store = store;
        _directory = directory; _automationFactory = automationFactory; _agents = agents;
        Wandur.Core.Localization.UiLanguage.Changed += LanguageChanged;
        Profiles = store.Profiles.ToList();
        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == selectedProfile?.Id);
        _baseline ??= Capture();
        if (SelectedProfile is null && selectedProfile is not null)
        {
            var empty = _baseline;
            OnSelectedProfileChanged(selectedProfile with { PasswordId = null, AutoLogin = false });
            _baseline = empty;
        }
        PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(WorldName) or nameof(Host) or nameof(Port) or nameof(Encoding) or nameof(UseTls)
                or nameof(Username) or nameof(Password) or nameof(RememberPassword) or nameof(AutoLogin) or nameof(UsernamePrompt) or nameof(PasswordPrompt)) DraftChanged();
        };
    }

    partial void OnSelectedProfileChanged(ConnectionProfile? value)
    {
        _updating = true; CancelLookup(); _suggestedName = null;
        WorldName = value?.Name ?? ""; Host = value?.Host ?? ""; Port = value?.Port ?? 4000;
        Encoding = value?.Encoding ?? "utf-8"; UseTls = value?.UseTls ?? false;
        Username = value?.Username ?? ""; Password = ""; _passwordId = value?.PasswordId;
        PasswordWatermark = _passwordId.HasValue ? L.SavedLeaveBlankToKeep : L.Password;
        RememberPassword = _passwordId.HasValue; AutoLogin = value?.AutoLogin ?? false;
        UsernamePrompt = value?.UsernamePrompt ?? AutoLoginSequence.DefaultUsernamePrompt;
        PasswordPrompt = value?.PasswordPrompt ?? AutoLoginSequence.DefaultPasswordPrompt;
        Error = ""; DirectoryStatus = L.SavedWorldEditTheAddressToLookUpIts; DirectoryTip = "";
        _updating = false; _baseline = Capture(); DraftChanged(); OnPropertyChanged(nameof(PendingRemoval)); RemoveCommand.NotifyCanExecuteChanged(); RefreshSections();
    }

    partial void OnRememberPasswordChanged(bool value) { if (!value) { AutoLogin = false; Password = ""; } }
    partial void OnHostChanged(string? oldValue, string newValue)
    {
        if (_updating) return;
        if (_suggestedName is not null && WorldName == _suggestedName) WorldName = "";
        _suggestedName = null;
        if (newValue.Length - (oldValue?.Length ?? 0) > 1) NormalizeAddress();
        _ = LookupAsync();
    }
    partial void OnPortChanged(decimal? value) { if (!_updating) _ = LookupAsync(); }
    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsEditable));
        ApplyCommand.NotifyCanExecuteChanged(); SaveCommand.NotifyCanExecuteChanged(); RemoveCommand.NotifyCanExecuteChanged(); NewCommand.NotifyCanExecuteChanged();
    }

    public void NormalizeAddress()
    {
        if (!WorldAddress.TryParse(Host, out var address)) return;
        _updating = true;
        try { if (address!.Port is { } port) Port = port; Host = address.Host; }
        finally { _updating = false; }
    }

    private void CancelLookup() { _lookup?.Cancel(); _lookup?.Dispose(); _lookup = null; }
    private async Task LookupAsync()
    {
        CancelLookup();
        if (_disposed || _updating) return;
        DirectoryStatus = L.PasteAnAddressToFindItsNameInThe; DirectoryTip = "";
        if (!WorldAddress.TryParse(Host, out var address) || Port is null) return;
        var request = _lookup = new CancellationTokenSource(); var token = request.Token;
        try
        {
            await Task.Delay(500, token); DirectoryStatus = L.LookingUpWorldName;
            var suggestion = await _directory.LookupAsync(address!.Host, address.Port ?? (int)Port.Value, token);
            if (_disposed || token.IsCancellationRequested || _lookup != request) return;
            if (suggestion is null) { DirectoryStatus = L.NoUniqueDirectoryMatchEnterAnyNameYouLike; return; }
            if (string.IsNullOrWhiteSpace(WorldName) || WorldName == _suggestedName) WorldName = _suggestedName = suggestion.Name;
            DirectoryStatus = L.Format(L.DirectoryNamed, suggestion.Name); DirectoryTip = suggestion.Listing.ToString();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!_disposed && !token.IsCancellationRequested && _lookup == request) DirectoryStatus = L.DirectoryUnavailableYouCanStillNameAndSaveThis;
        }
    }

    private bool CanEdit() => !IsBusy;
    private bool CanRemove() => !IsBusy && SelectedProfile is not null;
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task NewAsync() => await SelectProfileAsync(null, createNew: true);
    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void Remove()
    {
        _pendingRemoval = !_pendingRemoval;
        OnPropertyChanged(nameof(PendingRemoval)); DraftChanged();
    }
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task SaveAsync() => SaveProfileAsync(close: true);
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task ApplyAsync() => SaveProfileAsync(close: false);
    private async Task SaveProfileAsync(bool close)
    {
        IsBusy = true; Error = ""; CancelLookup();
        try
        {
            if (_pendingRemoval && SelectedProfile is { } removed)
            {
                await _store.RemoveWorldAsync(removed);
                IsBusy = false; CloseRequested?.Invoke(); return;
            }
            NormalizeAddress();
            if (Port is null || decimal.Truncate(Port.Value) != Port.Value) throw new ArgumentException(L.EnterAWholePortNumber);
            var profile = new ConnectionProfile
            {
                Id = EditingProfile.Id, Name = WorldName.Trim(), Host = Host.Trim(), Port = (int)Port.Value,
                Encoding = Encoding, UseTls = UseTls, Username = Username.Trim(), PasswordId = _passwordId,
                AutoLogin = AutoLogin, UsernamePrompt = UsernamePrompt, PasswordPrompt = PasswordPrompt,
                Theme = SelectedProfile is { } previous && previous.Host.TrimEnd('.').Equals(Host.Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase)
                    && previous.Port == (int)Port.Value && previous.UseTls == UseTls ? previous.Theme : null,
                ProtocolMapping = SelectedProfile?.GetProtocolMapping() is { } mapping && mapping.Endpoint.Matches(new(Host.Trim(), (int)Port.Value, UseTls))
                    ? mapping : null
            };
            // Validate every edited section before the first persistent write.
            var validation = profile with { PasswordId = RememberPassword ? (_passwordId ?? (Password.Length > 0 ? Guid.NewGuid() : null)) : null };
            validation.Validate();
            _automationEditors.TryGetValue(profile.Id, out var automation);
            _agentEditors.TryGetValue(profile.Id, out var agent);
            if (automation?.HasUnsavedChanges == true) automation.Validate();
            if (agent?.HasUnsavedChanges == true) agent.ValidateDraft();
            await _store.SaveWorldAsync(profile, Password, RememberPassword);
            Password = "";
            _passwordId = _store.Profiles.First(p => p.Id == profile.Id).PasswordId;
            if (agent?.HasUnsavedChanges == true) await agent.SaveDraftAsync(WorldKey(profile));
            if (automation?.HasUnsavedChanges == true) await automation.SaveAsync(WorldKey(profile));
            _baseline = Capture(); DraftChanged();
            if (!close)
            {
                var saved = _store.Profiles.First(p => p.Id == profile.Id);
                Profiles = _store.Profiles.ToList();
                OnPropertyChanged(nameof(Profiles));
                SelectedProfile = saved;
                // Equal records still need password metadata refreshed after an Apply.
                OnSelectedProfileChanged(saved);
            }
            IsBusy = false;
            if (close) CloseRequested?.Invoke();
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsBusy = false; }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; CancelLookup(); Password = "";
        Wandur.Core.Localization.UiLanguage.Changed -= LanguageChanged;
        ClearEditors();
    }
}
