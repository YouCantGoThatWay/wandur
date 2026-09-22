using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Wandur.Desktop.Services;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.Views;

/// <summary>Hosts this session's non-bars script panels beside the transcript as an accordion:
/// headers stay visible, the open section fills the remaining height. Map and Channels stay shell docks.</summary>
public sealed class ScriptPanelRailView : UserControl
{
    /// <summary>Column width while the rail is folded to the top restore control.</summary>
    public const double CollapsedWidth = 28;

    private const string ChevronLeft = "M 10,2 L 4,8 L 10,14";
    private const string ChevronRight = "M 6,2 L 12,8 L 6,14";

    private readonly Grid _sectionsHost = new()
    {
        Name = "ScriptPanelRailSections",
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch
    };
    private readonly Avalonia.Controls.Shapes.Path _chevron = new()
    {
        // Horizontal chevron: wider than tall so it reads as < / > not a rotated v.
        Width = 12,
        Height = 8,
        Stretch = Stretch.Uniform,
        StrokeThickness = 1.6,
        StrokeLineCap = PenLineCap.Round,
        StrokeJoin = PenLineJoin.Round,
        IsHitTestVisible = false
    };
    private readonly Button _collapse = new()
    {
        Name = "ScriptPanelRailCollapse",
        Width = 28,
        Height = 28,
        MinWidth = 28,
        MinHeight = 28,
        Padding = new Thickness(0),
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center
    };
    private readonly Border _headerBar = new()
    {
        Name = "ScriptPanelRailHeader",
        Height = 28,
        BorderThickness = new Thickness(0, 0, 0, 1),
        Padding = new Thickness(0)
    };
    private readonly ColumnDefinition _width = new(new GridLength(260)) { MinWidth = 180 };
    private readonly Dictionary<ScriptPanel, Section> _sections = new();
    private readonly List<ScriptPanel> _order = [];
    private ScriptPanelHost? _host;
    private bool _syncing;
    private bool _collapsed;
    private ScriptPanel? _selected;

    private sealed class Section(Border Header, TextBlock Title, Border Body, ScriptPanelView View)
    {
        public Border Header { get; } = Header;
        public TextBlock Title { get; } = Title;
        public Border Body { get; } = Body;
        public ScriptPanelView View { get; } = View;
    }

    public ScriptPanelRailView()
    {
        Name = "ScriptPanelRail";
        IsVisible = false;
        MinWidth = CollapsedWidth;
        MinHeight = 0;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        _chevron.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty, new DynamicResourceExtension("TextBrush"));
        _collapse.Content = _chevron;
        _collapse.Click += (_, _) => SetCollapsed(!_collapsed);
        _collapse.PointerEntered += (_, _) => _collapse.Background = Hover();
        _collapse.PointerExited += (_, _) => _collapse.Background = Brushes.Transparent;
        _headerBar.Child = _collapse;
        _headerBar.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("LineBrush"));
        _headerBar.Bind(BackgroundProperty, new DynamicResourceExtension("ShellBrush"));
        this.Bind(BackgroundProperty, new DynamicResourceExtension("ShellBrush"));
        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Children = { _headerBar, _sectionsHost }
        };
        Grid.SetRow(_sectionsHost, 1);
        Content = root;
        _sectionsHost.SizeChanged += (_, _) =>
        {
            if (_collapsed || _selected is null || !_sections.TryGetValue(_selected, out var section)) return;
            CapOpenBody(section.Body);
        };
        RefreshCollapseChrome();
    }

    /// <summary>Preferred width of the rail column when it is showing.</summary>
    public ColumnDefinition WidthDefinition => _width;

    /// <summary>True while the accordion is folded to the top restore control.</summary>
    public bool IsCollapsed => _collapsed;

    /// <summary>The session's script panels. Bars stay in the vitals strip; everything else appears here.</summary>
    public ScriptPanelHost? Panels
    {
        get => _host;
        set
        {
            if (ReferenceEquals(_host, value)) return;
            if (_host is not null) _host.Changed -= Sync;
            _host = value;
            if (_host is not null) _host.Changed += Sync;
            Sync();
        }
    }

    /// <summary>The panel whose accordion section is open, if any.</summary>
    public ScriptPanel? SelectedPanel => _selected;

    /// <summary>Opens one section and collapses the others. Expands the rail if it was folded.</summary>
    public void Select(ScriptPanel panel)
    {
        if (!_order.Contains(panel)) return;
        if (_collapsed) SetCollapsed(false);
        Expand(panel);
    }

    /// <summary>True when this panel is a visible non-bars panel in the accordion.</summary>
    public bool Shows(ScriptPanel panel)
        => !panel.IsBars && panel.IsVisible && _sections.ContainsKey(panel);

    /// <summary>Folds or unfolds the accordion. Folded, only the top restore control remains.</summary>
    public void SetCollapsed(bool collapsed)
    {
        if (_collapsed == collapsed) return;
        _collapsed = collapsed;
        RefreshCollapseChrome();
        WidthChanged?.Invoke();
    }

    /// <summary>Raised when the rail's preferred width changes (collapse or panel presence).</summary>
    public event Action? WidthChanged;

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_host is not null) _host.Changed -= Sync;
        base.OnDetachedFromVisualTree(e);
    }

    private void Sync()
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            var live = (_host?.Panels ?? []).Where(panel => !panel.IsBars && panel.IsVisible).ToArray();
            foreach (var panel in _sections.Keys.Where(panel => !live.Contains(panel)).ToArray())
            {
                _sections[panel].View.Detach();
                _sections.Remove(panel);
                if (ReferenceEquals(_selected, panel)) _selected = null;
            }
            foreach (var panel in live)
            {
                if (_sections.ContainsKey(panel))
                {
                    RefreshHeader(_sections[panel], panel);
                    continue;
                }
                _sections[panel] = Create(panel);
            }

            _order.Clear();
            _order.AddRange(live);

            ScriptPanel? focus = null;
            foreach (var panel in live)
                if (panel.TakeFocusRequest()) focus = panel;

            if (live.Length == 0)
            {
                _selected = null;
                RebuildLayout();
                IsVisible = false;
                WidthChanged?.Invoke();
                return;
            }

            IsVisible = true;
            if (focus is not null)
            {
                if (_collapsed) SetCollapsed(false);
                _selected = focus;
            }
            else if (_selected is null || !live.Contains(_selected))
                _selected = live[0];
            RebuildLayout();
            WidthChanged?.Invoke();
        }
        finally { _syncing = false; }
    }

    private Section Create(ScriptPanel panel)
    {
        var view = new ScriptPanelView(panel)
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        var title = new TextBlock
        {
            Name = "ScriptPanelRailTitle_" + Safe(panel.Id),
            Text = panel.Title,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(10, 0)
        };
        // Plain Border headers (not Button) so Fluent padding/radius cannot inset the highlight from the rail edge.
        var header = new Border
        {
            Name = "ScriptPanelRailSection_" + Safe(panel.Id),
            Child = title,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinHeight = 28,
            Padding = new Thickness(0, 6),
            BorderThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(0),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
        };
        header.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("LineBrush"));
        header.PointerPressed += (_, args) =>
        {
            if (!args.GetCurrentPoint(header).Properties.IsLeftButtonPressed) return;
            args.Handled = true;
            if (_collapsed) SetCollapsed(false);
            Expand(panel);
        };
        var body = new Border
        {
            Name = "ScriptPanelRailBody_" + Safe(panel.Id),
            Child = view,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            // Top-align so a short panel does not stretch into an empty plate.
            VerticalAlignment = VerticalAlignment.Top,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            IsVisible = false
        };
        body.Bind(BackgroundProperty, new DynamicResourceExtension("PanelBrush"));
        return new Section(header, title, body, view);
    }

    private void Expand(ScriptPanel panel)
    {
        if (!_sections.ContainsKey(panel)) return;
        _selected = panel;
        RebuildLayout();
    }

    /// <summary>
    /// Headers and the open body stack at content height; leftover rail height sits below the stack.
    /// Tall open panels get a MaxHeight so their inner ScrollViewer kicks in instead of shoving headers off-screen.
    /// </summary>
    private void RebuildLayout()
    {
        _sectionsHost.Children.Clear();
        _sectionsHost.RowDefinitions.Clear();
        if (_collapsed || _order.Count == 0) return;
        var row = 0;
        Border? openBody = null;
        foreach (var panel in _order)
        {
            var section = _sections[panel];
            var open = ReferenceEquals(panel, _selected);
            section.Header.Bind(BackgroundProperty, new DynamicResourceExtension(open ? "PanelBrush" : "ShellBrush"));
            _sectionsHost.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(section.Header, row++);
            _sectionsHost.Children.Add(section.Header);
            section.Body.IsVisible = open;
            if (!open)
            {
                section.Body.MaxHeight = double.PositiveInfinity;
                continue;
            }
            _sectionsHost.RowDefinitions.Add(new RowDefinition(GridLength.Auto) { MinHeight = 0 });
            Grid.SetRow(section.Body, row++);
            _sectionsHost.Children.Add(section.Body);
            openBody = section.Body;
        }
        _sectionsHost.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        CapOpenBody(openBody);
    }

    private void CapOpenBody(Border? openBody)
    {
        if (openBody is null) return;
        void Apply()
        {
            if (!ReferenceEquals(openBody.Parent, _sectionsHost)) return;
            var host = _sectionsHost.Bounds.Height;
            if (host <= 0) return;
            var headers = 0d;
            foreach (var panel in _order) headers += _sections[panel].Header.Bounds.Height;
            var max = Math.Max(48, host - headers);
            if (openBody.MaxHeight != max) openBody.MaxHeight = max;
        }
        if (_sectionsHost.Bounds.Height > 0) Apply();
        else _sectionsHost.LayoutUpdated += OnLayout;
        void OnLayout(object? sender, EventArgs e)
        {
            _sectionsHost.LayoutUpdated -= OnLayout;
            Apply();
        }
    }

    private void RefreshCollapseChrome()
    {
        _sectionsHost.IsVisible = !_collapsed;
        // Folded: chevron points left (show the rail). Open: points right (tuck it away).
        _chevron.Data = StreamGeometry.Parse(_collapsed ? ChevronLeft : ChevronRight);
        _collapse.Bind(ToolTip.TipProperty, LocalizedText.Binding(_collapsed ? nameof(L.ScriptPanelRailExpand) : nameof(L.ScriptPanelRailCollapse)));
        Avalonia.Automation.AutomationProperties.SetName(_collapse, _collapsed ? L.ScriptPanelRailExpand : L.ScriptPanelRailCollapse);
    }

    private static void RefreshHeader(Section section, ScriptPanel panel)
    {
        section.Title.Text = panel.Title;
    }

    private static IBrush Hover()
        => (IBrush?)Application.Current?.FindResource("ToolbarHoverBrush") ?? Brushes.Transparent;

    private static string Safe(string id) => new(id.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
}
