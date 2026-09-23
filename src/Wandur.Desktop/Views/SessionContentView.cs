using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Wandur.Core.Discovery;

namespace Wandur.Desktop.Views;

public sealed class SessionContentView : UserControl
{
    private readonly SessionWorkspace _sessions;
    private readonly ContentControl _content = new();
    private readonly Border _fleetHeader;
    private readonly Border _frame;
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
        var title = Ui.TextKey(nameof(Wandur.Core.Localization.Strings.SettingsTerminal), 14);
        title.FontWeight = FontWeight.SemiBold;
        title.VerticalAlignment = VerticalAlignment.Center;
        _fleetHeader = new Border { Name = "FleetDocumentHeader", Padding = new Thickness(12, 0),
            BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = Brush.Parse("#424743"), Child = title };
        _fleetHeader.Bind(Border.BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("DockHeaderBrush"));
        _fleetHeader.Bind(HeightProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("DockHeaderHeight"));
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Children = { _fleetHeader, _content } };
        Grid.SetRow(_content, 1);
        _frame = new Border { Name = "FleetDocumentFrame", Child = layout };
        Content = _frame;
        ApplySkin();
        Refresh();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); _sessions.Changed += Refresh; _sessions.SelectionChanged += Activated; ThemeService.Applied += ApplySkin; ApplySkin(); Refresh(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { _sessions.Changed -= Refresh; _sessions.SelectionChanged -= Activated; ThemeService.Applied -= ApplySkin; base.OnDetachedFromVisualTree(e); }
    private void ApplySkin()
    {
        var fleet = FleetSkin.IsActive;
        _fleetHeader.IsVisible = fleet && _content.Content is TerminalView;
        _frame.Padding = fleet ? new Thickness(2) : default;
        _frame.Background = fleet ? FleetSkin.Metal : Brushes.Transparent;
        _frame.BorderBrush = fleet ? Brush.Parse("#F3F3EC") : Brushes.Transparent;
        _frame.BorderThickness = fleet ? new Thickness(1) : default;
        _frame.CornerRadius = fleet ? new CornerRadius(2) : default;
    }
    private void Activated() => _activated = true;
    private void Refresh()
    {
        SessionOpenTrace.Count("session content refresh");
        var activated = _activated;
        _activated = false;
        foreach (var removed in _views.Keys.Where(t => !_sessions.Tabs.Contains(t)).ToArray()) _views.Remove(removed);
        if ((_sessions.IsBrowsing || !_sessions.Active.Controller.HasSession) && _catalog is not null)
        {
            _fleetHeader.IsVisible = false;
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
        _fleetHeader.IsVisible = FleetSkin.IsActive;
        if (activated) view.FocusComposer();
    }
}
