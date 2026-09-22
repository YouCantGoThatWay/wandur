using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wandur.Core.Discovery;
using Wandur.Core.Settings;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.ViewModels;

public sealed record WorldBrowserQuery
{
    public string Search { get; init; } = "";
    public int Connection { get; init; }
    public bool OnlineOnly { get; init; }
    public int Sort { get; init; }
    public IReadOnlyDictionary<string, string> Facets { get; init; } = new Dictionary<string, string>();
    public decimal? MinimumPlayers { get; init; }
    public decimal? MaximumPlayers { get; init; }
    public int Rating { get; init; }
    public bool TlsOnly { get; init; }
}

public sealed record WorldBrowserFacet(string Key, string LabelKey, Func<WorldListing, IEnumerable<string>> Values)
{
    public string Label => LocalizedText.Instance[LabelKey];
}

/// <summary>Directory browsing and saved-world decisions, independent of window controls and artwork.</summary>
public sealed partial class WorldBrowserViewModel : ObservableObject, IDisposable
{
    private readonly WorldCatalog _catalog;
    private readonly SessionWorkspace _sessions;
    private CancellationTokenSource _lifetime = new();
    private Action<Action> _dispatch = action => action();
    private bool _attached;
    private bool _disposed;
    [ObservableProperty] private WorldBrowserQuery _query = new();
    [ObservableProperty] private IReadOnlyList<WorldListing> _results = [];
    [ObservableProperty] private WorldListing? _selectedWorld;
    [ObservableProperty] private bool _useTls;
    [ObservableProperty] private string _count = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _feedback = "";
    [ObservableProperty] private string _filterSummary = "";
    [ObservableProperty] private string _filterHint = "";
    [ObservableProperty] private string _emptyTitle = "";
    [ObservableProperty] private string _emptyDescription = "";
    [ObservableProperty] private bool _canRefresh = true;
    [ObservableProperty] private IReadOnlyDictionary<string, string[]> _facetOptions = new Dictionary<string, string[]>();
    // Transient view state, retained when the center page is hidden or rebuilt.
    internal string? ScrollWorldId { get; set; }
    internal double DetailScrollOffset { get; set; }
    internal double ResultsScrollOffset { get; set; }
    public IReadOnlyList<WorldBrowserFacet> Facets { get; } =
    [
        new("Theme", nameof(L.Theme), w => [w.Features.Theme]),
        new("Kind", nameof(L.GameType), w => [w.Features.Kind]),
        new("Language", nameof(L.Language), w => [w.Features.Language]),
        new("Roleplaying", nameof(L.Roleplaying), w => [w.Features.Roleplaying]),
        new("PlayerKilling", nameof(L.PlayerKilling), w => [w.Features.PlayerKilling]),
        new("Codebase", nameof(L.Codebase), w => [w.Features.Codebase]),
        new("Development", nameof(L.DevelopmentStage), w => [w.Features.DevelopmentStatus]),
        new("WorldSize", nameof(L.WorldSizeRooms), w => [w.Features.WorldSize]),
        new("Location", nameof(L.ServerLocation), w => [w.Features.Location]),
        new("Tag", nameof(L.FeatureTag), w => w.Tags)
    ];

    public WorldBrowserViewModel(WorldCatalog catalog, SessionWorkspace sessions)
    {
        _catalog = catalog; _sessions = sessions;
        RefreshCatalog();
    }

    public void Attach(Action<Action>? dispatch = null)
    {
        if (_attached || _disposed) return;
        if (_lifetime.IsCancellationRequested) { _lifetime.Dispose(); _lifetime = new(); }
        _dispatch = dispatch ?? (action => action());
        _catalog.Changed += CatalogChanged;
        Wandur.Core.Localization.UiLanguage.Changed += CatalogChanged;
        _attached = true;
        RefreshCatalog();
    }
    public void Detach()
    {
        if (_disposed || !_attached) return;
        _catalog.Changed -= CatalogChanged;
        Wandur.Core.Localization.UiLanguage.Changed -= CatalogChanged;
        _attached = false;
        _lifetime.Cancel();
    }
    private void CatalogChanged() => _dispatch(() => { if (_attached && !_disposed) RefreshCatalog(); });
    private void RefreshCatalog()
    {
        CanRefresh = !_catalog.Loading;
        Status = _catalog.Loading ? L.LoadingTheDirectoryTheFirstFullDownloadMayTake :
            _catalog.Warning ?? (_catalog.FetchedAt is { } time ? L.Format(L.DirectorySavedSearchWorksOfflineRefreshedEvery24Hours, time.ToLocalTime()) : L.StartTheLocalDirectoryServerToDiscoverWorlds);
        EmptyTitle = _catalog.Worlds.Count == 0 ? L.AWorldOfPossibilities : L.NoWorldsFound;
        EmptyDescription = _catalog.Worlds.Count == 0 ? L.YourDirectoryWillAppearHereWhenItIsAvailable : L.TryRemovingAFilterOrShorteningYourSearchAll;
        FacetOptions = Facets.ToDictionary(f => f.Key, f => _catalog.Worlds.SelectMany(f.Values)
            .Append(Query.Facets.GetValueOrDefault(f.Key) ?? "").Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToArray());
        RefreshResults();
    }
    partial void OnQueryChanged(WorldBrowserQuery value) => RefreshResults();
    partial void OnCanRefreshChanged(bool value) => RefreshCommand.NotifyCanExecuteChanged();
    // Highlighting a listing never themes the window: a world theme arrives with its session.
    partial void OnSelectedWorldChanged(WorldListing? value)
    {
        Feedback = "";
        UseTls = value?.Port is null && value?.TlsPort is not null;
        SaveCommand.NotifyCanExecuteChanged(); ConnectCommand.NotifyCanExecuteChanged();
    }
    private void RefreshResults()
    {
        Feedback = "";
        var id = SelectedWorld?.Id;
        var matches = _catalog.Search(Query.Search)
            .Where(w => Query.Connection switch { 1 => w.CanConnect, 2 => w.WebOnly, _ => true })
            .Where(w => !Query.OnlineOnly || w.Availability.Online == true && w.Availability.Archived != true)
            .Where(MatchesAdvanced);
        var results = SortResults(matches).ToArray();
        SelectedWorld = results.FirstOrDefault(w => w.Id == id) ?? results.FirstOrDefault();
        Count = L.Format(L.OfWorlds, results.Length, _catalog.Worlds.Count);
        var count = Query.Facets.Count + (Query.MinimumPlayers.HasValue ? 1 : 0) + (Query.MaximumPlayers.HasValue ? 1 : 0)
            + (Query.Rating > 0 ? 1 : 0) + (Query.TlsOnly ? 1 : 0);
        FilterSummary = count == 0 ? L.AdvancedSearchFindYourKindOfWorld : L.Format(count == 1 ? L.FiltersOne : L.FiltersMany, count);
        FilterHint = Query.MinimumPlayers > Query.MaximumPlayers ? L.MinimumPlayersExceedsMaximumAdjustTheRangeToFind : L.AllPreferencesCombinePlayerCountsAreLastObservedNot;
        Results = results;
    }
    private bool MatchesAdvanced(WorldListing world)
    {
        foreach (var facet in Facets)
            if (Query.Facets.TryGetValue(facet.Key, out var selected) && !facet.Values(world).Contains(selected, StringComparer.OrdinalIgnoreCase)) return false;
        if (Query.TlsOnly && (!world.CanConnect || world.TlsPort is not > 0)) return false;
        if (Query.MinimumPlayers is { } min && (world.Population.LatestCount is not { } count || count < min)) return false;
        if (Query.MaximumPlayers is { } max && (world.Population.LatestCount is not { } maximumCount || maximumCount > max)) return false;
        var minimumRating = Query.Rating switch { 1 => 3m, 2 => 4m, 3 => 4.5m, _ => (decimal?)null };
        return minimumRating is null || world.Community.RatingCount is > 0 && world.Community.Rating >= minimumRating;
    }
    private IEnumerable<WorldListing> SortResults(IEnumerable<WorldListing> matches) => Query.Sort switch
    {
        1 => matches.OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase),
        2 => matches.OrderByDescending(w => w.Population.LatestCount).ThenBy(w => w.Name, StringComparer.OrdinalIgnoreCase),
        3 => matches.OrderByDescending(w => w.Community.RatingCount is > 0 ? w.Community.Rating : null)
            .ThenByDescending(w => w.Community.RatingCount).ThenBy(w => w.Name, StringComparer.OrdinalIgnoreCase),
        4 => matches.OrderByDescending(w => w.Source.UpdatedAt).ThenBy(w => w.Name, StringComparer.OrdinalIgnoreCase),
        5 => matches.OrderByDescending(w => w.EstablishedAt).ThenBy(w => w.Name, StringComparer.OrdinalIgnoreCase),
        _ => matches
    };
    [RelayCommand] private void ResetFilters() => Query = new();
    [RelayCommand(CanExecute = nameof(CanRefresh))] private Task RefreshAsync() => LoadAsync(true);
    public async Task LoadAsync(bool force = false)
    {
        try { await _catalog.LoadAsync(force, _lifetime.Token); }
        catch (OperationCanceledException) { }
    }
    private bool CanSave() => SelectedWorld?.CanConnect == true;
    [RelayCommand(CanExecute = nameof(CanSave))] private void Save() => SaveSelectedWorld();
    public ConnectionProfile? SaveSelectedWorld()
    {
        if (SelectedWorld is not { } world) return null;
        try
        {
            var profile = world.ToProfile(UseTls);
            var controller = _sessions.Active.Controller;
            var existing = controller.Settings.Profiles.FirstOrDefault(p => p.Host.Equals(profile.Host, StringComparison.OrdinalIgnoreCase) && p.Port == profile.Port && p.UseTls == profile.UseTls);
            if (existing is not null)
            {
                var updated = existing with { Theme = profile.Theme ?? existing.Theme, ProtocolMapping = profile.GetProtocolMapping() ?? existing.GetProtocolMapping() };
                if (updated != existing)
                {
                    try { controller.SaveSettings(controller.Settings with
                        { Profiles = controller.Settings.Profiles.Select(p => p.Id == existing.Id ? updated : p).ToList() }); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Data.Common.DbException)
                    { Feedback = L.WorldThemeCouldNotBeSaved; return updated; }
                }
                Feedback = L.AlreadyInYourWorlds; return updated == existing ? existing : updated;
            }
            controller.SaveSettings(controller.Settings with { Profiles = [..controller.Settings.Profiles, profile] });
            Feedback = L.Format(L.AddedToYourWorlds, world.Name);
            return profile;
        }
        catch (Exception ex) { Feedback = ex.Message; return null; }
    }
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task ConnectAsync()
    {
        if (SaveSelectedWorld() is not { } profile) return;
        await _sessions.OpenAsync(profile);
    }
    public void ReportLinkFailure() => Feedback = L.CouldNotOpenTheLinkInYourBrowser;
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_attached) _catalog.Changed -= CatalogChanged;
        Wandur.Core.Localization.UiLanguage.Changed -= CatalogChanged;
        _lifetime.Cancel(); _lifetime.Dispose();
    }
}
