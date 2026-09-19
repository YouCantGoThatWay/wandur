using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using Wandur.Core.Scripting;
using Wandur.Desktop.Services;
using Wandur.Desktop.Views;

namespace Wandur.Desktop;

/// <summary>A docked tool showing one script-declared panel. Closing it removes the panel from its session.</summary>
public sealed class ScriptPanelTool : WorkspaceTool
{
    public required ScriptPanel Panel { get; init; }
    public required WorkspaceController Controller { get; init; }
    public Action? Closed { get; init; }
    public bool IsClosed { get; private set; }
    public override bool OnClose()
    {
        IsClosed = true;
        Closed?.Invoke();
        return true;
    }
}

public sealed partial class WorkspaceFactory
{
    private const string RightPanelsDockId = "panels-dock";
    private const string LeftPanelsDockId = "panels-left-dock";
    private const string LeftColumnId = "left-column";
    private readonly List<ScriptPanelTool> _panelTools = [];
    private bool _syncingPanels;
    private ProportionalDock? _leftColumn;
    public IReadOnlyList<ScriptPanelTool> ScriptPanelTools => _panelTools;

    /// <summary>The dock holding the script panels on the right edge, between the map and the channels. It exists only while a panel shows there.</summary>
    public ToolDock? RightPanelsDock { get; private set; }

    /// <summary>The dock holding the script panels on the left edge, below the world library. It exists only while a panel shows there.</summary>
    public ToolDock? LeftPanelsDock { get; private set; }

    /// <summary>Brings the docked tools in line with the panels every open session currently declares.</summary>
    public void SyncScriptPanels()
    {
        if (_layout is null || _right is null || _libraryDock is null || _syncingPanels) return;
        _syncingPanels = true;
        try
        {
            // A bars panel has no tool: the vitals strip under the transcript renders its gauges.
            var live = sessions.Tabs
                .SelectMany(tab => tab.Controller.ScriptLibrary.Panels.Panels.Where(panel => !panel.IsBars).Select(panel => (tab.Controller, Panel: panel))).ToArray();
            foreach (var tool in _panelTools.ToArray())
            {
                if (live.Any(item => ReferenceEquals(item.Panel, tool.Panel))) continue;
                _panelTools.Remove(tool);
                if (!tool.IsClosed) CloseDockable(tool);
            }
            foreach (var (controller, panel) in live)
                if (!_panelTools.Any(tool => ReferenceEquals(tool.Panel, panel))) _panelTools.Add(Create(controller, panel));
            foreach (var tool in _panelTools) if (tool.Title != tool.Panel.Title) tool.Title = tool.Panel.Title;
            // Tabs keep the order the scripts declared their panels in, session by session.
            var ordered = live.Select(item => _panelTools.First(tool => ReferenceEquals(tool.Panel, item.Panel))).ToArray();
            Arrange(ScriptPanelAction.DockRight, ordered);
            Arrange(ScriptPanelAction.DockLeft, ordered);
            // A focus request the panel accepted (one per second) brings its tab to the front of its dock.
            foreach (var tool in ordered)
                if (tool.Panel.TakeFocusRequest() && !tool.IsClosed && tool.Owner is IDock owner)
                {
                    SetActiveDockable(tool);
                    SetFocusedDockable(owner, tool);
                }
        }
        finally { _syncingPanels = false; }
    }

    private ScriptPanelTool Create(WorkspaceController controller, ScriptPanel panel)
    {
        ScriptPanelTool? created = null;
        created = new ScriptPanelTool
        {
            Id = "script-panel-" + Guid.NewGuid().ToString("N"),
            Title = panel.Title,
            Panel = panel,
            Controller = controller,
            CanClose = true,
            CanFloat = true,
            Build = () => new ScriptPanelView(panel),
            // The user closing the tool retires the panel; the script may declare it again.
            Closed = () => { if (created is not null) _panelTools.Remove(created); controller.ScriptLibrary.Panels.Close(panel); }
        };
        return created;
    }

    /// <summary>Fills one side's panels dock with the shown panels for that side, in declared order, creating or removing the dock as needed.</summary>
    private void Arrange(string side, ScriptPanelTool[] ordered)
    {
        var right = side == ScriptPanelAction.DockRight;
        var dock = right ? RightPanelsDock : LeftPanelsDock;
        var wanted = ordered.Where(tool => tool.Panel.Dock == side && tool.Panel.IsVisible).ToList();
        if (wanted.Count == 0)
        {
            if (dock is null) return;
            foreach (var tool in dock.VisibleDockables!.OfType<ScriptPanelTool>().ToArray()) RemoveDockable(tool, false);
            if (right) DetachRightPanels(dock); else DetachLeftPanels(dock);
            return;
        }
        if (dock is null)
        {
            // Never collapsable: the dock leaves the layout only through this class, when its last panel goes.
            dock = new ToolDock { Id = right ? RightPanelsDockId : LeftPanelsDockId, Alignment = right ? Alignment.Right : Alignment.Left, IsCollapsable = false, VisibleDockables = CreateList<IDockable>() };
            if (right) { RightPanelsDock = dock; AttachRightPanels(dock); } else { LeftPanelsDock = dock; AttachLeftPanels(dock); }
        }
        var tabs = dock.VisibleDockables!;
        foreach (var tool in tabs.OfType<ScriptPanelTool>().ToArray()) if (!wanted.Contains(tool)) RemoveDockable(tool, false);
        for (var i = 0; i < wanted.Count; i++)
        {
            var index = tabs.IndexOf(wanted[i]);
            if (index == i) continue;
            if (index >= 0) RemoveDockable(wanted[i], false);
            InsertDockable(dock, wanted[i], i);
        }
        // The first panel declared stays the active tab; a later one never steals it.
        if (dock.ActiveDockable is null || !tabs.Contains(dock.ActiveDockable)) SetActiveDockable(wanted[0]);
    }

    private void AttachRightPanels(ToolDock dock)
    {
        var edge = _layout!.VisibleDockables!;
        if (!edge.Contains(_right!)) InsertDockable(_layout, _right!, edge.Count);
        var items = _right!.VisibleDockables!;
        var index = _channelsDock is { } channels ? items.IndexOf(channels) : -1;
        InsertDockable(_right, dock, index < 0 ? items.Count : index);
        NormalizeSplitters(_right);
        NormalizeSplitters(_layout);
        ShareRightEdge();
    }

    private void DetachRightPanels(ToolDock dock)
    {
        RightPanelsDock = null;
        RemoveDockable(dock, false);
        NormalizeSplitters(_right!);
        ShareRightEdge();
    }

    /// <summary>Divides the right edge between the docks present: map above, panels in the middle, channels below.</summary>
    private void ShareRightEdge()
    {
        var docks = _right!.VisibleDockables!.OfType<IDock>().ToArray();
        double[] shares = docks.Length switch
        {
            3 => [0.40, 0.35, 0.25],
            2 => RightPanelsDock is null ? [0.58, 0.42] : [0.55, 0.45],
            _ => [1.0]
        };
        for (var i = 0; i < docks.Length; i++) Share(docks[i], shares[Math.Min(i, shares.Length - 1)]);
    }

    /// <summary>Sets a dock's share of its owner. The stored collapsed share goes with it, or the panel would restore the old one on the next layout pass.</summary>
    private static void Share(IDock dock, double proportion)
    {
        dock.Proportion = proportion;
        dock.CollapsedProportion = proportion;
    }

    private void AttachLeftPanels(ToolDock dock)
    {
        var edge = _layout!.VisibleDockables!;
        var index = edge.IndexOf(_libraryDock!);
        if (index < 0)
        {
            // The world library was closed, so the panels take the left edge on their own.
            Share(dock, _libraryDock!.Proportion);
            InsertDockable(_layout, dock, 0);
            NormalizeSplitters(_layout);
            return;
        }
        // The left edge becomes a column: the world library above, the panels below.
        var column = _leftColumn = new ProportionalDock { Id = LeftColumnId, Orientation = Dock.Model.Core.Orientation.Vertical, IsCollapsable = false, VisibleDockables = CreateList<IDockable>() };
        Share(column, _libraryDock!.Proportion);
        InsertDockable(_layout, column, index);
        RemoveDockable(_libraryDock, false);
        Share(_libraryDock, 0.55);
        Share(dock, 0.45);
        AddDockable(column, _libraryDock);
        AddDockable(column, new ProportionalDockSplitter());
        AddDockable(column, dock);
        column.ActiveDockable = _libraryDock;
    }

    private void DetachLeftPanels(ToolDock dock)
    {
        LeftPanelsDock = null;
        RemoveDockable(dock, false);
        if (_leftColumn is { } column)
        {
            _leftColumn = null;
            var edge = _layout!.VisibleDockables!;
            var index = edge.IndexOf(column);
            // The library goes back to being the plain left dock it was, at the column's width, unless the user closed it meanwhile.
            var library = column.VisibleDockables!.Contains(_libraryDock!);
            if (library) RemoveDockable(_libraryDock!, false);
            if (index >= 0) RemoveDockable(column, false);
            if (library)
            {
                Share(_libraryDock!, column.Proportion);
                InsertDockable(_layout, _libraryDock!, Math.Max(index, 0));
            }
        }
        NormalizeSplitters(_layout!);
    }

    /// <summary>Keeps exactly one splitter between neighbouring docks and none at either end.</summary>
    private void NormalizeSplitters(IDock dock)
    {
        var items = dock.VisibleDockables!;
        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (i >= items.Count) continue;
            if (items[i] is IProportionalDockSplitter && (i == 0 || i == items.Count - 1 || items[i - 1] is IProportionalDockSplitter)) RemoveDockable(items[i], false);
        }
        for (var i = 1; i < items.Count; i++)
            if (items[i] is not IProportionalDockSplitter && items[i - 1] is not IProportionalDockSplitter) InsertDockable(dock, new ProportionalDockSplitter(), i++);
    }

    /// <summary>Takes every panel tool and both lazy docks out of the layout, for example while the workspace layout is rebuilt.
    /// The panels stay declared, so the next layout's sync shows them again.</summary>
    public void CloseScriptPanels()
    {
        sessions.ScriptPanelsChanged -= SyncScriptPanels;
        _syncingPanels = true;
        try
        {
            foreach (var tool in _panelTools) if (!tool.IsClosed) RemoveDockable(tool, false);
            _panelTools.Clear();
            if (RightPanelsDock is { } right) DetachRightPanels(right);
            if (LeftPanelsDock is { } left) DetachLeftPanels(left);
        }
        finally { _syncingPanels = false; }
    }

    public bool IsScriptPanelVisible(ScriptPanel panel)
        => _panelTools.FirstOrDefault(tool => ReferenceEquals(tool.Panel, panel)) is { IsClosed: false, Owner: IDock owner } tool
            && owner.VisibleDockables?.Contains(tool) == true;
}
