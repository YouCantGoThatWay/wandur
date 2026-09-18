using Avalonia;
using Avalonia.Controls;
using Wandur.Core.Discovery;

namespace Wandur.Desktop.Views;

public sealed class SessionContentView : UserControl
{
    private readonly SessionWorkspace _sessions;
    private readonly ContentControl _content = new();
    private readonly Dictionary<SessionTab, TerminalView> _views = [];
    private WorldBrowserView? _browser;
    private readonly WorldCatalog? _catalog;
    private readonly Action<WorkspaceController, int>? _editAutomation;

    public SessionContentView(SessionWorkspace sessions, WorldCatalog? catalog = null, Action<WorkspaceController, int>? editAutomation = null)
    {
        _sessions = sessions; _catalog = catalog; _editAutomation = editAutomation;
        Content = _content;
        Refresh();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); _sessions.Changed += Refresh; Refresh(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { _sessions.Changed -= Refresh; base.OnDetachedFromVisualTree(e); }
    private void Refresh()
    {
        foreach (var removed in _views.Keys.Where(t => !_sessions.Tabs.Contains(t)).ToArray()) _views.Remove(removed);
        if ((_sessions.IsBrowsing || !_sessions.Active.Controller.HasSession) && _catalog is not null)
        {
            _browser ??= new WorldBrowserView(_sessions.Browser(_catalog), _catalog);
            _content.Content = _browser;
            return;
        }
        if (!_views.TryGetValue(_sessions.Active, out var view))
        {
            var controller = _sessions.Active.Controller;
            view = new TerminalView(controller, _editAutomation is null || _sessions.Active.Profile is null ? null : section => _editAutomation(controller, section));
            _views.Add(_sessions.Active, view);
        }
        _content.Content = view;
    }
}
