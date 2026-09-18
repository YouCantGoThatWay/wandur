using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wandur.Core.Agents;
using Wandur.Core.Localization;
using Wandur.Desktop.Services;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.ViewModels;

/// <summary>A world-scoped draft; credentials are passed separately to the credential service.</summary>
public sealed partial class AgentProfileViewModel : ObservableObject, IDisposable
{
    private string _worldKey;
    private readonly IAgentClientServices _services;
    private AgentProfile _saved;
    private CancellationTokenSource? _discovery;
    private CancellationTokenSource? _automaticDiscovery;
    private bool _updatingAddress;
    private bool _selectingDefaultGoal;
    private string? _discoveryErrorKey;
    private int _discoveryStatusCode;
    private bool _disposed;
    private bool _loading = true;
    private int _revision;
    private string _baselineDraft = "";
    private string? _statusKey;
    [ObservableProperty] private string _serverAddress = "";
    [ObservableProperty] private bool _isDiscovering;
    [ObservableProperty] private string _endpoint = "";
    [ObservableProperty] private string _model = "";
    [ObservableProperty] private string _systemPrompt = "";
    public ObservableCollection<AgentGoalViewModel> Goals { get; } = [];
    [ObservableProperty] private AgentGoalViewModel? _selectedGoal;
    public bool HasSelectedGoal => SelectedGoal is not null;
    partial void OnSelectedGoalChanged(AgentGoalViewModel? value) => OnPropertyChanged(nameof(HasSelectedGoal));
    private void GoalsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_selectingDefaultGoal) return;
        if (args.PropertyName == nameof(AgentGoalViewModel.Enabled) && sender is AgentGoalViewModel { Enabled: true } selected)
        {
            _selectingDefaultGoal = true;
            try { foreach (var goal in Goals.Where(g => g != selected)) goal.Enabled = false; }
            finally { _selectingDefaultGoal = false; }
        }
        _revision++; HasUnsavedChanges = IsDraftChanged(); SetStatus(null); Error = null;
    }
    [RelayCommand] private void AddGoalTemplate(string? key)
    {
        if (key is null || Goals.Count >= 32) return;
        AddGoalDefinition(BuiltInAgentGoals.Create(key));
    }
    private void AddGoalDefinition(AgentGoal definition)
    {
        var goal = new AgentGoalViewModel(definition);
        goal.PropertyChanged += GoalsChanged; Goals.Add(goal); SelectedGoal = goal; GoalsChanged(this, new(null));
    }
    [RelayCommand] private void AddGoal()
    {
        if (Goals.Count >= 32) return;
        AddGoalDefinition(new(Guid.NewGuid(), "", false));
    }
    [RelayCommand] private void DeleteGoal()
    {
        if (SelectedGoal is not { } goal) return;
        goal.PropertyChanged -= GoalsChanged; Goals.Remove(goal); SelectedGoal = Goals.FirstOrDefault(); GoalsChanged(this, new(null));
    }
    [ObservableProperty] private string _commands = "";
    [ObservableProperty] private int _providerIndex;
    [ObservableProperty] private string _apiKey = "";
    [ObservableProperty] private bool _forgetKey;
    [ObservableProperty] private bool _jsonMode;
    [ObservableProperty] private decimal? _maxDecisions;
    [ObservableProperty] private decimal? _maxRunSeconds;
    [ObservableProperty] private decimal? _actionIntervalSeconds;
    [ObservableProperty] private decimal? _responseTimeoutSeconds;
    [ObservableProperty] private decimal? _maxInputCharacters;
    [ObservableProperty] private decimal? _maxOutputTokens;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private IReadOnlyList<string> _models = [];
    [ObservableProperty] private string? _selectedModel;
    [ObservableProperty] private bool _hasUnsavedChanges;
    public bool HasError => !string.IsNullOrEmpty(Error);
    public string Status => _statusKey is null ? "" : L.ResourceManager.GetString(_statusKey, UiLanguage.Culture) ?? "";
    private static readonly string[] ProviderKeys = ["openai-compatible", "lmstudio-native"];
    public IReadOnlyList<LocalizedChoiceViewModel> Providers { get; } = [new(nameof(L.AgentOpenAiCompatible)), new(nameof(L.AgentLmStudioNative))];
    public bool SupportsJsonMode => ProviderIndex == 0;
    public string EndpointHelp => ProviderIndex == 1 ? L.AgentNativeEndpointHelp : L.AgentCompatibleEndpointHelp;
    private static string WithoutApiPath(string value)
    {
        var address = value.Trim().TrimEnd('/');
        foreach (var suffix in new[] { "/api/v1", "/v1" })
            if (address.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return address[..^suffix.Length];
        return address;
    }
    private static string ComposeEndpoint(string address, int provider)
    {
        address = WithoutApiPath(address ?? "");
        if (string.IsNullOrWhiteSpace(address)) return "";
        if (!address.Contains("://", StringComparison.Ordinal)) address = "http://" + address;
        return address + (provider == 1 ? "/api/v1" : "/v1");
    }
    partial void OnServerAddressChanged(string value)
    {
        if (_updatingAddress) return;
        _updatingAddress = true;
        try { Endpoint = ComposeEndpoint(value, ProviderIndex); }
        finally { _updatingAddress = false; }
    }
    partial void OnEndpointChanged(string value)
    {
        if (_updatingAddress) return;
        _updatingAddress = true;
        try { ServerAddress = WithoutApiPath(value ?? ""); }
        finally { _updatingAddress = false; }
    }
    partial void OnProviderIndexChanged(int value)
    {
        OnPropertyChanged(nameof(SupportsJsonMode)); OnPropertyChanged(nameof(EndpointHelp));
        if (_loading) return;
        if (!SupportsJsonMode) JsonMode = false;
        Endpoint = ComposeEndpoint(ServerAddress, value);
    }

    public AgentProfileViewModel(string worldKey, IAgentClientServices services, AgentProfile? initialDraft = null)
    {
        _worldKey = worldKey; _services = services; _saved = initialDraft ?? services.Profiles.Load(worldKey);
        ProviderIndex = Array.IndexOf(ProviderKeys, _saved.Provider);
        Endpoint = _saved.Endpoint; Model = _saved.Model; SystemPrompt = _saved.SystemPrompt;
        foreach (var definition in AgentGoals.FromProfile(_saved))
        {
            var goal = new AgentGoalViewModel(definition); goal.PropertyChanged += GoalsChanged; Goals.Add(goal);
        }
        SelectedGoal = Goals.FirstOrDefault(); Commands = _saved.Commands; JsonMode = _saved.JsonMode;
        MaxDecisions = _saved.MaxDecisions; MaxRunSeconds = _saved.MaxRunSeconds;
        ActionIntervalSeconds = (decimal)_saved.ActionIntervalSeconds; ResponseTimeoutSeconds = (decimal)_saved.ResponseTimeoutSeconds;
        MaxInputCharacters = _saved.MaxInputCharacters; MaxOutputTokens = _saved.MaxOutputTokens;
        _loading = false;
        _baselineDraft = Snapshot();
        PropertyChanged += DraftChanged;
        UiLanguage.Changed += LanguageChanged;
        ScheduleDiscovery();
    }

    private void DraftChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (_loading || _disposed) return;
        if (args.PropertyName is nameof(Endpoint) or nameof(Model) or nameof(SystemPrompt)
            or nameof(Commands) or nameof(ProviderIndex) or nameof(ApiKey) or nameof(ForgetKey) or nameof(JsonMode)
            or nameof(MaxDecisions) or nameof(MaxRunSeconds) or nameof(ActionIntervalSeconds)
            or nameof(ResponseTimeoutSeconds) or nameof(MaxInputCharacters) or nameof(MaxOutputTokens))
        {
            _revision++; HasUnsavedChanges = IsDraftChanged(); SetStatus(null); Error = null; _discoveryErrorKey = null;
            if (args.PropertyName is nameof(Endpoint) or nameof(ProviderIndex) or nameof(ApiKey) or nameof(ForgetKey))
            {
                _discovery?.Cancel(); Models = []; SelectedModel = null; IsDiscovering = false;
                ScheduleDiscovery();
            }
        }
    }
    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));
    partial void OnApiKeyChanged(string value) { if (!_loading && value is { Length: > 0 }) ForgetKey = false; }
    partial void OnForgetKeyChanged(bool value) { if (!_loading && value) ApiKey = ""; }
    partial void OnSelectedModelChanged(string? value) { if (value is not null) Model = value; }
    private void SetStatus(string? key) { _statusKey = key; OnPropertyChanged(nameof(Status)); }
    private void LanguageChanged()
    {
        foreach (var provider in Providers) provider.Refresh();
        OnPropertyChanged(nameof(Status)); OnPropertyChanged(nameof(EndpointHelp));
        if (_discoveryErrorKey is not null) Error = L.Format(L.ResourceManager.GetString(_discoveryErrorKey, UiLanguage.Culture) ?? L.AgentDiscoveryFailed, _discoveryStatusCode);
    }
    private void ScheduleDiscovery()
    {
        _automaticDiscovery?.Cancel();
        var cancellation = new CancellationTokenSource();
        _automaticDiscovery = cancellation;
        _ = DiscoverAfterDelayAsync(cancellation);
    }
    private async Task DiscoverAfterDelayAsync(CancellationTokenSource cancellation)
    {
        using (cancellation)
        {
            try
            {
                await Task.Delay(450, cancellation.Token);
                if (_disposed || cancellation.IsCancellationRequested) return;
                if (ReferenceEquals(_automaticDiscovery, cancellation)) _automaticDiscovery = null;
                await DiscoverModelsAsync();
            }
            catch (OperationCanceledException) { }
            finally { if (ReferenceEquals(_automaticDiscovery, cancellation)) _automaticDiscovery = null; }
        }
    }
    private bool CanAct() => !_disposed;

    private AgentProfile Draft()
    {
        if (ProviderIndex < 0 || ProviderIndex >= ProviderKeys.Length) throw new ArgumentException(L.AgentInvalidSettings);
        static int Integer(decimal? value) => value is { } n && n == decimal.Truncate(n) && n >= int.MinValue && n <= int.MaxValue
            ? (int)n : throw new ArgumentException(L.AgentInvalidSettings);
        return _saved with
        {
            Provider = ProviderKeys[ProviderIndex], Endpoint = Endpoint?.Trim() ?? "", Model = Model?.Trim() ?? "",
            SystemPrompt = SystemPrompt ?? "", DefaultGoal = "", Goals = Goals.Select(g => g.ToDefinition()).ToArray(), Commands = Commands ?? "", JsonMode = SupportsJsonMode && JsonMode,
            MaxDecisions = Integer(MaxDecisions), MaxRunSeconds = Integer(MaxRunSeconds),
            ActionIntervalSeconds = Integer(ActionIntervalSeconds),
            ResponseTimeoutSeconds = Integer(ResponseTimeoutSeconds),
            MaxInputCharacters = Integer(MaxInputCharacters), MaxOutputTokens = Integer(MaxOutputTokens)
        };
    }

    private string Snapshot() => JsonSerializer.Serialize(new
    {
        ProviderIndex, Endpoint, Model, SystemPrompt, Commands, JsonMode, MaxDecisions, MaxRunSeconds,
        ActionIntervalSeconds, ResponseTimeoutSeconds, MaxInputCharacters, MaxOutputTokens,
        Goals = Goals.Select(g => g.ToDefinition()).ToArray()
    });
    private bool IsDraftChanged() => !string.IsNullOrEmpty(ApiKey) || ForgetKey || Snapshot() != _baselineDraft;

    public void ValidateDraft()
    {
        var draft = Draft(); AgentConfiguration.Validate(draft); AgentCatalog.Parse(draft.Commands);
    }

    public async Task SaveDraftAsync(string worldKey)
    {
        _worldKey = worldKey;
        await SaveAsync();
        if (HasError) throw new InvalidOperationException(Error);
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task SaveAsync()
    {
        if (_disposed) return;
        Error = null; _discoveryErrorKey = null;
        try
        {
            var draft = Draft(); AgentConfiguration.Validate(draft); AgentCatalog.Parse(draft.Commands);
            var revision = _revision;
            var key = ApiKey ?? "";
            var forget = ForgetKey;
            var saved = await _services.SaveAsync(_worldKey, draft, key, forget);
            if (_disposed) return;
            _saved = saved;
            _loading = true;
            try { if (ApiKey == key) ApiKey = ""; if (ForgetKey == forget) ForgetKey = false; }
            finally { _loading = false; }
            if (revision == _revision) _baselineDraft = Snapshot();
            HasUnsavedChanges = revision != _revision;
            SetStatus(nameof(L.AgentSettingsSaved));
        }
        catch (ArgumentException) { if (!_disposed) Error = L.AgentInvalidSettings; }
        catch (Exception) { if (!_disposed) Error = L.AgentSaveFailed; }
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task DiscoverModelsAsync()
    {
        if (_disposed) return;
        _automaticDiscovery?.Cancel();
        _discovery?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _discovery = cancellation;
        Error = null; _discoveryErrorKey = null; IsDiscovering = true; SetStatus(nameof(L.AgentDiscovering));
        try
        {
            if (ProviderIndex < 0 || ProviderIndex >= ProviderKeys.Length) throw new ArgumentException();
            // Discovery depends only on connection settings, not unfinished prompt/command edits.
            var draft = _saved with { Provider = ProviderKeys[ProviderIndex], Endpoint = Endpoint, Model = "", ResponseTimeoutSeconds = Math.Min(_saved.ResponseTimeoutSeconds, 10) };
            AgentConfiguration.Validate(draft, requireModel: false);
            string? key = string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey;
            if (key is null && !ForgetKey && draft.Endpoint == _saved.Endpoint && draft.Provider == _saved.Provider)
                key = await _services.ReadCredentialAsync(_saved).WaitAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            var results = await _services.Providers.Resolve(draft.Provider).ListModelsAsync(draft, key, cancellation.Token).WaitAsync(cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested || !ReferenceEquals(_discovery, cancellation)) return;
            Models = results;
            if (results.Contains(Model, StringComparer.Ordinal)) SelectedModel = Model;
            else if (string.IsNullOrWhiteSpace(Model) && results.Count == 1) SelectedModel = results[0];
            SetStatus(results.Count == 0 ? nameof(L.AgentNoModels) : nameof(L.AgentModelsLoaded));
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!_disposed && !cancellation.IsCancellationRequested && ReferenceEquals(_discovery, cancellation))
            {
                _discoveryStatusCode = error is HttpRequestException { StatusCode: { } code } ? (int)code : 0;
                _discoveryErrorKey = error switch
                {
                    HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => nameof(L.AgentDiscoveryUnauthorized),
                    HttpRequestException { StatusCode: HttpStatusCode.NotFound } or InvalidDataException => nameof(L.AgentDiscoveryWrongApi),
                    HttpRequestException { StatusCode: not null } => nameof(L.AgentDiscoveryServerError),
                    HttpRequestException => nameof(L.AgentDiscoveryUnreachable),
                    TimeoutException => nameof(L.AgentDiscoveryTimedOut),
                    ArgumentException => nameof(L.AgentServerAddressInvalid),
                    _ => nameof(L.AgentDiscoveryFailed)
                };
                Error = L.Format(L.ResourceManager.GetString(_discoveryErrorKey, UiLanguage.Culture) ?? L.AgentDiscoveryFailed, _discoveryStatusCode); SetStatus(null);
            }
        }
        finally { if (ReferenceEquals(_discovery, cancellation)) { _discovery = null; IsDiscovering = false; } }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _automaticDiscovery?.Cancel(); _discovery?.Cancel(); ApiKey = "";
        foreach (var goal in Goals) goal.PropertyChanged -= GoalsChanged;
        PropertyChanged -= DraftChanged; UiLanguage.Changed -= LanguageChanged;
        SaveCommand.NotifyCanExecuteChanged(); DiscoverModelsCommand.NotifyCanExecuteChanged();
    }
}
