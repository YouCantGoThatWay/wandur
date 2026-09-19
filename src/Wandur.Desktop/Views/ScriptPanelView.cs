using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Wandur.Core.Scripting;
using Wandur.Desktop.Services;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.Views;

/// <summary>Renders one script-declared panel with native controls. Scripts supply data only;
/// property updates change the existing controls rather than rebuilding the panel.</summary>
public sealed class ScriptPanelView : UserControl
{
    private sealed record Widget(string Kind, Control Root, Action<ScriptWidgetProperties> Apply)
    {
        public Panel? Children { get; init; }
        public Action? Detach { get; init; }
    }

    private readonly ScriptPanel _panel;
    private readonly StackPanel _body = new() { Spacing = 6, Margin = new Thickness(8, 6) };
    private readonly TextBlock _empty = Ui.TextKey(nameof(L.ScriptPanelEmpty), 12, "muted");
    private readonly Dictionary<string, Widget> _widgets = new(StringComparer.Ordinal);
    private bool _syncing;

    public ScriptPanelView(ScriptPanel panel)
    {
        _panel = panel;
        Name = "ScriptPanel_" + Safe(panel.Id);
        _empty.Margin = new Thickness(12);
        _empty.TextWrapping = TextWrapping.Wrap;
        var host = new Grid { Children = { _body, _empty } };
        var scroll = new ScrollViewer { Content = host, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var frame = new Border { Child = scroll };
        frame.Bind(BackgroundProperty, new DynamicResourceExtension("ShellBrush"));
        Content = frame;
    }

    public ScriptPanel Panel => _panel;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _panel.Changed += Sync;
        Sync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _panel.Changed -= Sync;
        base.OnDetachedFromVisualTree(e);
    }

    private static string Safe(string id) => new(id.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

    private void Sync()
    {
        foreach (var id in _widgets.Keys.Where(id => _panel.Widgets.All(widget => widget.Id != id)).ToArray())
        {
            _widgets[id].Detach?.Invoke();
            _widgets.Remove(id);
        }
        foreach (var declared in _panel.Widgets)
        {
            if (_widgets.TryGetValue(declared.Id, out var existing) && existing.Kind != declared.Kind)
            {
                existing.Detach?.Invoke();
                _widgets.Remove(declared.Id);
            }
            if (!_widgets.ContainsKey(declared.Id))
            {
                _widgets[declared.Id] = Build(declared);
                declared.Changed += rebuilt => { if (rebuilt || declared.Kind == "group") Sync(); else Update(declared); };
            }
            Update(declared);
        }
        Layout();
        _empty.IsVisible = _panel.Widgets.Count == 0;
    }

    private void Update(ScriptPanelWidget declared)
    {
        if (!_widgets.TryGetValue(declared.Id, out var widget)) return;
        _syncing = true;
        try { widget.Apply(declared.Properties); }
        finally { _syncing = false; }
    }

    private void Layout()
    {
        var claimed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in _panel.Widgets.Where(widget => widget.Kind == "group"))
            foreach (var child in group.Properties.Children)
                if (child != group.Id && _widgets.TryGetValue(child, out var candidate) && candidate.Kind != "group" && !claimed.ContainsKey(child))
                    claimed[child] = group.Id;
        var wanted = new List<(Panel Host, Control[] Children)>
        {
            (_body, _panel.Widgets.Where(widget => !claimed.ContainsKey(widget.Id)).Select(widget => _widgets[widget.Id].Root).ToArray())
        };
        foreach (var group in _panel.Widgets.Where(widget => widget.Kind == "group"))
            wanted.Add((_widgets[group.Id].Children!, _panel.Widgets
                .Where(widget => claimed.GetValueOrDefault(widget.Id) == group.Id).Select(widget => _widgets[widget.Id].Root).ToArray()));
        if (wanted.All(item => item.Host.Children.SequenceEqual(item.Children))) return;
        // Re-parenting needs every host emptied first, so a moved control has no previous parent.
        foreach (var (host, _) in wanted) host.Children.Clear();
        foreach (var (host, children) in wanted) foreach (var child in children) host.Children.Add(child);
    }

    private Widget Build(ScriptPanelWidget declared)
    {
        var name = "ScriptWidget_" + Safe(declared.Id);
        switch (declared.Kind)
        {
            case "gauge":
            {
                var bar = new ResourceBar { Name = name, Margin = new Thickness(0) };
                return new("gauge", bar, properties => bar.Update(properties.Label ?? declared.Id,
                    ScriptPanelAction.Measure(properties.Number, properties.Maximum),
                    properties.Maximum > 0 ? properties.Number / properties.Maximum * 100 : 0,
                    Warned(properties) ? ResourceBar.ColorFor("health") : null));
            }
            case "label":
            {
                var text = Ui.Text("", 13);
                text.Name = name; text.TextWrapping = TextWrapping.Wrap;
                return new("label", text, properties => text.Text = properties.Text ?? "");
            }
            case "text":
            {
                var text = Ui.Text("", 12, "muted");
                text.Name = name; text.TextWrapping = TextWrapping.Wrap;
                return new("text", text, properties => text.Text = properties.Text ?? "");
            }
            case "list":
            {
                var items = new ObservableCollection<string>();
                var list = new ListBox { Name = name, ItemsSource = items, MaxHeight = 220 };
                list.SelectionChanged += (_, _) =>
                {
                    if (_syncing || list.SelectedItem is not string selected) return;
                    _panel.Invoke(declared.Id, "select", text: selected);
                };
                var caption = Ui.Text("", 11, "muted");
                var host = new StackPanel { Spacing = 4, Children = { caption, list } };
                return new("list", host, properties =>
                {
                    caption.Text = properties.Title ?? "";
                    caption.IsVisible = caption.Text.Length > 0;
                    if (items.SequenceEqual(properties.Items, StringComparer.Ordinal)) return;
                    var selected = list.SelectedItem as string;
                    items.Clear();
                    foreach (var item in properties.Items) items.Add(item);
                    list.SelectedItem = selected is not null && items.Contains(selected) ? selected : null;
                });
            }
            case "table":
            {
                var grid = new Grid { Name = name, ColumnSpacing = 10, RowSpacing = 2 };
                var caption = Ui.Text("", 11, "muted");
                var host = new StackPanel { Spacing = 4, Children = { caption, grid } };
                return new("table", host, properties =>
                {
                    caption.Text = properties.Title ?? "";
                    caption.IsVisible = caption.Text.Length > 0;
                    Fill(grid, properties);
                });
            }
            case "button":
            {
                var button = new Button { Name = name, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
                button.Click += (_, _) => _panel.Invoke(declared.Id, "click");
                return new("button", button, properties => button.Content = properties.Label ?? declared.Id);
            }
            case "toggle":
            {
                var toggle = new CheckBox { Name = name, FontSize = 12 };
                toggle.IsCheckedChanged += (_, _) => { if (!_syncing) _panel.Invoke(declared.Id, "change", flag: toggle.IsChecked == true); };
                return new("toggle", toggle, properties =>
                {
                    toggle.Content = properties.Label ?? declared.Id;
                    toggle.IsChecked = properties.On;
                });
            }
            case "input":
            {
                var box = new TextBox { Name = name, MinHeight = 28, Padding = new Thickness(6, 2), VerticalContentAlignment = VerticalAlignment.Center };
                box.KeyDown += (_, args) =>
                {
                    if (args.Key != Key.Enter && args.Key != Key.Return) return;
                    args.Handled = true;
                    var text = box.Text ?? "";
                    box.Text = "";
                    _panel.Invoke(declared.Id, "submit", text: text);
                };
                return new("input", box, properties =>
                {
                    box.PlaceholderText = properties.Placeholder ?? "";
                    if (properties.Value is { } value) box.Text = value;
                });
            }
            case "group":
            {
                var caption = Ui.Text("", 12);
                var children = new StackPanel { Spacing = 6 };
                var frame = new Border { Name = name, Padding = new Thickness(8, 6), CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1),
                    Child = new StackPanel { Spacing = 6, Children = { caption, children } } };
                frame.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("LineBrush"));
                return new Widget("group", frame, properties =>
                {
                    caption.Text = properties.Title ?? "";
                    caption.IsVisible = caption.Text.Length > 0;
                }) { Children = children };
            }
            default:
            {
                var separator = new Separator { Name = name, Margin = new Thickness(0, 2) };
                separator.Bind(BackgroundProperty, new DynamicResourceExtension("LineBrush"));
                return new("separator", separator, _ => { });
            }
        }
    }

    private static bool Warned(ScriptWidgetProperties properties)
        => properties.Warn is { } warn && properties.Maximum > 0 && properties.Number / properties.Maximum <= warn;

    private static void Fill(Grid grid, ScriptWidgetProperties properties)
    {
        var columns = Math.Max(properties.Columns.Count, properties.Rows.Count == 0 ? 0 : properties.Rows.Max(row => row.Count));
        grid.Children.Clear();
        grid.ColumnDefinitions.Clear();
        grid.RowDefinitions.Clear();
        if (columns == 0) return;
        for (var column = 0; column < columns; column++) grid.ColumnDefinitions.Add(new(GridLength.Auto));
        grid.ColumnDefinitions[^1] = new(GridLength.Star);
        var rows = properties.Columns.Count > 0 ? properties.Rows.Count + 1 : properties.Rows.Count;
        for (var row = 0; row < rows; row++) grid.RowDefinitions.Add(new(GridLength.Auto));
        var offset = 0;
        if (properties.Columns.Count > 0)
        {
            for (var column = 0; column < properties.Columns.Count; column++) Cell(grid, properties.Columns[column], 0, column, true);
            offset = 1;
        }
        for (var row = 0; row < properties.Rows.Count; row++)
            for (var column = 0; column < properties.Rows[row].Count && column < columns; column++)
                Cell(grid, properties.Rows[row][column], row + offset, column, false);
    }

    private static void Cell(Grid grid, string text, int row, int column, bool heading)
    {
        var block = Ui.Text(text, heading ? 11 : 12, heading ? "muted" : null);
        block.TextTrimming = TextTrimming.CharacterEllipsis;
        AutomationProperties.SetName(block, text);
        Grid.SetRow(block, row);
        Grid.SetColumn(block, column);
        grid.Children.Add(block);
    }
}
