using Dock.Model.Core;
using Wandur.Desktop.ViewModels;

namespace Wandur.Desktop;

public sealed partial class WorkspaceFactory
{
    internal const string SearchKey = "search";
    private WorkspaceNavigationViewModel? _navigation;
    private bool _selectingEditor;
    internal object SelectedKey { get; private set; } = SearchKey;
    public WorkspaceNavigationViewModel Navigation => _navigation ??= new(sessions, this);

    public void ShowSearch() => sessions.Browse();

    private void SessionSelected()
    {
        if (_selectingEditor) return;
        SelectedKey = sessions.IsBrowsing ? SearchKey : sessions.Active;
        if (_sessionDocument is { Owner: IDock owner } document)
        {
            SetActiveDockable(document);
            SetFocusedDockable(owner, document);
        }
        Navigation.Refresh();
    }

    private void EditorSelected(IDockable? document, WorkspaceController controller)
    {
        if (document is null) return;
        _selectingEditor = true;
        try
        {
            var tab = sessions.Tabs.FirstOrDefault(t => ReferenceEquals(t.Controller, controller));
            if (tab is not null && (sessions.Active != tab || sessions.IsBrowsing)) sessions.Select(tab);
            SelectedKey = document;
        }
        finally { _selectingEditor = false; }
        Navigation.Refresh();
    }

    internal void Navigate(object key)
    {
        if (Equals(key, SearchKey)) ShowSearch();
        else if (key is SessionTab tab) sessions.Select(tab);
        else if (key is IDockable document)
        {
            SetActiveDockable(document);
            document.OnSelected();
            if (document.Owner is IDock owner) SetFocusedDockable(owner, document);
        }
    }

    internal async Task CloseEntryAsync(object key)
    {
        if (key is SessionTab tab) await sessions.CloseAsync(tab);
        else if (key is IDockable document)
        {
            var wasSelected = ReferenceEquals(SelectedKey, key);
            CloseDockable(document);
            if (wasSelected) SessionSelected();
        }
        else if (sessions.Active.Controller.HasSession) sessions.Select(sessions.Active);
        Navigation.Refresh();
    }

    public Task CloseSelectedAsync() => CloseEntryAsync(SelectedKey);

    public void SelectNextDocument(int direction)
    {
        Navigation.Refresh();
        var entries = Navigation.Entries;
        if (entries.Count == 0) return;
        var current = entries.ToList().FindIndex(e => Equals(e.Key, SelectedKey));
        Navigate(entries[(current + direction + entries.Count) % entries.Count].Key);
    }

    public void DetachNavigation()
    {
        sessions.SelectionChanged -= SessionSelected;
        sessions.ScriptPanelsChanged -= SyncScriptPanels;
        _navigation?.Detach();
    }
}
