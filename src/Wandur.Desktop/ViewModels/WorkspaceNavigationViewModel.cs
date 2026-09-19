using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Core;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.ViewModels;

public sealed partial class WorkspaceNavigationEntry : ObservableObject
{
    public required object Key { get; init; }
    public required IRelayCommand OpenCommand { get; init; }
    public required IAsyncRelayCommand CloseCommand { get; init; }
    public required IRelayCommand RenameCommand { get; init; }
    public bool CanRename => Key is SessionTab;
    public required IRelayCommand FloatCommand { get; init; }
    public bool CanClose => Key is not string;
    public bool CanFloat => Key is IDockable { CanFloat: true };
    public bool IsChild => Key is IDockable;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _details = "";
    [ObservableProperty] private string _status = "";
    public bool HasDetails => Key is SessionTab;
}

/// <summary>Stable navigation rows. Switching pages does not own or dispose a connection.</summary>
public sealed partial class WorkspaceNavigationViewModel(SessionWorkspace sessions, WorkspaceFactory workspace) : ObservableObject
{
    public ObservableCollection<WorkspaceNavigationEntry> Entries { get; } = [];
    public ObservableCollection<WorkspaceNavigationEntry> OpenEntries { get; } = [];
    [ObservableProperty] private SessionTab? _renameTarget;
    [ObservableProperty] private string _renameText = "";
    public bool IsSearchSelected => sessions.IsBrowsing && Equals(workspace.SelectedKey, WorkspaceFactory.SearchKey);
    public bool IsRenaming => RenameTarget is not null;
    partial void OnRenameTargetChanged(SessionTab? value) => OnPropertyChanged(nameof(IsRenaming));
    [RelayCommand] private void Browse() => workspace.ShowSearch();
    [RelayCommand] private void SaveName()
    {
        if (RenameTarget is { } tab) sessions.Rename(tab, RenameText);
        RenameTarget = null;
    }
    [RelayCommand] private void CancelRename() => RenameTarget = null;
    private void Rename(object key)
    {
        if (key is not SessionTab tab) return;
        RenameText = tab.CustomName ?? tab.Profile?.Username ?? "";
        RenameTarget = tab;
    }
    [ObservableProperty] private WorkspaceNavigationEntry? _selected;
    private bool _refreshing;
    private bool _attached;

    partial void OnSelectedChanged(WorkspaceNavigationEntry? value)
    {
        if (!_refreshing && value is not null) workspace.Navigate(value.Key);
    }

    public void Attach()
    {
        if (_attached) return;
        _attached = true;
        sessions.Changed += Refresh;
        Wandur.Core.Localization.UiLanguage.Changed += Refresh;
        Refresh();
    }

    public void Detach()
    {
        if (!_attached) return;
        _attached = false;
        sessions.Changed -= Refresh;
        Wandur.Core.Localization.UiLanguage.Changed -= Refresh;
    }

    public void Refresh()
    {
        SessionOpenTrace.Count("navigation refresh");
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var desired = new List<(object Key, string Title, string Details, string Status)>
                { (WorkspaceFactory.SearchKey, L.FindAMUD, "", "⌕") };
            var tabs = sessions.Tabs.Where(t => t.Controller.HasSession && !t.IsClosing).ToArray();
            foreach (var tab in tabs)
            {
                var login = tab.Profile?.Username;
                var siblings = tabs.Where(t => t.Controller.WorldName == tab.Controller.WorldName && t.Profile?.Username == login).ToArray();
                var title = tab.CustomName ?? tab.Controller.WorldName +
                    (!string.IsNullOrWhiteSpace(login) ? $" · {login}" : "") +
                    (siblings.Length > 1 ? $" · {Array.IndexOf(siblings, tab) + 1}" : "");
                desired.Add((tab, title, tab.Endpoint, tab.Controller.IsConnected ? "●" : "○"));
                foreach (var map in workspace.MapDocuments.Where(d => !d.IsClosed && d.Controller == tab.Controller))
                    desired.Add((map, L.MapEditor, "", ""));

            }
            foreach (var old in Entries.Where(e => !desired.Any(d => Equals(d.Key, e.Key))).ToArray()) Entries.Remove(old);
            for (var i = 0; i < desired.Count; i++)
            {
                var item = desired[i];
                var row = Entries.FirstOrDefault(e => Equals(e.Key, item.Key));
                if (row is null)
                {
                    var key = item.Key;
                    row = new WorkspaceNavigationEntry
                    {
                        Key = key,
                        OpenCommand = new RelayCommand(() => workspace.Navigate(key)),
                        CloseCommand = new AsyncRelayCommand(() => workspace.CloseEntryAsync(key)),
                        RenameCommand = new RelayCommand(() => Rename(key)),
                        FloatCommand = new RelayCommand(() => { if (key is IDockable doc) workspace.FloatDockable(doc); })
                    };
                    Entries.Insert(i, row);
                }
                else if (Entries.IndexOf(row) != i) Entries.Move(Entries.IndexOf(row), i);
                row.Title = item.Title; row.Details = item.Details;
                row.Status = item.Key is SessionTab { HasActivity: true } ? "●  " + L.NewActivity : item.Status;
            }
            foreach (var old in OpenEntries.Where(e => !Entries.Contains(e)).ToArray()) OpenEntries.Remove(old);
            for (var i = 1; i < Entries.Count; i++)
            {
                var row = Entries[i];
                if (!OpenEntries.Contains(row)) OpenEntries.Insert(i - 1, row);
                else if (OpenEntries.IndexOf(row) != i - 1) OpenEntries.Move(OpenEntries.IndexOf(row), i - 1);
            }
            if (RenameTarget is { } target && !sessions.Tabs.Contains(target)) RenameTarget = null;
            Selected = Entries.FirstOrDefault(e => Equals(e.Key, workspace.SelectedKey)) ??
                Entries.FirstOrDefault(e => Equals(e.Key, sessions.IsBrowsing ? WorkspaceFactory.SearchKey : sessions.Active));
        }
        finally { _refreshing = false; OnPropertyChanged(nameof(IsSearchSelected)); }
    }
}
