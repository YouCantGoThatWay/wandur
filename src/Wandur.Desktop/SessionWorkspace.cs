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
        // Reuse only a blank tab. Disconnected transcripts remain available until closed.
        SessionTab tab;
        using (SessionOpenTrace.Measure("tab + controller")) tab = Active.Controller.HasSession || Active.IsClosing ? NewTab() : Active;
        ApplyAppearance();
        var themeCacheFailed = false;
        // A listed world takes the directory's current scripts, theme and mapping, not a copy up to five minutes old.
        if (profile is not null && _catalog is not null)
            using (SessionOpenTrace.Measure("catalog refresh")) await _catalog.RefreshBeforeOpenAsync(profile.Host, profile.Port, profile.UseTls);
        if (_disposed) return;
        Wandur.Core.Discovery.WorldListing? entry = null;
        using (SessionOpenTrace.Measure("catalog theme lookup"))
        if (profile is not null && _catalog?.FindEndpoint(profile.Host, profile.Port, profile.UseTls) is { } listing)
        {
            entry = listing;
            // A missing/corrupt optional catalog map must not evict the last valid endpoint-bound copy,
            // and neither must a listing that carries no theme: a directory that has stopped supplying
            // one says nothing about this world's appearance, and the saved theme is what opens offline.
            var updated = profile with { Theme = listing.Theme ?? profile.Theme,
                Codebase = listing.Features.Codebase is { Length: > 0 and <= 100 } codebase && !codebase.Any(char.IsControl) ? codebase : profile.Codebase,
                ProtocolMapping = listing.MappingForEndpoint(profile.Host, profile.Port, profile.UseTls) ?? profile.GetProtocolMapping() };
            if (updated != profile && tab.Controller.Settings.Profiles.Any(p => p.Id == profile.Id))
            {
                try
                {
                    tab.Controller.SaveSettings(tab.Controller.Settings with
                        { Profiles = tab.Controller.Settings.Profiles.Select(p => p.Id == profile.Id ? updated : p).ToList() });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Data.Common.DbException)
                { themeCacheFailed = true; }
            }
            profile = updated;
        }
        tab.Profile = profile;
        tab.ConnectionCounted = false;
        tab.RecordedCharacter = null;
        IsBrowsing = false;
        using (SessionOpenTrace.Measure("select + dock")) SelectionChanged?.Invoke();
        await tab.Controller.StartAsync(profile, entry?.SupportedScripts);
        if (themeCacheFailed && tab.Controller.Notice is null) tab.Controller.ShowNotice(L.WorldThemeCouldNotBeSaved);
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
