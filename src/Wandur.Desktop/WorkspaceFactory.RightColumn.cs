using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;

namespace Wandur.Desktop;

public sealed partial class WorkspaceFactory
{
    /// <summary>
    /// When Map and Channels are both pinned or hidden, the nested right column used to stay in the
    /// layout with <c>IsCollapsable=false</c>, leaving a grey empty strip. Pull the whole column out
    /// of the horizontal layout so the session document expands; put it back when either tool returns.
    /// </summary>
    public override void HideDockable(IDockable dockable)
    {
        base.HideDockable(dockable);
        if (IsRightEdgeTool(dockable)) SyncRightColumn();
    }

    public override void PinDockable(IDockable dockable)
    {
        // Unpinning reattaches to OriginalOwner; that tool dock must still sit under the right column.
        var root = FindRoot(dockable, _ => true);
        if (IsRightEdgeTool(dockable) && root is not null && IsDockablePinned(dockable, root))
            EnsureRightColumnPresent();
        base.PinDockable(dockable);
        if (IsRightEdgeTool(dockable)) SyncRightColumn();
    }

    public override void RestoreDockable(IDockable dockable)
    {
        if (IsRightEdgeTool(dockable)) EnsureRightColumnPresent();
        base.RestoreDockable(dockable);
        if (IsRightEdgeTool(dockable)) SyncRightColumn();
    }

    public override void OnDockableClosed(IDockable? dockable)
    {
        base.OnDockableClosed(dockable);
        if (dockable is not null && IsRightEdgeTool(dockable)) SyncRightColumn();
    }

    private bool IsRightEdgeTool(IDockable? dockable) =>
        ReferenceEquals(dockable, MapTool) || ReferenceEquals(dockable, ChannelsTool);

    private static bool ToolInColumn(WorkspaceTool? tool) =>
        tool?.Owner is IDock dock && dock.VisibleDockables?.Contains(tool) == true;

    private void SyncRightColumn()
    {
        if (_layout is null || _right is null) return;
        var any = ToolInColumn(MapTool) || ToolInColumn(ChannelsTool);
        var present = _layout.VisibleDockables?.Contains(_right) == true;
        if (!any && present)
        {
            if (_right.Proportion > 0 && !double.IsNaN(_right.Proportion))
                _right.CollapsedProportion = _right.Proportion;
            RemoveDockable(_right, collapse: false);
            CleanupOrphanedSplitters(_layout);
        }
        else if (any && !present) EnsureRightColumnPresent();
    }

    private void EnsureRightColumnPresent()
    {
        if (_layout is null || _right is null) return;
        if (_layout.VisibleDockables?.Contains(_right) == true) return;
        if (_right.CollapsedProportion > 0 && !double.IsNaN(_right.CollapsedProportion))
            _right.Proportion = _right.CollapsedProportion;
        else if (!(_right.Proportion > 0) || double.IsNaN(_right.Proportion))
            _right.Proportion = 0.23;
        AddVisibleDockable(_layout, _right);
        InitDockable(_right, _layout);
        CleanupOrphanedSplitters(_layout);
    }
}
