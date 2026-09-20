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
    // An activation (a session opened, a tab chosen) is what sends the keyboard to the composer; a dock rebuild
    // that merely shows the same view again is not.
    private bool _activated;

    public SessionContentView(SessionWorkspace sessions, WorldCatalog? catalog = null, Action<WorkspaceController, int>? editAutomation = null)
    {
        _sessions = sessions; _catalog = catalog; _editAutomation = editAutomation;
        Content = _content;
        Refresh();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); _sessions.Changed += Refresh; _sessions.SelectionChanged += Activated; Refresh(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { _sessions.Changed -= Refresh; _sessions.SelectionChanged -= Activated; base.OnDetachedFromVisualTree(e); }
    private void Activated() => _activated = true;
    private void Refresh()
    {
        SessionOpenTrace.Count("session content refresh");
        var activated = _activated;
        _activated = false;
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
            using (SessionOpenTrace.Measure("terminal view"))
                view = new TerminalView(controller, _editAutomation is null || _sessions.Active.Profile is null ? null : section => _editAutomation(controller, section));
            _views.Add(_sessions.Active, view);
        }
        _content.Content = view;
        if (activated) view.FocusComposer();
    }
}
