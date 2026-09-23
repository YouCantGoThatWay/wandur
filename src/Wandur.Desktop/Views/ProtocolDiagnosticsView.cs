using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Wandur.Core.Diagnostics;
using Wandur.Desktop.ViewModels;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.Views;

public sealed class ProtocolDiagnosticsView : UserControl
{
    /// <param name="console">The session's raw text stream; when given, a Console tab shows it beside the protocol messages.</param>
    public ProtocolDiagnosticsView(ProtocolDiagnosticsViewModel model, ConsoleLog? console = null)
    {
        DataContext = model;
        var count = Ui.Text("", 13, "muted");
        count.Name = "DiagnosticsCount";
        count.Bind(TextBlock.TextProperty, new Binding(nameof(model.CountLabel)));
        var clear = Ui.ToolbarIconKey(new Button { Name = "ClearDiagnostics", Command = model.ClearCommand },
            "M 3,4 H 13 M 6,4 V 2 H 10 V 4 M 4,4 L 5,14 H 11 L 12,4 M 7,6 V 12 M 9,6 V 12", nameof(L.DiagnosticsClear));
        clear.Width = 32; clear.Height = 32;
        var follow = new CheckBox { Name = "FollowDiagnostics", FontSize = 13, MinHeight = 32, VerticalAlignment = VerticalAlignment.Center };
        follow.Bind(ContentControl.ContentProperty, LocalizedText.Binding(nameof(L.DiagnosticsFollow)));
        follow.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(model.Follow)) { Mode = BindingMode.TwoWay });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { follow, clear } };
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(12, 4), Children = { count, actions } };
        Grid.SetColumn(actions, 1); count.VerticalAlignment = VerticalAlignment.Center;
        var list = new ListBox
        {
            Name = "ProtocolMessages", ItemsSource = model.Visible,
            ItemTemplate = new FuncDataTemplate<ProtocolDiagnosticEntry>((entry, _) =>
            {
                var name = new TextBlock
                {
                    Text = entry?.Content?.Name ?? entry?.Protocol, FontSize = 13,
                    FontWeight = FontWeight.Medium, TextTrimming = TextTrimming.CharacterEllipsis
                };
                var metadata = Ui.Text(entry is null ? "" : $"{entry.ReceivedAt.ToLocalTime():HH:mm:ss.fff}  {entry.Protocol}", 12, "muted");
                var row = new StackPanel { Spacing = 3, Margin = new Thickness(4, 3), Children = { name, metadata } };
                ToolTip.SetTip(row, entry?.Heading);
                return row;
            })
        };
        list.Classes.Add("protocol-messages");
        list.Bind(SelectingItemsControl.SelectedItemProperty, new Binding(nameof(model.SelectedEntry)) { Mode = BindingMode.TwoWay });
        list.SelectionChanged += (_, args) =>
        {
            if (args.AddedItems.Count > 0 && !ReferenceEquals(list.SelectedItem, model.Visible.LastOrDefault())) model.Follow = false;
            if (model.Follow && list.SelectedItem is not null) list.ScrollIntoView(list.SelectedItem);
        };
        var detail = new DiagnosticsBodyEditor { Name = "ProtocolMessageDetail" };
        detail.Bind(DiagnosticsBodyEditor.SourceTextProperty, new Binding(nameof(model.Detail)));
        var panes = new Grid { Children = { list, detail } };
        var divider = new GridSplitter { ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        divider.Bind(BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("LineBrush"));
        panes.Children.Add(divider);
        bool? wide = null;
        void ArrangePanes(Size size)
        {
            var nextWide = size.Width >= 840;
            if (wide != nextWide)
            {
                wide = nextWide;
                panes.RowDefinitions = new RowDefinitions(nextWide ? "*" : "2*,8,3*");
                panes.ColumnDefinitions = new ColumnDefinitions(nextWide ? "2*,8,3*" : "*");
                if (nextWide)
                {
                    panes.ColumnDefinitions[0].MinWidth = 280;
                    panes.ColumnDefinitions[2].MinWidth = 320;
                }
                Grid.SetRow(detail, nextWide ? 0 : 2); Grid.SetColumn(detail, nextWide ? 2 : 0);
                Grid.SetRow(divider, nextWide ? 0 : 1); Grid.SetColumn(divider, nextWide ? 1 : 0);
                divider.ResizeDirection = nextWide ? GridResizeDirection.Columns : GridResizeDirection.Rows;
                divider.HorizontalAlignment = HorizontalAlignment.Stretch;
                divider.VerticalAlignment = VerticalAlignment.Stretch;
                divider.Cursor = new Cursor(nextWide ? StandardCursorType.SizeWestEast : StandardCursorType.SizeNorthSouth);
            }
            if (!nextWide)
            {
                // Never force the enclosing document taller than its allocated viewport.
                // The minima leave spare space and shrink along with a short embedded pane.
                var available = Math.Max(0, size.Height - 8);
                panes.RowDefinitions[0].MinHeight = Math.Min(140, available * .35);
                panes.RowDefinitions[2].MinHeight = Math.Min(180, available * .45);
            }
        }
        ArrangePanes(default);
        panes.SizeChanged += (_, args) => ArrangePanes(args.NewSize);
        var empty = Ui.TextKey(nameof(L.DiagnosticsEmpty), 13, "muted");
        empty.HorizontalAlignment = HorizontalAlignment.Center; empty.VerticalAlignment = VerticalAlignment.Center;
        empty.Margin = new Thickness(24); empty.MaxWidth = 480;
        empty.Bind(IsVisibleProperty, new Binding(nameof(model.IsEmpty)));
        panes.Bind(IsVisibleProperty, new Binding("!" + nameof(model.IsEmpty)));
        var filterRow = FilterRow(model);
        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Children = { filterRow, panes, empty } };
        Grid.SetRow(panes, 1); Grid.SetRow(empty, 1);
        var schema = new DiagnosticsBodyEditor(wordWrap: false) { Name = "ProtocolSchemaDetail" };
        schema.Bind(DiagnosticsBodyEditor.SourceTextProperty, new Binding(nameof(model.SchemaDetail)));
        var explanation = Ui.TextKey(nameof(L.DiagnosticsSchemaHelp), 13, "muted");
        explanation.Margin = new Thickness(14, 8); explanation.TextWrapping = TextWrapping.Wrap;
        var schemaBody = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Children = { explanation, schema } };
        Grid.SetRow(schema, 1);
        var tabs = new TabControl { Name = "ProtocolDiagnosticTabs", Items =
        {
            new TabItem { Name = "ProtocolMessagesTab", Header = Ui.TextKey(nameof(L.DiagnosticsMessages), 13), Content = body },
            new TabItem { Name = "ProtocolSchemaTab", Header = Ui.TextKey(nameof(L.DiagnosticsObservedFields), 13), Content = schemaBody }
        } };
        if (console is not null)
        {
            var consoleTab = new TabItem { Name = "ProtocolConsoleTab", Header = Ui.TextKey(nameof(L.ConsoleTab), 13), Content = new ConsoleView(console) { Name = "ConsoleView" } };
            tabs.Items.Add(consoleTab);
            // The message count, Follow and Clear above the tabs belong to the protocol list, not the console.
            tabs.SelectionChanged += (_, _) => bar.IsVisible = !ReferenceEquals(tabs.SelectedItem, consoleTab);
        }
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Children = { bar, tabs } };
        Grid.SetRow(tabs, 1); Content = root;
    }

    /// <summary>
    /// Search above a bounded, scrollable kind picker. All and More stay reachable as the kind list grows.
    /// Chips and text filter the same retained entries; the view model rebuilds <see cref="ProtocolDiagnosticsViewModel.Visible"/>.
    /// </summary>
    private static Control FilterRow(ProtocolDiagnosticsViewModel model)
    {
        var all = new Button { Name = "AllKinds", Command = model.ClearKindsCommand, FontSize = 13, MinHeight = 32, Padding = new Thickness(10, 5) };
        all.Classes.Add("diag-chip");
        all.Bind(ContentControl.ContentProperty, LocalizedText.Binding(nameof(L.DiagnosticsAllKinds)));
        var chips = new ItemsControl
        {
            Name = "KindChips", ItemsSource = model.ChipKinds,
            ItemsPanel = new FuncTemplate<Panel?>(() => new WrapPanel { Orientation = Orientation.Horizontal }),
            ItemTemplate = new FuncDataTemplate<ProtocolDiagnosticKind>((_, _) =>
            {
                var chip = new ToggleButton { FontSize = 13, MinHeight = 32, Padding = new Thickness(10, 5), MaxWidth = 260 };
                chip.Classes.Add("diag-chip");
                // Keep the string content for accessibility and use a trimming template for unusually long kind names.
                chip.Bind(ContentControl.ContentProperty, new Binding(nameof(ProtocolDiagnosticKind.Label)));
                chip.ContentTemplate = new FuncDataTemplate<string>((text, _) => new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis });
                chip.Bind(ToolTip.TipProperty, new Binding(nameof(ProtocolDiagnosticKind.Label)));
                chip.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(ProtocolDiagnosticKind.IsActive)) { Mode = BindingMode.TwoWay });
                return chip;
            })
        };
        var more = new ToggleButton { Name = "MoreKinds", FontSize = 13, MinHeight = 32, Padding = new Thickness(10, 5) };
        more.Classes.Add("diag-chip");
        more.Bind(ContentControl.ContentProperty, new Binding(nameof(model.MoreKindsLabel)));
        more.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(model.KindsExpanded)) { Mode = BindingMode.TwoWay });
        more.Bind(IsVisibleProperty, new Binding(nameof(model.HasMoreKinds)));
        var picker = new ScrollViewer
        {
            Content = chips, MaxHeight = 72, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        var kinds = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Children = { all, picker, more } };
        Grid.SetColumn(picker, 1); Grid.SetColumn(more, 2);
        var filter = new TextBox { Name = "DiagnosticsFilter", FontSize = 13, MinHeight = 34, Padding = new Thickness(10, 6), HorizontalAlignment = HorizontalAlignment.Stretch };
        filter.Bind(TextBox.PlaceholderTextProperty, LocalizedText.Binding(nameof(L.DiagnosticsFilterPlaceholder)));
        filter.Bind(Avalonia.Automation.AutomationProperties.NameProperty, LocalizedText.Binding(nameof(L.DiagnosticsFilterPlaceholder)));
        filter.Bind(TextBox.TextProperty, new Binding(nameof(model.Filter)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        filter.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape || args.KeyModifiers != KeyModifiers.None) return;
            model.ClearFilterCommand.Execute(null); args.Handled = true;
        };
        var row = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto"), Margin = new Thickness(12, 8, 12, 8), Children = { filter, kinds } };
        Grid.SetRow(kinds, 1); kinds.Margin = new Thickness(0, 8, 0, 0);
        row.Bind(IsVisibleProperty, new Binding("!" + nameof(model.IsEmpty)));
        return row;
    }
}
