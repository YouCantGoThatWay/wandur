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

/// <summary>Session script editor. Execution and drafts belong to the session, not this view.</summary>
public sealed class ScriptLibraryView : UserControl
{
    public ScriptLibraryView(ScriptLibraryViewModel model, bool saveWithDialog = false)
    {
        DataContext = model;
        if (!saveWithDialog) KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.S, OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control), Command = model.SaveCommand });
        Button Action(string label, string name, System.Windows.Input.ICommand command, string icon)
            => Ui.ToolbarIconKey(new Button { Name = name, Command = command }, icon, label);

        var add = Action(nameof(L.ScriptNew), "NewWorldScript", model.NewCommand, "M 8,2 V 14 M 2,8 H 14");
        var delete = Action(nameof(L.ScriptDelete), "DeleteWorldScript", model.RequestDeleteCommand, "M 2,4 H 14 M 6,4 V 2 H 10 V 4 M 4,4 L 5,14 H 11 L 12,4 M 7,7 V 11 M 9,7 V 11");
        delete.Bind(IsEnabledProperty, new Binding(nameof(model.HasSelected)));
        var duplicate = Action(nameof(L.ScriptDuplicate), "DuplicateWorldScript", model.DuplicateCommand, "M 5,2 H 13 V 11 M 3,5 H 11 V 14 H 3 Z");
        duplicate.Bind(IsEnabledProperty, new Binding(nameof(model.HasSelected)));
        var save = Action(nameof(L.ScriptSave), "SaveWorldScript", model.SaveCommand, "M 2,2 H 11 L 14,5 V 14 H 2 Z M 5,2 V 6 H 10 V 2 M 5,14 V 9 H 11 V 14");
        save.Bind(ToolTip.TipProperty, LocalizedText.Binding(nameof(L.ScriptLibrarySaveHint)));
        save.IsVisible = !saveWithDialog;
        var nameBox = new TextBox { Name = "WorldScriptName", MaxLength = 120, MinWidth = 80, MinHeight = 28, Height = 28, Padding = new Thickness(6, 2), VerticalContentAlignment = VerticalAlignment.Center };
        nameBox.Bind(TextBox.PlaceholderTextProperty, LocalizedText.Binding(nameof(L.ScriptName)));
        nameBox.Bind(TextBox.TextProperty, new Binding(nameof(model.Name)) { Mode = BindingMode.TwoWay });
        nameBox.Bind(IsEnabledProperty, new Binding(nameof(model.HasSelected)));
        var enabled = new CheckBox { Name = "EnableWorldScript", FontSize = 11, Margin = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Center };
        enabled.Bind(ContentControl.ContentProperty, LocalizedText.Binding(nameof(L.ScriptEnabled)));
        enabled.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(model.Enabled)) { Mode = BindingMode.TwoWay });
        enabled.Bind(ToolTip.TipProperty, LocalizedText.Binding(nameof(L.ScriptEnableHint)));
        enabled.Bind(IsEnabledProperty, new Binding(nameof(model.HasSelected)));
        var outputToggle = Ui.ToolbarIconKey(new ToggleButton { Name = "ToggleScriptOutput", IsChecked = false }, "M 2,3 H 14 V 13 H 2 Z M 5,6 L 7,8 L 5,10 M 9,10 H 12", nameof(L.ScriptOutputLabel));
        var help = Ui.ToolbarIconKey(new Button { Name = "ScriptHelp" }, "M 8,15 A 7,7 0 1 1 8,1 A 7,7 0 1 1 8,15 M 6,5 C 6,2 12,3 10,6 L 8,8 V 9 M 8,11 V 12", nameof(L.ScriptHelpTitle));
        var examples = new SelectableTextBlock { FontFamily = new FontFamily("Menlo, Consolas, DejaVu Sans Mono"), FontSize = 12 };
        examples.Bind(TextBlock.TextProperty, LocalizedText.Binding(nameof(L.ScriptApiExamples)));
        help.Flyout = new Flyout { Content = new StackPanel { Spacing = 10, MaxWidth = 560, Children =
        {
            Ui.TextKey(nameof(L.ScriptCompletionHint), 12, "muted"),
            new ScrollViewer { MaxHeight = 320, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Content = examples }
        } } };
        var allowSend = new CheckBox { Name = "AllowPackScriptSend", FontSize = 11, Margin = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Center };
        allowSend.Bind(ContentControl.ContentProperty, LocalizedText.Binding(nameof(L.ScriptPackAllowSend)));
        allowSend.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(model.AllowSend)) { Mode = BindingMode.TwoWay });
        allowSend.Bind(IsVisibleProperty, new Binding(nameof(model.IsPack)));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Children = { add, duplicate, delete, save } };
        var utilities = new StackPanel { Orientation = Orientation.Horizontal, Children = { allowSend, enabled, outputToggle, help } };
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8, Children = { actions, nameBox, utilities } };
        Grid.SetColumn(nameBox, 1); Grid.SetColumn(utilities, 2);
        var toolbar = Ui.Toolbar(bar, "ScriptEditorToolbar");

        var list = new ListBox { Name = "WorldScriptList", ItemsSource = model.Items, MinWidth = 110 };
        list.Classes.Add("script-library");
        list.Bind(ListBox.SelectedItemProperty, new Binding(nameof(model.Selected)) { Mode = BindingMode.TwoWay });
        list.ItemTemplate = new FuncDataTemplate<ScriptLibraryItemViewModel>((item, _) =>
        {
            if (item is null) return null;
            var name = Ui.Text("", 12); name.TextTrimming = TextTrimming.CharacterEllipsis; name.TextWrapping = TextWrapping.NoWrap;
            name.Bind(TextBlock.TextProperty, new Binding(nameof(item.Name)));
            var status = Ui.Text("", 10, "muted"); status.Bind(TextBlock.TextProperty, new Binding(nameof(item.Status)));
            var pack = Ui.Text("", 10, "muted"); pack.Name = "WorldScriptPackMarker";
            pack.Bind(TextBlock.TextProperty, new Binding(nameof(item.PackLabel)));
            pack.Bind(IsVisibleProperty, new Binding(nameof(item.IsPack)));
            return new StackPanel { Spacing = 3, Margin = new Thickness(3, 4), Children = { name, status, pack } };
        });
        var source = new ScriptCodeEditor { Name = "WorldScriptSource" };
        source.Bind(ScriptCodeEditor.SourceTextProperty, new Binding(nameof(model.Source)) { Mode = BindingMode.TwoWay });
        source.Bind(IsVisibleProperty, new Binding(nameof(model.HasSelected)));
        // A supplied script is shown, never edited in place; the user duplicates it to make changes.
        source.Bind(AvaloniaEdit.TextEditor.IsReadOnlyProperty, new Binding(nameof(model.IsReadOnly)));
        nameBox.Bind(TextBox.IsReadOnlyProperty, new Binding(nameof(model.IsReadOnly)));
        var empty = Ui.TextKey(nameof(L.ScriptLibraryEmpty), 14, "muted"); empty.Margin = new Thickness(20);
        empty.Bind(IsVisibleProperty, new Binding(nameof(model.IsEmpty)));
        var editorHost = new Grid { Children = { source, empty } };
        var body = new Grid { Name = "ScriptEditorBody", Margin = new Thickness(4), ColumnDefinitions = new ColumnDefinitions("150,5,*"), Children = { list, new GridSplitter { Width = 5, ResizeDirection = GridResizeDirection.Columns }, editorHost } };
        Grid.SetColumn(body.Children[1], 1); Grid.SetColumn(editorHost, 2);

        var error = Ui.Text("", 12); error.Bind(TextBlock.TextProperty, new Binding(nameof(model.Error)));
        var errorScroll = new ScrollViewer { Content = error, MaxHeight = 70, Margin = new Thickness(8, 4), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        errorScroll.Bind(IsVisibleProperty, new Binding(nameof(model.HasError)));
        var confirmDelete = new Button { Name = "ConfirmDeleteWorldScript", Command = model.DeleteCommand };
        confirmDelete.Bind(ContentControl.ContentProperty, LocalizedText.Binding(nameof(L.ScriptDelete)));
        var cancelDelete = new Button { Name = "CancelDeleteWorldScript", Command = model.CancelDeleteCommand };
        cancelDelete.Bind(ContentControl.ContentProperty, LocalizedText.Binding(nameof(L.Cancel)));
        var confirm = new StackPanel { Spacing = 8, Margin = new Thickness(8), Children =
        {
            Ui.TextKey(nameof(L.ScriptDeletePrompt), 12),
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { confirmDelete, cancelDelete } }
        } };
        confirm.Bind(IsVisibleProperty, new Binding(nameof(model.ConfirmDelete)));
        var packLabel = Ui.Text("", 11, "muted"); packLabel.Name = "PackScriptProvenance";
        packLabel.Bind(TextBlock.TextProperty, new Binding(nameof(model.PackLabel)));
        var packNotice = Ui.TextKey(nameof(L.ScriptPackReadOnly), 11, "muted");
        packNotice.TextWrapping = TextWrapping.Wrap;
        var packDescription = Ui.Text("", 11, "muted"); packDescription.TextWrapping = TextWrapping.Wrap;
        packDescription.Bind(TextBlock.TextProperty, new Binding(nameof(model.PackDescription)));
        packDescription.Bind(IsVisibleProperty, new Binding(nameof(model.HasPackDescription)));
        var pack = new StackPanel { Name = "PackScriptNotice", Spacing = 3, Margin = new Thickness(10, 4), Children = { packLabel, packNotice, packDescription } };
        pack.Bind(IsVisibleProperty, new Binding(nameof(model.IsPack)));
        var notices = new StackPanel { Children = { pack, errorScroll, confirm } };

        var output = new TextBox { Name = "WorldScriptOutput", IsReadOnly = true, AcceptsReturn = true, FontFamily = new FontFamily("Menlo, Consolas, DejaVu Sans Mono"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Height = 130, Margin = new Thickness(4) };
        output.Bind(TextBox.TextProperty, new Binding(nameof(model.Log)));
        output.Bind(IsVisibleProperty, new Binding(nameof(ToggleButton.IsChecked)) { Source = outputToggle });
        ScrollViewer.SetVerticalScrollBarVisibility(output, ScrollBarVisibility.Auto);
        var statusText = Ui.Text("", 11, "muted"); statusText.Bind(TextBlock.TextProperty, new Binding(nameof(model.Status)));
        var unsaved = Ui.TextKey(nameof(L.ScriptUnsaved), 11, "muted"); unsaved.Bind(IsVisibleProperty, new Binding(nameof(model.HasUnsavedChanges)));
        var statusBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Margin = new Thickness(10, 4), Children = { statusText, unsaved } };
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto"), Children = { toolbar, notices, body, output, statusBar } };
        Grid.SetRow(notices, 1); Grid.SetRow(body, 2); Grid.SetRow(output, 3); Grid.SetRow(statusBar, 4);
        Content = root;
    }
}
