using Wandur.Core.Storage;
using Wandur.Desktop.Services;
using Wandur.Core.Scripting;
using Wandur.Core.Mapping;
using L = Wandur.Core.Localization.Strings;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Wandur.Core.Settings;
using Wandur.Desktop.Security;

namespace Wandur.Desktop;

public sealed class SessionTab(WorkspaceController controller) : INotifyPropertyChanged
{
    public Guid Id { get; } = Guid.NewGuid();
    public string? CustomName { get; internal set; }
    public WorkspaceController Controller { get; } = controller;
    public bool HasActivity { get; internal set; }
    public ConnectionProfile? Profile { get; internal set; }
    public bool IsClosing { get; internal set; }
    internal int LastVersion { get; set; }
    internal bool HadSession { get; set; }
    /// <summary>Whether the current connection has already been counted; cleared while the tab is disconnected.</summary>
    internal bool ConnectionCounted { get; set; }
    /// <summary>The character name last written to the usage store for this tab, so a name is stored once.</summary>
    internal string? RecordedCharacter { get; set; }
    public string Endpoint => Controller.HasSession ? Controller.Endpoint : L.FindAMUDInTheDirectory;
    /// <summary>The world and, once known, the character: "Legends of the Jedi · Talek". A custom name replaces both.</summary>
    public string Title => (HasActivity ? "●  " : "") + (Controller.HasSession ? CustomName ?? Controller.SessionLabel : L.FindAMUD);
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Refresh()
    {
        PropertyChanged?.Invoke(this, new(nameof(Title)));
        PropertyChanged?.Invoke(this, new(nameof(Endpoint)));
    }
}

/// <summary>Owns independent connections and the currently selected session.</summary>
public sealed partial class SessionWorkspace : IAsyncDisposable
{
    private readonly ISettingsStore _store;
    private readonly IAgentClientServices? _agents;
    private readonly IPasswordVault _passwords;
    private readonly IRoomMapStore _maps;
    private readonly IScriptRuntimeFactory _scriptRuntimes;
    private readonly IWorldScriptLibraryStore _scriptLibraryStore;
    private readonly IWorldKnowledgeStore? _knowledge;
    private readonly Wandur.Core.Discovery.WorldCatalog? _catalog;
    private readonly Wandur.Core.Classification.RoomClassificationService? _classification;
    private readonly IWorldUsageStore? _usage;
    private bool _disposed;
    private readonly Wandur.Desktop.Terminal.ITranscriptDisplayFactory _displays;
    public ObservableCollection<SessionTab> Tabs { get; } = [];
    public SessionTab Active { get; private set; }
    public event Action? Changed;
    public event Action? SelectionChanged;
    /// <summary>Raised when any session's scripts added, changed or removed a docked panel.</summary>
    public event Action? ScriptPanelsChanged;
    /// <summary>Raised after a session's connection has been counted, so the saved worlds list can reorder itself.</summary>
    public event Action? UsageChanged;
    /// <summary>The directory, when there is one: the saved worlds list finds its artwork through it.</summary>
    public Wandur.Core.Discovery.WorldCatalog? Catalog => _catalog;
    /// <summary>Where connections are counted, when the client has a database to count them in.</summary>
    public IWorldUsageStore? Usage => _usage;
    private WorldThumbnails? _thumbnails;
    /// <summary>The saved worlds' small pictures, kept here so a rebuilt dock does not decode them again.</summary>
    public WorldThumbnails? Thumbnails => _disposed || _catalog is null ? null : _thumbnails ??= new(_catalog);
    public bool IsBrowsing { get; private set; } = true;
    private ViewModels.WorldBrowserViewModel? _browser;
    internal ViewModels.WorldBrowserViewModel Browser(Wandur.Core.Discovery.WorldCatalog catalog)
        => _browser ??= new(catalog, this);

    public void Browse()
    {
        if (_disposed) return;
        IsBrowsing = true;
        SelectionChanged?.Invoke();
        Changed?.Invoke();
    }

    public SessionWorkspace(Wandur.Desktop.Terminal.ITranscriptDisplayFactory displays, ISettingsStore store, IPasswordVault passwords, IRoomMapStore maps, IScriptRuntimeFactory scriptRuntimes, IWorldScriptLibraryStore scriptLibraryStore, IWorldKnowledgeStore? knowledge = null, Wandur.Core.Discovery.WorldCatalog? catalog = null, IAgentClientServices? agents = null, Wandur.Core.Classification.RoomClassificationService? classification = null, IWorldUsageStore? usage = null)
    {
        _classification = classification; _agents = agents; _displays = displays; _store = store; _knowledge = knowledge; _catalog = catalog; _usage = usage;
        _passwords = passwords;
        _maps = maps;
        _scriptRuntimes = scriptRuntimes;
        _scriptLibraryStore = scriptLibraryStore;
        Active = CreateTab();
        Tabs.Add(Active);
        ApplyAppearance();
        if (_catalog is not null) _catalog.Changed += CatalogAppearanceChanged;
    }

    private SessionTab CreateTab()
    {
        var controller = new WorkspaceController(_displays, _store, _passwords, _maps, _scriptRuntimes, _scriptLibraryStore, _knowledge, _agents, _classification);
        var tab = new SessionTab(controller);
        controller.Changed += () => OnChanged(tab);
        controller.ScriptLibrary.Panels.Changed += () => { if (!_disposed) ScriptPanelsChanged?.Invoke(); };
        controller.SettingsSaved += settings =>
        {
            foreach (var other in Tabs.Where(t => t != tab)) other.Controller.ApplySettings(settings);
            ApplyAppearance();
        };
        return tab;
    }

    /// <summary>Opens into a blank tab with the world theme already staged, so Select never paints the personal preset first.</summary>
    private SessionTab AcquireOpenTab(ConnectionProfile? profile)
    {
        var theme = profile?.Theme is { IsValid: true } value ? value : null;
        if (!(Active.Controller.HasSession || Active.IsClosing))
        {
            Active.Controller.StageWorldTheme(theme);
            ApplyAppearance();
            return Active;
        }
        var tab = CreateTab();
        Tabs.Add(tab);
        tab.Controller.StageWorldTheme(theme);
        Active = tab;
        IsBrowsing = false;
        ApplyAppearance();
        tab.HasActivity = false;
        tab.Refresh();
        return tab;
    }

    private static ConnectionProfile MergeCatalogListing(ConnectionProfile profile, Wandur.Core.Discovery.WorldListing listing) =>
        profile with
        {
            // A listing that carries no theme says nothing about this world's appearance, so the saved theme remains.
            Theme = listing.Theme ?? profile.Theme,
            Codebase = listing.Features.Codebase is { Length: > 0 and <= 100 } codebase && !codebase.Any(char.IsControl) ? codebase : profile.Codebase,
            ProtocolMapping = listing.MappingForEndpoint(profile.Host, profile.Port, profile.UseTls) ?? profile.GetProtocolMapping()
        };

    private bool TryCacheProfile(SessionTab tab, ConnectionProfile original, ConnectionProfile updated)
    {
        if (updated == original || !tab.Controller.Settings.Profiles.Any(p => p.Id == original.Id)) return false;
        try
        {
            tab.Controller.SaveSettings(tab.Controller.Settings with
                { Profiles = tab.Controller.Settings.Profiles.Select(p => p.Id == original.Id ? updated : p).ToList() });
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Data.Common.DbException)
        {
            return true;
        }
    }

    private void OnChanged(SessionTab tab)
    {
        if (_disposed || tab.IsClosing) return;
        SessionOpenTrace.Count("changed fan-out");
        using var trace = SessionOpenTrace.Measure("changed fan-out");
        if (!tab.HadSession && tab.Controller.HasSession && tab == Active)
        {
            IsBrowsing = false;
            SelectionChanged?.Invoke();
        }
        tab.HadSession = tab.Controller.HasSession;
        if ((tab != Active || IsBrowsing) && tab.Controller.OutputVersion != tab.LastVersion && tab.Controller.Terminal.PlainText.Length > 0)
            tab.HasActivity = true;
        tab.LastVersion = tab.Controller.OutputVersion;
        tab.Refresh();
        ApplyAppearance();
        Changed?.Invoke();
        CountConnection(tab);
        RecordCharacter(tab);
    }

    /// <summary>
    /// The character a connected session plays as is remembered on its world, once per name, so the saved worlds
    /// can name it before the next connection. A store that cannot be written keeps the session running.
    /// </summary>
    private void RecordCharacter(SessionTab tab)
    {
        if (_usage is null || tab.Profile is not { } profile || !tab.Controller.IsConnected) return;
        var name = tab.Controller.CharacterName;
        if (name.Length == 0 || name == tab.RecordedCharacter) return;
        tab.RecordedCharacter = name;
        try { _usage.RecordCharacter(profile.Host, profile.Port, name); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Data.Common.DbException) { }
    }

    /// <summary>
    /// One count per successful connection: the first connected status after an open or a reconnect, never the
    /// open itself, and never the offline demo. A store that cannot be written keeps the session running.
    /// </summary>
    private void CountConnection(SessionTab tab)
    {
        if (!tab.Controller.IsConnected) { tab.ConnectionCounted = false; return; }
        if (tab.ConnectionCounted || _usage is null || tab.Profile is not { } profile) return;
        tab.ConnectionCounted = true;
        try { _usage.RecordConnection(profile.Host, profile.Port); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Data.Common.DbException) { return; }
        if (!_disposed) UsageChanged?.Invoke();
    }

    public void Select(SessionTab tab)
    {
        if (_disposed || !Tabs.Contains(tab) || tab.IsClosing) return;
        Active = tab;
        IsBrowsing = !tab.Controller.HasSession;
        ApplyAppearance();
        tab.HasActivity = false;
        tab.Refresh();
        SelectionChanged?.Invoke();
        Changed?.Invoke();
    }

    public SessionTab NewTab()
    {
        if (_disposed) return Active;
        var tab = CreateTab();
        Tabs.Add(tab);
        Select(tab);
        return tab;
    }

    public async Task OpenAsync(ConnectionProfile? profile = null)
    {
        if (_disposed) return;
        // Prefer the theme already on the profile or in the cached directory so the shell paints correctly
        // before any network wait. A listed world still refreshes after the tab is on screen.
        Wandur.Core.Discovery.WorldListing? entry = null;
        if (profile is not null && _catalog?.FindEndpoint(profile.Host, profile.Port, profile.UseTls) is { } cached)
        {
            entry = cached;
            profile = MergeCatalogListing(profile, cached);
        }

        SessionTab tab;
        using (SessionOpenTrace.Measure("tab + controller")) tab = AcquireOpenTab(profile);
        var themeCacheFailed = false;
        var opened = profile;
        tab.Profile = opened;
        tab.ConnectionCounted = false;
        tab.RecordedCharacter = null;
        IsBrowsing = false;
        using (SessionOpenTrace.Measure("select + dock")) SelectionChanged?.Invoke();

        await tab.Controller.StartAsync(opened, entry?.SupportedScripts, beforePack: CreateOpenPackRefresh(tab, profile, () => entry, listing => entry = listing, failed => themeCacheFailed = failed));
        if (themeCacheFailed && tab.Controller.Notice is null) tab.Controller.ShowNotice(L.WorldThemeCouldNotBeSaved);
    }

    private Func<Task<IReadOnlyList<Wandur.Core.Discovery.WorldScriptListing>?>>? CreateOpenPackRefresh(
        SessionTab tab,
        ConnectionProfile? profile,
        Func<Wandur.Core.Discovery.WorldListing?> currentEntry,
        Action<Wandur.Core.Discovery.WorldListing> setEntry,
        Action<bool> setThemeCacheFailed)
    {
        if (profile is null || _catalog is null) return null;
        var catalog = _catalog;
        return async () =>
        {
            await catalog.RefreshBeforeOpenAsync(profile.Host, profile.Port, profile.UseTls);
            if (_disposed) return null;
            if (catalog.FindEndpoint(profile.Host, profile.Port, profile.UseTls) is not { } listing)
                return currentEntry()?.SupportedScripts;
            setEntry(listing);
            var updated = MergeCatalogListing(profile, listing);
            tab.Profile = updated;
            // Theme first so SaveSettings → ApplyAppearance does not briefly restore the personal preset.
            tab.Controller.ApplyCatalogProfile(updated);
            if (TryCacheProfile(tab, profile, updated)) setThemeCacheFailed(true);
            ApplyAppearance();
            return listing.SupportedScripts;
        };
    }

    public async Task CloseAsync(SessionTab tab)
    {
        if (_disposed || tab.IsClosing || !Tabs.Contains(tab)) return;
        tab.IsClosing = true;
        try
        {
            await tab.Controller.DisposeAsync();
        }
        finally
        {
            var index = Tabs.IndexOf(tab);
            // Add the replacement before removing the last tab so views always have an active session.
            if (Tabs.Count == 1) NewTab();
            if (Active == tab) Select(Tabs[index == 0 ? 1 : index - 1]);
            Tabs.Remove(tab);
            Changed?.Invoke();
        }
    }

    public void Rename(SessionTab tab, string? name)
    {
        if (_disposed || !Tabs.Contains(tab) || tab.IsClosing) return;
        var value = name?.Trim();
        if (value is { Length: > 100 } || value?.Any(char.IsControl) == true) return;
        tab.CustomName = string.IsNullOrWhiteSpace(value) ? null : value;
        tab.Refresh();
        Changed?.Invoke();
    }

    public void SelectNext(int direction)
    {
        if (Tabs.Count < 2) return;
        Select(Tabs[(Tabs.IndexOf(Active) + direction + Tabs.Count) % Tabs.Count]);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _browser?.Dispose();
        _thumbnails?.Dispose();
        DisposeThemeImages();
        if (_catalog is not null) _catalog.Changed -= CatalogAppearanceChanged;
        await Task.WhenAll(Tabs.Select(async tab => await tab.Controller.DisposeAsync()));
    }
}
