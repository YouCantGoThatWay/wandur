using L = Wandur.Core.Localization.Strings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using Wandur.Core.Settings;
using Wandur.Desktop.ViewModels;

namespace Wandur.Desktop.Views;

public sealed class WorldLibraryView : UserControl
{
    private readonly WorldLibraryViewModel _model;

    public WorldLibraryView(SessionWorkspace sessions, Action addWorld, Action? browseWorlds = null, Action<ConnectionProfile>? editProfile = null)
    {
        _model = new WorldLibraryViewModel(sessions, addWorld, browseWorlds, editProfile);
        DataContext = _model;
        var worlds = new ListBox { Name = "WorldProfiles", MinHeight = 80, Margin = new Thickness(4, 6), Background = Brushes.Transparent };
        worlds.Classes.Add("world-list");
        worlds.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(_model.Profiles)));
        worlds.Bind(ListBox.SelectedItemProperty, new Binding(nameof(_model.SelectedProfile)) { Mode = BindingMode.TwoWay });
        worlds.Bind(IsVisibleProperty, new Binding(nameof(_model.HasWorlds)));
        worlds.ItemTemplate = new FuncDataTemplate<ConnectionProfile>((profile, _) =>
        {
            if (profile is null) return null;
            var name = Ui.Text(profile.Name, 13);
            name.FontWeight = FontWeight.Medium;
            name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis;
            var endpoint = $"{profile.Host}:{profile.Port}" + (profile.UseTls ? " · TLS" : "");
            var details = Ui.Text(endpoint, 10, "muted");
            details.TextWrapping = TextWrapping.NoWrap; details.TextTrimming = TextTrimming.CharacterEllipsis;
            var text = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center, Children = { name, details } };
            // The picture sits on the left at a fixed size, so the two text lines keep the row height they had.
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8, Children = { new WorldThumbnail(profile, sessions.Thumbnails), text } };
            Grid.SetColumn(text, 1);
            ToolTip.SetTip(row, L.Format(L.DoubleClickToConnect, profile.Name, endpoint));
            return row;
        });
        // A reorder waits while the pointer is over the list or a row menu is open, and lands when the pointer leaves.
        var menuOpen = false;
        _model.IsBusy = () => worlds.IsPointerOver || menuOpen;
        worlds.PointerExited += (_, _) => { if (!menuOpen) _model.ApplyPendingOrder(); };
        worlds.ContainerPrepared += (_, args) =>
        {
            if (args.Container is not ListBoxItem item || item.Content is not ConnectionProfile profile) return;
            item.ContextMenu = new ContextMenu
            {
                ItemsSource = new[]
                {
                    new MenuItem { [!MenuItem.HeaderProperty] = LocalizedText.Binding(nameof(L.Edit2)), Name = "EditWorldMenu", Command = _model.EditProfileCommand, CommandParameter = profile },
                    new MenuItem { [!MenuItem.HeaderProperty] = LocalizedText.Binding(nameof(L.DeleteSavedWorld)), Name = "DeleteWorldMenu", Command = _model.RequestDeleteProfileCommand, CommandParameter = profile }
                }
            };
            item.ContextMenu.Opening += (_, _) => { menuOpen = true; _model.SelectedProfile = profile; };
            item.ContextMenu.Closed += (_, _) => { menuOpen = false; if (!worlds.IsPointerOver) _model.ApplyPendingOrder(); };
        };
        worlds.ContainerClearing += (_, args) =>
        {
            args.Container.ContextMenu?.Close();
            args.Container.ContextMenu = null;
        };
        worlds.AddHandler(PointerPressedEvent, (_, args) =>
        {
            if (args.Source is not Visual source ||
                source.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault()?.Content is not ConnectionProfile profile) return;
            var pointer = args.GetCurrentPoint(worlds).Properties;
            if (!pointer.IsLeftButtonPressed && !pointer.IsRightButtonPressed) return;
            _model.SelectedProfile = profile;
        }, RoutingStrategies.Tunnel);
        worlds.DoubleTapped += async (_, _) => await _model.ConnectCommand.ExecuteAsync(null);
        worlds.KeyDown += async (_, args) =>
        {
            if (args.Key == Key.Enter) { args.Handled = true; await _model.ConnectCommand.ExecuteAsync(null); }
        };
        Button ActionButton(string label, string name, System.Windows.Input.ICommand command, string geometry)
            => Ui.ToolbarIconKey(new Button { Name = name, Command = command }, geometry, label);
        var connect = ActionButton(nameof(L.ConnectToSelectedWorld), "ConnectSavedWorld", _model.ConnectCommand, "M 4,2 L 13,8 L 4,14 Z");
        var add = ActionButton(nameof(L.AddAWorld), "AddSavedWorld", _model.AddCommand, "M 8,2 V 14 M 2,8 H 14");
        var edit = ActionButton(nameof(L.Edit), "EditSavedWorld", _model.EditCommand, "M 2,10 L 10,2 L 14,6 L 6,14 L 2,14 Z M 8,4 L 12,8");
        var delete = ActionButton(nameof(L.DeleteSavedWorld), "DeleteSavedWorld", _model.RequestDeleteCommand, "M 2,4 H 14 M 6,4 V 2 H 10 V 4 M 4,4 L 5,14 H 11 L 12,4 M 7,7 V 11 M 9,7 V 11");
        var browse = ActionButton(nameof(L.FindAMUDInTheDirectory), "BrowseWorlds", _model.BrowseCommand, "M 11,6 A 5,5 0 1 1 1,6 A 5,5 0 1 1 11,6 M 10,10 L 15,15");
        browse.IsVisible = _model.CanBrowse;
        var actions = new WrapPanel { Orientation = Orientation.Horizontal, Children = { connect, add, edit, delete, browse } };
        var toolbar = Ui.Toolbar(actions, "WorldLibraryToolbar");
        var empty = Ui.TextKey(nameof(L.KeepYourFavoriteWorldsHereAddOneToStart), 12, "muted");
        empty.Margin = new Thickness(14); empty.VerticalAlignment = VerticalAlignment.Top;
        empty.Bind(IsVisibleProperty, new Binding(nameof(_model.IsEmpty)));
        var content = new Grid { Children = { worlds, empty } };

        // The prompt names the world, because the toolbar button acts on the selection and the reader
        // cannot otherwise be sure which row it caught.
        var prompt = Ui.TextKey(nameof(L.DeleteSavedWorldPrompt), 12);
        prompt.TextWrapping = TextWrapping.Wrap;
        var named = Ui.Text("", 12); named.FontWeight = FontWeight.SemiBold;
        named.Bind(TextBlock.TextProperty, new Binding(nameof(_model.PendingDeleteName)));
        var confirmDelete = new Button { Name = "ConfirmDeleteWorld", Command = _model.DeleteCommand };
        confirmDelete.Bind(ContentControl.ContentProperty, LocalizedText.Binding(nameof(L.DeleteSavedWorld)));
        var cancelDelete = new Button { Name = "CancelDeleteWorld", Command = _model.CancelDeleteCommand };
        cancelDelete.Bind(ContentControl.ContentProperty, LocalizedText.Binding(nameof(L.Cancel)));
        var confirm = new StackPanel
        {
            Name = "DeleteWorldConfirm",
            Spacing = 6,
            Margin = new Thickness(10, 8),
            Children =
            {
                named, prompt,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    Children = { confirmDelete, cancelDelete }
                }
            }
        };
        confirm.Bind(IsVisibleProperty, new Binding(nameof(_model.ConfirmDelete)));

        var panel = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), Children = { toolbar, confirm, content } };
        Grid.SetRow(confirm, 1); Grid.SetRow(content, 2); Content = panel;
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); _model.Attach(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { _model.Detach(); base.OnDetachedFromVisualTree(e); }
}
