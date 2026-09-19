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
    public string Endpoint => Controller.HasSession ? Controller.Endpoint : L.FindAMUDInTheDirectory;
    public string Title => (HasActivity ? "●  " : "") + (Controller.HasSession ? CustomName ?? Controller.WorldName : L.FindAMUD);
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
    private bool _disposed;
    private readonly Wandur.Desktop.Terminal.ITranscriptDisplayFactory _displays;
    public ObservableCollection<SessionTab> Tabs { get; } = [];
    public SessionTab Active { get; private set; }
    public event Action? Changed;
    public event Action? SelectionChanged;
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

    public SessionWorkspace(Wandur.Desktop.Terminal.ITranscriptDisplayFactory displays, ISettingsStore store, IPasswordVault passwords, IRoomMapStore maps, IScriptRuntimeFactory scriptRuntimes, IWorldScriptLibraryStore scriptLibraryStore, IWorldKnowledgeStore? knowledge = null, Wandur.Core.Discovery.WorldCatalog? catalog = null, IAgentClientServices? agents = null, Wandur.Core.Classification.RoomClassificationService? classification = null)
    {
        _classification = classification; _agents = agents; _displays = displays; _store = store; _knowledge = knowledge; _catalog = catalog;
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
        using (SessionOpenTrace.Measure("catalog theme lookup"))
        if (profile is not null && _catalog?.FindEndpoint(profile.Host, profile.Port, profile.UseTls) is { } listing)
        {
            // A missing/corrupt optional catalog map must not evict the last valid endpoint-bound copy,
            // and neither must a listing that carries no theme: a directory that has stopped supplying
            // one says nothing about this world's appearance, and the saved theme is what opens offline.
            var updated = profile with { Theme = listing.Theme ?? profile.Theme,
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
        IsBrowsing = false;
        using (SessionOpenTrace.Measure("select + dock")) SelectionChanged?.Invoke();
        await tab.Controller.StartAsync(profile);
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
        DisposeThemeImages();
        if (_catalog is not null) _catalog.Changed -= CatalogAppearanceChanged;
        await Task.WhenAll(Tabs.Select(async tab => await tab.Controller.DisposeAsync()));
    }
}
