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
        var count = Ui.Text("", 11, "muted");
        count.Name = "DiagnosticsCount";
        count.Bind(TextBlock.TextProperty, new Binding(nameof(model.CountLabel)));
        var clear = Ui.ToolbarIconKey(new Button { Name = "ClearDiagnostics", Command = model.ClearCommand },
            "M 3,4 H 13 M 6,4 V 2 H 10 V 4 M 4,4 L 5,14 H 11 L 12,4 M 7,6 V 12 M 9,6 V 12", nameof(L.DiagnosticsClear));
        var follow = new CheckBox { Name = "FollowDiagnostics", FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        follow.Bind(ContentControl.ContentProperty, LocalizedText.Binding(nameof(L.DiagnosticsFollow)));
        follow.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(model.Follow)) { Mode = BindingMode.TwoWay });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { follow, clear } };
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(12, 4), Children = { count, actions } };
        Grid.SetColumn(actions, 1); count.VerticalAlignment = VerticalAlignment.Center;
        var list = new ListBox
        {
            Name = "ProtocolMessages", ItemsSource = model.Visible,
            ItemTemplate = new FuncDataTemplate<ProtocolDiagnosticEntry>((entry, _) => new TextBlock
            {
                Text = entry?.Heading, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
                FontFamily = new FontFamily("Menlo, Consolas, DejaVu Sans Mono")
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
        var panes = new Grid { RowDefinitions = new RowDefinitions("*,5,2*"), Children = { list, detail } };
        Grid.SetRow(detail, 2);
        var divider = new GridSplitter { ResizeDirection = GridResizeDirection.Rows, HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetRow(divider, 1); panes.Children.Add(divider);
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
        var explanation = Ui.TextKey(nameof(L.DiagnosticsSchemaHelp), 12, "muted");
        explanation.Margin = new Thickness(14, 8); explanation.TextWrapping = TextWrapping.Wrap;
        var schemaBody = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Children = { explanation, schema } };
        Grid.SetRow(schema, 1);
        var tabs = new TabControl { Name = "ProtocolDiagnosticTabs", Items =
        {
            new TabItem { Name = "ProtocolMessagesTab", Header = Ui.TextKey(nameof(L.DiagnosticsMessages), 12), Content = body },
            new TabItem { Name = "ProtocolSchemaTab", Header = Ui.TextKey(nameof(L.DiagnosticsObservedFields), 12), Content = schemaBody }
        } };
        if (console is not null)
        {
            var consoleTab = new TabItem { Name = "ProtocolConsoleTab", Header = Ui.TextKey(nameof(L.ConsoleTab), 12), Content = new ConsoleView(console) { Name = "ConsoleView" } };
            tabs.Items.Add(consoleTab);
            // The message count, Follow and Clear above the tabs belong to the protocol list, not the console.
            tabs.SelectionChanged += (_, _) => bar.IsVisible = !ReferenceEquals(tabs.SelectedItem, consoleTab);
        }
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Children = { bar, tabs } };
        Grid.SetRow(tabs, 1); Content = root;
    }

    /// <summary>
    /// The kinds seen as toggle chips ("All" first, a "more" expander past the cap) with the filter box beside them.
    /// Chips and text filter the same retained entries; the view model rebuilds <see cref="ProtocolDiagnosticsViewModel.Visible"/>.
    /// </summary>
    private static Control FilterRow(ProtocolDiagnosticsViewModel model)
    {
        var all = new Button { Name = "AllKinds", Command = model.ClearKindsCommand };
        all.Classes.Add("diag-chip");
        all.Bind(ContentControl.ContentProperty, LocalizedText.Binding(nameof(L.DiagnosticsAllKinds)));
        var chips = new ItemsControl
        {
            Name = "KindChips", ItemsSource = model.ChipKinds,
            ItemsPanel = new FuncTemplate<Panel?>(() => new WrapPanel { Orientation = Orientation.Horizontal }),
            ItemTemplate = new FuncDataTemplate<ProtocolDiagnosticKind>((_, _) =>
            {
                var chip = new ToggleButton();
                chip.Classes.Add("diag-chip");
                chip.Bind(ContentControl.ContentProperty, new Binding(nameof(ProtocolDiagnosticKind.Label)));
                chip.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(ProtocolDiagnosticKind.IsActive)) { Mode = BindingMode.TwoWay });
                return chip;
            })
        };
        var more = new ToggleButton { Name = "MoreKinds" };
        more.Classes.Add("diag-chip");
        more.Bind(ContentControl.ContentProperty, new Binding(nameof(model.MoreKindsLabel)));
        more.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(model.KindsExpanded)) { Mode = BindingMode.TwoWay });
        more.Bind(IsVisibleProperty, new Binding(nameof(model.HasMoreKinds)));
        var kinds = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Children = { all, chips, more } };
        Grid.SetColumn(chips, 1); Grid.SetColumn(more, 2);
        var filter = new TextBox { Name = "DiagnosticsFilter", Width = 200, FontSize = 11, MinHeight = 0, Padding = new Thickness(8, 3), VerticalAlignment = VerticalAlignment.Top };
        filter.Bind(TextBox.PlaceholderTextProperty, LocalizedText.Binding(nameof(L.DiagnosticsFilterPlaceholder)));
        filter.Bind(TextBox.TextProperty, new Binding(nameof(model.Filter)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        filter.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape || args.KeyModifiers != KeyModifiers.None) return;
            model.ClearFilterCommand.Execute(null); args.Handled = true;
        };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(12, 6, 12, 2), Children = { kinds, filter } };
        Grid.SetColumn(filter, 1); filter.Margin = new Thickness(12, 0, 0, 0);
        row.Bind(IsVisibleProperty, new Binding("!" + nameof(model.IsEmpty)));
        return row;
    }
}
