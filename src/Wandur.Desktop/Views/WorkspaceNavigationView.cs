using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Wandur.Desktop.ViewModels;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.Views;

public sealed class WorkspaceNavigationView : UserControl
{
    private readonly WorkspaceNavigationViewModel _model;
    public WorkspaceNavigationView(WorkspaceNavigationViewModel model, WorldLibraryView library)
    {
        _model = model; DataContext = model;
        var list = new ListBox { Name = "WorkspaceItems", Background = Brushes.Transparent, Margin = new Thickness(4), MinHeight = 100 };
        list.Classes.Add("world-list");
        list.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(model.OpenEntries)));
        list.Bind(ListBox.SelectedItemProperty, new Binding(nameof(model.Selected)) { Mode = BindingMode.TwoWay });
        list.ItemTemplate = new FuncDataTemplate<WorkspaceNavigationEntry>((entry, _) =>
        {
            if (entry is null) return null;
            var title = Ui.Text("", 13);
            title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis;
            title.Bind(TextBlock.TextProperty, new Binding(nameof(entry.Title)));
            var details = Ui.Text("", 10, "muted");
            details.TextWrapping = TextWrapping.NoWrap; details.TextTrimming = TextTrimming.CharacterEllipsis;
            details.Bind(TextBlock.TextProperty, new Binding(nameof(entry.Details)));
            details.IsVisible = entry.HasDetails;
            var status = Ui.Text("", 10, "muted");
            status.Bind(TextBlock.TextProperty, new Binding(nameof(entry.Status)));
            var close = new Button { Content = "×", Command = entry.CloseCommand, IsVisible = entry.CanClose, Name = "CloseWorkspaceItem", VerticalAlignment = VerticalAlignment.Center };
            close.Classes.Add("tab-close");
            close.Bind(ToolTip.TipProperty, LocalizedText.Binding(nameof(L.CloseWorkspaceItem)));
            close.Bind(Avalonia.Automation.AutomationProperties.NameProperty, LocalizedText.Binding(nameof(L.CloseWorkspaceItem)));
            var text = new StackPanel { Spacing = 3, Children = { title, details } };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 6, Margin = new Thickness(entry.IsChild ? 16 : 0, 2, 0, 2), Children = { text, status, close } };
            Grid.SetColumn(status, 1); Grid.SetColumn(close, 2);
            row.Bind(ToolTip.TipProperty, new Binding(nameof(entry.Details)));
            return row;
        });
        list.ContainerPrepared += (_, args) =>
        {
            if (args.Container is not ListBoxItem item || item.Content is not WorkspaceNavigationEntry entry) return;
            item.ContextMenu = new ContextMenu { ItemsSource = new[]
            {
                new MenuItem { [!MenuItem.HeaderProperty] = LocalizedText.Binding(nameof(L.OpenWorkspaceItem)), Command = entry.OpenCommand },
                new MenuItem { [!MenuItem.HeaderProperty] = LocalizedText.Binding(nameof(L.RenameSession)), Command = entry.RenameCommand, IsVisible = entry.CanRename },
                new MenuItem { [!MenuItem.HeaderProperty] = LocalizedText.Binding(nameof(L.FloatWorkspaceItem)), Command = entry.FloatCommand, IsVisible = entry.CanFloat },
                new MenuItem { [!MenuItem.HeaderProperty] = LocalizedText.Binding(nameof(L.CloseWorkspaceItem)), Command = entry.CloseCommand, IsVisible = entry.CanClose }
            } };
        };
        list.ContainerClearing += (_, args) => { args.Container.ContextMenu?.Close(); args.Container.ContextMenu = null; };
        var savedToggle = new ToggleButton { Name = "SavedWorldsSection", IsChecked = true, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
        var savedLabel = Ui.TextKey(nameof(L.SavedWorlds), 12);
        var chevron = Ui.Text("▾", 12, "muted");
        var savedHeading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { savedLabel, chevron } };
        Grid.SetColumn(chevron, 1); savedToggle.Content = savedHeading;
        savedToggle.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        savedToggle.IsCheckedChanged += (_, _) => chevron.Text = savedToggle.IsChecked == true ? "▾" : "▸";
        savedToggle.Bind(Avalonia.Automation.AutomationProperties.NameProperty, LocalizedText.Binding(nameof(L.SavedWorlds)));
        savedToggle.Classes.Add("workspace-nav");
        library.MinHeight = 120; library.MaxHeight = 320;
        library.Bind(IsVisibleProperty, new Binding(nameof(ToggleButton.IsChecked)) { Source = savedToggle });
        var saved = new StackPanel { Children = { savedToggle, library } };
        var browse = new ToggleButton { Name = "WorkspaceFindMud", Command = model.BrowseCommand, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
        browse.Bind(ContentControl.ContentProperty, LocalizedText.Binding(nameof(L.FindAMUD)));
        browse.Classes.Add("workspace-nav");
        browse.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(model.IsSearchSelected)) { Mode = BindingMode.OneWay });
        var heading = Ui.TextKey(nameof(L.OpenSessions), 11, "muted"); heading.Margin = new Thickness(12, 10, 0, 4);
        var top = new StackPanel { Children = { browse, heading } };
        var name = new TextBox { Name = "SessionName", MaxLength = 100 };
        name.Bind(TextBox.TextProperty, new Binding(nameof(model.RenameText)) { Mode = BindingMode.TwoWay });
        name.Bind(TextBox.PlaceholderTextProperty, LocalizedText.Binding(nameof(L.SessionName)));
        var save = new Button { Command = model.SaveNameCommand };
        save.Bind(ContentControl.ContentProperty, LocalizedText.Binding(nameof(L.RenameSession)));
        var cancel = new Button { Command = model.CancelRenameCommand };
        cancel.Bind(ContentControl.ContentProperty, LocalizedText.Binding(nameof(L.Cancel)));
        var rename = new StackPanel { Spacing = 6, Margin = new Thickness(10), Children = { name, new WrapPanel { Children = { save, cancel } } } };
        rename.Bind(IsVisibleProperty, new Binding(nameof(model.IsRenaming)));
        rename.PropertyChanged += (_, args) => { if (args.Property == IsVisibleProperty && rename.IsVisible) Avalonia.Threading.Dispatcher.UIThread.Post(() => { name.Focus(); name.SelectAll(); }); };
        name.KeyDown += (_, args) =>
        {
            if (args.Key == Avalonia.Input.Key.Enter) { model.SaveNameCommand.Execute(null); args.Handled = true; }
            else if (args.Key == Avalonia.Input.Key.Escape) { model.CancelRenameCommand.Execute(null); args.Handled = true; }
        };
        list.KeyDown += (_, args) => { if (args.Key == Avalonia.Input.Key.F2 && model.Selected?.CanRename == true) { model.Selected.RenameCommand.Execute(null); args.Handled = true; } };
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"), Children = { top, list, rename, saved } };
        Grid.SetRow(list, 1); Grid.SetRow(rename, 2); Grid.SetRow(saved, 3); Content = layout;
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); _model.Attach(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { _model.Detach(); base.OnDetachedFromVisualTree(e); }
}
