using Avalonia;
using L = Wandur.Core.Localization.Strings;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;
using Wandur.Desktop.Views;
using Wandur.Core.Settings;
using Wandur.Desktop.ViewModels;
using Wandur.Core.Discovery;

namespace Wandur.Desktop;

public sealed class WorkspaceTool : Tool
{
    public required Func<Control> Build { get; init; }
}

public sealed class SessionDocument : Document
{
    public required SessionWorkspace Sessions { get; init; }
    public WorldCatalog? Catalog { get; init; }
    public Action? Selected { get; init; }
    public Action<WorkspaceController, int>? EditAutomation { get; init; }
    public override void OnSelected() { Selected?.Invoke(); base.OnSelected(); }
}

public sealed partial class WorkspaceFactory(SessionWorkspace sessions, Action editWorld, Action? browseWorlds = null, Action<ConnectionProfile>? editProfile = null, WorldCatalog? catalog = null, Action<WorkspaceController, int>? editAutomation = null) : Factory
{
    private DocumentDock? _documents;
    private SessionDocument? _sessionDocument;
    public WorkspaceTool? WorldsTool { get; private set; }
    public WorkspaceTool? MapTool { get; private set; }
    public WorkspaceTool? ChannelsTool { get; private set; }

    public static void RegisterTemplates(DataTemplates templates)
    {
        templates.Add(new FuncDataTemplate<WorkspaceTool>((model, _) => model?.Build()));
        templates.Add(new FuncDataTemplate<MapEditorDocument>((model, _) => model is null ? null : new WorkspaceDocumentView(model, new MapEditorView(model.Model))));
        templates.Add(new FuncDataTemplate<SessionDocument>((model, _) => model is null ? null : new SessionContentView(model.Sessions, model.Catalog, model.EditAutomation)));
    }

    public override IRootDock CreateLayout()
    {
        SelectedKey = sessions.IsBrowsing ? SearchKey : sessions.Active;
        sessions.SelectionChanged += SessionSelected;
        UpdateDocumentTabs();
        var library = WorldsTool = new WorkspaceTool { Id = "worlds", Title = L.Workspace, CanClose = true, Build = () => new WorkspaceNavigationView(Navigation, new WorldLibraryView(sessions, editWorld, browseWorlds, editProfile)) };
        MapTool = new WorkspaceTool { Id = "map", Title = L.Map, CanClose = true, Build = () => new ActiveSessionView(sessions, controller => new MapView(controller, source => OpenMapEditor(controller, source))) };
        ChannelsTool = new WorkspaceTool { Id = "channels", Title = L.Channels, CanClose = true, Build = () => new ActiveSessionView(sessions, controller => new ChannelsView(new ChannelsViewModel(controller))) };
        var session = _sessionDocument = new SessionDocument { Id = "session", Title = L.Session, CanClose = false, CanFloat = false, Sessions = sessions, Catalog = catalog, EditAutomation = editAutomation, Selected = () => { SelectedKey = sessions.IsBrowsing ? SearchKey : sessions.Active; Navigation.Refresh(); } };
        var documents = _documents = new DocumentDock { Id = "documents", CanCreateDocument = false, VisibleDockables = CreateList<IDockable>(session), ActiveDockable = session, Proportion = 0.59 };
        var left = new ToolDock { Id = "left", Alignment = Alignment.Left, Proportion = 0.18, VisibleDockables = CreateList<IDockable>(library), ActiveDockable = library };
        var map = new ToolDock { Id = "map-dock", Alignment = Alignment.Right, Proportion = 0.58, VisibleDockables = CreateList<IDockable>(MapTool), ActiveDockable = MapTool };
        var channels = new ToolDock { Id = "channels-dock", Alignment = Alignment.Right, Proportion = 0.42, VisibleDockables = CreateList<IDockable>(ChannelsTool), ActiveDockable = ChannelsTool };
        // The map and the channels share the right edge, one above the other, both closable from the View menu.
        var right = new ProportionalDock { Id = "right", Proportion = 0.23, Orientation = Dock.Model.Core.Orientation.Vertical,
            VisibleDockables = CreateList<IDockable>(map, new ProportionalDockSplitter(), channels), ActiveDockable = map };
        var layout = new ProportionalDock { Orientation = Dock.Model.Core.Orientation.Horizontal, VisibleDockables = CreateList<IDockable>(left, new ProportionalDockSplitter(), documents, new ProportionalDockSplitter(), right), ActiveDockable = documents };
        return new RootDock { Id = "root", IsCollapsable = false, VisibleDockables = CreateList<IDockable>(layout), ActiveDockable = layout, DefaultDockable = layout };
    }

    public void OpenScripts(WorkspaceController controller)
    {
        var tab = sessions.Tabs.FirstOrDefault(t => ReferenceEquals(t.Controller, controller));
        if (tab is null || !controller.HasSession) return;
        sessions.Select(tab);
        editAutomation?.Invoke(controller, 2);
    }

    private void UpdateDocumentTabs()
    {
        if (Application.Current is { } app) app.Resources["DockDocumentControlTabStripVisible"] = false;
        Navigation.Refresh();
    }

    public override void InitLayout(IDockable layout)
    {
        HostWindowLocator = new Dictionary<string, Func<IHostWindow?>> { ["DockWindow"] = () => new HostWindow() };
        base.InitLayout(layout);
    }
}
