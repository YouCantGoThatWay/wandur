using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Wandur.Desktop.ViewModels;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.Views;

/// <summary>Form-based automation editor, with all state and execution owned by the view model.</summary>
public sealed class MacroLibraryView : UserControl
{
    public MacroLibraryView(MacroLibraryViewModel model, bool saveWithDialog = false)
    {
        DataContext = model;
        if (!saveWithDialog) KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.S, OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control), Command = model.SaveCommand });
        Binding TwoWay(string property) => new(property) { Mode = BindingMode.TwoWay };
        Button Action(string key, string name, System.Windows.Input.ICommand command, string icon) => Ui.ToolbarIconKey(new Button { Name = name, Command = command }, icon, key);
        var add = Action(nameof(L.MacroNew), "NewMacro", model.NewCommand, "M 8,2 V 14 M 2,8 H 14");
        var delete = Action(nameof(L.ScriptDelete), "DeleteMacro", model.RequestDeleteCommand, "M 2,4 H 14 M 6,4 V 2 H 10 V 4 M 4,4 L 5,14 H 11 L 12,4");
        var save = Action(nameof(L.ScriptSave), "SaveMacro", model.SaveCommand, "M 2,2 H 11 L 14,5 V 14 H 2 Z M 5,2 V 6 H 10 V 2 M 5,14 V 9 H 11 V 14");
        delete.Bind(IsEnabledProperty, new Binding(nameof(model.HasSelected))); save.Bind(IsEnabledProperty, new Binding(nameof(model.HasSelected)));
        save.IsVisible = !saveWithDialog;
        var name = new TextBox { Name = "MacroName", MaxLength = 120, MinHeight = 28, Height = 28, Padding = new Thickness(6, 2) };
        name.Bind(TextBox.TextProperty, TwoWay(nameof(model.Name))); name.Bind(IsEnabledProperty, new Binding(nameof(model.HasSelected)));
        name.Bind(TextBox.PlaceholderTextProperty, LocalizedText.Binding(nameof(L.ScriptName)));
        var enabled = new CheckBox { Name = "EnableMacro", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        enabled.Bind(ContentControl.ContentProperty, LocalizedText.Binding(nameof(L.ScriptEnabled)));
        enabled.Bind(ToggleButton.IsCheckedProperty, TwoWay(nameof(model.Enabled))); enabled.Bind(IsEnabledProperty, new Binding(nameof(model.HasSelected)));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Children = { add, delete, save } };
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 10, Children = { actions, name, enabled } };
        Grid.SetColumn(name, 1); Grid.SetColumn(enabled, 2);
        var toolbar = Ui.Toolbar(bar, "MacroToolbar");

        var list = new ListBox { Name = "MacroList", ItemsSource = model.Items };
        list.Classes.Add("script-library"); list.Bind(ListBox.SelectedItemProperty, TwoWay(nameof(model.Selected)));
        list.ItemTemplate = new FuncDataTemplate<ScriptLibraryItemViewModel>((item, _) =>
        {
            var title = Ui.Text("", 12); title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis;
            title.Bind(TextBlock.TextProperty, new Binding(nameof(ScriptLibraryItemViewModel.Name)));
            var status = Ui.Text("", 10, "muted"); status.Bind(TextBlock.TextProperty, new Binding(nameof(ScriptLibraryItemViewModel.Status)));
            return new StackPanel { Spacing = 3, Margin = new Thickness(3, 5), Children = { title, status } };
        });
        Control Field(string key, Control input, string? visible = null)
        {
            input.Bind(Avalonia.Automation.AutomationProperties.NameProperty, LocalizedText.Binding(key));
            var field = new StackPanel { Spacing = 6, Children = { Ui.TextKey(key, 12, "muted"), input } };
            if (visible is not null) field.Bind(IsVisibleProperty, new Binding(visible));
            return field;
        }
        var optionTemplate = new FuncDataTemplate<LocalizedChoiceViewModel>((_, _) =>
        {
            var label = Ui.Text("", 12); label.Bind(TextBlock.TextProperty, new Binding(nameof(LocalizedChoiceViewModel.Label))); return label;
        });
        var kind = new ComboBox { Name = "MacroKind", ItemTemplate = optionTemplate, HorizontalAlignment = HorizontalAlignment.Stretch };
        kind.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(model.Kinds)));
        kind.Bind(SelectingItemsControl.SelectedIndexProperty, TwoWay(nameof(model.KindIndex)));
        var pattern = new TextBox { Name = "MacroPattern", MaxLength = 1024 };
        pattern.Bind(TextBox.TextProperty, TwoWay(nameof(model.Pattern)));
        var match = new ComboBox { Name = "MacroMatch", ItemTemplate = optionTemplate, HorizontalAlignment = HorizontalAlignment.Stretch };
        match.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(model.Matches)));
        match.Bind(SelectingItemsControl.SelectedIndexProperty, TwoWay(nameof(model.MatchIndex)));
        var ignoreCase = new CheckBox { Name = "MacroIgnoreCase" };
        ignoreCase.Bind(ContentControl.ContentProperty, LocalizedText.Binding(nameof(L.MacroIgnoreCase)));
        ignoreCase.Bind(ToggleButton.IsCheckedProperty, TwoWay(nameof(model.IgnoreCase)));
        ignoreCase.Bind(IsVisibleProperty, new Binding(nameof(model.IsText)));
        var interval = new NumericUpDown { Name = "MacroInterval", Minimum = 1, Maximum = 86400, Increment = 1, FormatString = "0" };
        interval.Bind(NumericUpDown.ValueProperty, TwoWay(nameof(model.Interval)));
        var key = new ComboBox { Name = "MacroKey", ItemsSource = Enumerable.Range(1, 12).Select(i => "F" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray(), HorizontalAlignment = HorizontalAlignment.Stretch };
        key.Bind(SelectingItemsControl.SelectedItemProperty, TwoWay(nameof(model.ShortcutKey)));
        var commands = new TextBox { Name = "MacroCommands", AcceptsReturn = true, MinHeight = 140, MaxLength = 8192, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Menlo, Consolas, DejaVu Sans Mono") };
        commands.Bind(TextBox.TextProperty, TwoWay(nameof(model.Commands)));
        ScrollViewer.SetVerticalScrollBarVisibility(commands, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(commands, ScrollBarVisibility.Auto);
        var aliasHelp = Ui.TextKey(nameof(L.MacroAliasHelp), 12, "muted"); aliasHelp.Bind(IsVisibleProperty, new Binding(nameof(model.IsAlias)));
        var keyHelp = Ui.TextKey(nameof(L.MacroKeyHelp), 12, "muted"); keyHelp.Bind(IsVisibleProperty, new Binding(nameof(model.IsShortcut)));
        var form = new StackPanel { Name = "MacroForm", Spacing = 14, Margin = new Thickness(22, 18), MaxWidth = 660, HorizontalAlignment = HorizontalAlignment.Stretch, Children =
        {
            Field(nameof(L.MacroType), kind),
            Field(nameof(L.MacroPattern), pattern, nameof(model.IsText)),
            Field(nameof(L.MacroMatchLabel), match, nameof(model.IsTrigger)),
            ignoreCase, aliasHelp,
            Field(nameof(L.MacroInterval), interval, nameof(model.IsTimer)),
            Field(nameof(L.MacroKey), key, nameof(model.IsShortcut)), keyHelp,
            Field(nameof(L.MacroCommands), commands), Ui.TextKey(nameof(L.MacroCommandsHelp), 12, "muted"),
            Ui.TextKey(nameof(L.MacroHelp), 12, "muted")
        } };
        form.Bind(IsVisibleProperty, new Binding(nameof(model.HasSelected)));
        var empty = Ui.TextKey(nameof(L.MacroEmpty), 14, "muted"); empty.Margin = new Thickness(28);
        empty.Bind(IsVisibleProperty, new Binding(nameof(model.IsEmpty)));
        var formHost = new Grid { Children = { new ScrollViewer { Content = form }, empty } };
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("170,5,*"), Children = { list, new GridSplitter { Width = 5, ResizeDirection = GridResizeDirection.Columns }, formHost } };
        Grid.SetColumn(body.Children[1], 1); Grid.SetColumn(formHost, 2);
        var error = Ui.Text("", 12); error.Bind(TextBlock.TextProperty, new Binding(nameof(model.Error))); error.Bind(IsVisibleProperty, new Binding(nameof(model.HasError)));
        var confirmDelete = new Button { Name = "ConfirmDeleteMacro", Command = model.DeleteCommand };
        confirmDelete.Bind(ContentControl.ContentProperty, LocalizedText.Binding(nameof(L.ScriptDelete)));
        var cancel = new Button { Command = model.CancelDeleteCommand }; cancel.Bind(ContentControl.ContentProperty, LocalizedText.Binding(nameof(L.Cancel)));
        var confirm = new StackPanel { Spacing = 6, Children = { Ui.TextKey(nameof(L.MacroDeletePrompt), 12), new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { confirmDelete, cancel } } } };
        confirm.Bind(IsVisibleProperty, new Binding(nameof(model.ConfirmDelete)));
        var notices = new StackPanel { Margin = new Thickness(10, 4), Children = { error, confirm } };
        var status = Ui.Text("", 11, "muted"); status.Bind(TextBlock.TextProperty, new Binding(nameof(model.Status)));
        var unsaved = Ui.TextKey(nameof(L.ScriptUnsaved), 11, "muted"); unsaved.Bind(IsVisibleProperty, new Binding(nameof(model.HasUnsavedChanges)));
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 4), Spacing = 12, Children = { status, unsaved } };
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), Children = { toolbar, notices, body, footer } };
        Grid.SetRow(notices, 1); Grid.SetRow(body, 2); Grid.SetRow(footer, 3);
        Content = root;
    }
}
