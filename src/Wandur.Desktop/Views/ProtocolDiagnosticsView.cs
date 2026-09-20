using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
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
            Name = "ProtocolMessages", ItemsSource = model.Entries,
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
            if (args.AddedItems.Count > 0 && !ReferenceEquals(list.SelectedItem, model.Entries.LastOrDefault())) model.Follow = false;
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
        var body = new Grid { Children = { panes, empty } };
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
}
