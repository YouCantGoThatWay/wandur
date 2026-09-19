using Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
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
    internal bool Shown { get; set; } = true;
    public override bool OnClose()
    {
        IsClosed = true;
        Closed?.Invoke();
        return true;
    }
}

public sealed partial class WorkspaceFactory
{
    private readonly List<ScriptPanelTool> _panelTools = [];
    private bool _syncingPanels;
    public IReadOnlyList<ScriptPanelTool> ScriptPanelTools => _panelTools;

    /// <summary>Brings the docked tools in line with the panels every open session currently declares.</summary>
    public void SyncScriptPanels()
    {
        if (_rightTools is null || _leftTools is null || _syncingPanels) return;
        _syncingPanels = true;
        try
        {
            var live = sessions.Tabs
                .SelectMany(tab => tab.Controller.ScriptLibrary.Panels.Panels.Select(panel => (tab.Controller, Panel: panel))).ToArray();
            foreach (var tool in _panelTools.ToArray())
            {
                if (live.Any(item => ReferenceEquals(item.Panel, tool.Panel))) continue;
                _panelTools.Remove(tool);
                if (!tool.IsClosed) CloseDockable(tool);
            }
            foreach (var (controller, panel) in live)
                if (!_panelTools.Any(tool => ReferenceEquals(tool.Panel, panel))) Open(controller, panel);
            foreach (var tool in _panelTools) Apply(tool);
        }
        finally { _syncingPanels = false; }
    }

    private void Open(WorkspaceController controller, ScriptPanel panel)
    {
        ScriptPanelTool? created = null;
        var tool = created = new ScriptPanelTool
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
        _panelTools.Add(tool);
        AddDockable(panel.Dock == ScriptPanelAction.DockLeft ? _leftTools! : _rightTools!, tool);
        SetActiveDockable(tool);
    }

    private void Apply(ScriptPanelTool tool)
    {
        if (tool.Title != tool.Panel.Title) tool.Title = tool.Panel.Title;
        if (tool.Shown == tool.Panel.IsVisible) return;
        tool.Shown = tool.Panel.IsVisible;
        if (tool.Shown) RestoreDockable(tool); else HideDockable(tool);
    }

    /// <summary>Closes every panel tool, for example while the workspace layout is rebuilt.</summary>
    public void CloseScriptPanels()
    {
        foreach (var tool in _panelTools.ToArray()) if (!tool.IsClosed) CloseDockable(tool);
        _panelTools.Clear();
    }

    public bool IsScriptPanelVisible(ScriptPanel panel)
        => _panelTools.FirstOrDefault(tool => ReferenceEquals(tool.Panel, panel)) is { IsClosed: false, Owner: IDock owner } tool
            && owner.VisibleDockables?.Contains(tool) == true;
}
