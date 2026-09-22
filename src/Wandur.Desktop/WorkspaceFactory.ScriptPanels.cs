using Dock.Model.Mvvm.Controls;
using Wandur.Desktop.Services;

namespace Wandur.Desktop;

/// <summary>Legacy dock tool type kept for API compatibility; script panels no longer use docks.</summary>
public sealed class ScriptPanelTool : WorkspaceTool
{
    public required ScriptPanel Panel { get; init; }
    public required WorkspaceController Controller { get; init; }
    public Action? Closed { get; set; }
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
    /// <summary>Always empty: non-bars panels render in the session rail beside the transcript.</summary>
    public IReadOnlyList<ScriptPanelTool> ScriptPanelTools => [];

    /// <summary>Always null: the right panels dock was retired in favour of the session rail.</summary>
    public ToolDock? RightPanelsDock => null;

    /// <summary>Always null: the left panels dock was retired in favour of the session rail.</summary>
    public ToolDock? LeftPanelsDock => null;

    /// <summary>No-op. Panels sync inside each session's ScriptPanelRailView.</summary>
    public void SyncScriptPanels() { }

    /// <summary>No-op. There are no panel tools left in the layout to tear down.</summary>
    public void CloseScriptPanels()
    {
        sessions.ScriptPanelsChanged -= SyncScriptPanels;
    }

    /// <summary>True while a non-bars panel is visible; the session rail shows it beside the transcript.</summary>
    public bool IsScriptPanelVisible(ScriptPanel panel)
        => !panel.IsBars && panel.IsVisible;
}
