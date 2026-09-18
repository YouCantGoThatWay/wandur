using Avalonia;
using Avalonia.Controls;

namespace Wandur.Desktop.Views;

/// <summary>A tool panel that follows the selected session.</summary>
public sealed class ActiveSessionView(SessionWorkspace sessions, Func<WorkspaceController, Control> build) : ContentControl
{
    private WorkspaceController? _shown;
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        sessions.Changed += Refresh;
        Refresh();
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        sessions.Changed -= Refresh;
        base.OnDetachedFromVisualTree(e);
    }
    private void Refresh()
    {
        if (_shown == sessions.Active.Controller) return;
        _shown = sessions.Active.Controller;
        Content = build(_shown);
    }
}
