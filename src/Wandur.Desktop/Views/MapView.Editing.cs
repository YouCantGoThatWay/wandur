using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using System.Globalization;
using Avalonia.Layout;
using Avalonia.Media;
using Wandur.Core.Mapping;
using Wandur.Desktop.ViewModels;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.Views;

public sealed partial class MapView
{
    partial void AddNavigationControls(StackPanel controls);

    private sealed class PreserveCustomEnvironmentConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value;
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is string key ? key : BindingOperations.DoNothing;
    }

    private static readonly IDataTemplate RoomTemplate = new FuncDataTemplate<MapRoom>((room, _) =>
        new TextBlock { Text = room is null ? "" : $"{room.Name} · {room.ServerId ?? room.Id}", TextTrimming = TextTrimming.CharacterEllipsis });

    private static Control Field(string label, Control input) => new StackPanel
    { Spacing = 4, Children = { Ui.TextKey(label, 11, "muted"), input } };

    private TextBox EditorText(string name, string property, bool multiline = false)
    {
        var box = new TextBox { Name = name, FontSize = 12, AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, MinHeight = multiline ? 64 : 32,
            MaxHeight = multiline ? 110 : double.PositiveInfinity };
        box.Bind(TextBox.TextProperty, new Binding(property) { Mode = BindingMode.TwoWay });
        return box;
    }
    private static Button EditorAction(string label, string name, System.Windows.Input.ICommand command)
    {
        var button = new Button { Name = name, [!ContentControl.ContentProperty] = LocalizedText.Binding(label), Command = command, FontSize = 11, Padding = new Thickness(8, 6), Margin = new Thickness(0, 0, 6, 4) };
        button.Classes.Add("app-button"); return button;
    }
    private static CheckBox Check(string label, string name, string property)
    {
        var check = new CheckBox { Name = name, Content = Ui.TextKey(label, 11), MinHeight = 28 };
        check.Bind(ToggleButton.IsCheckedProperty, new Binding(property) { Mode = BindingMode.TwoWay });
        return check;
    }
    private ComboBox RoomChoice(string name, string property)
    {
        var choice = new ComboBox { Name = name, ItemTemplate = RoomTemplate, HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 32 };
        choice.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Model.OtherRooms)));
        choice.Bind(SelectingItemsControl.SelectedItemProperty, new Binding(property) { Mode = BindingMode.TwoWay });
        return choice;
    }
    private Control CreateRoomEditor()
    {
        var coordinates = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*") };
        var x = Field(nameof(L.MapCoordinateX), EditorText("MapRoomX", "RoomEditor.X"));
        var y = Field(nameof(L.MapCoordinateY), EditorText("MapRoomY", "RoomEditor.Y")); y.Margin = new Thickness(6, 0);
        var z = Field(nameof(L.MapCoordinateZ), EditorText("MapRoomZ", "RoomEditor.Z"));
        Grid.SetColumn(y, 1); Grid.SetColumn(z, 2); coordinates.Children.Add(x); coordinates.Children.Add(y); coordinates.Children.Add(z);
        var terrain = new ComboBox
        {
            Name = "MapTerrainPreset", ItemsSource = MapEnvironmentPalette.Styles,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemTemplate = new FuncDataTemplate<MapEnvironmentStyle>((style, _) => style is null ? null : Ui.TextKey(MapEnvironmentPalette.LabelKey(style.Key))),
            SelectedValueBinding = new Binding(nameof(MapEnvironmentStyle.Key))
        };
        terrain.Bind(SelectingItemsControl.SelectedValueProperty, new Binding("RoomEditor.Environment") { Mode = BindingMode.TwoWay, Converter = new PreserveCustomEnvironmentConverter() });
        var terrainProvenance = Ui.Text("", 11, "muted");
        terrainProvenance.Bind(TextBlock.TextProperty, new Binding("RoomEditor.TerrainProvenance"));
        terrainProvenance.Bind(IsVisibleProperty, new Binding("RoomEditor.HasTerrainProvenance"));
        var room = new StackPanel { Spacing = 7, Children =
        {
            Field(nameof(L.MapRoomName), EditorText("MapRoomName", "RoomEditor.Name")),
            Field(nameof(L.MapRoomDescription), EditorText("MapRoomDescription", "RoomEditor.Description", true)),
            Field(nameof(L.MapArea), EditorText("MapRoomArea", "RoomEditor.Area")), coordinates,
            Field(nameof(L.MapRoomEnvironment), new StackPanel { Spacing = 4, Children = { terrain, EditorText("MapRoomEnvironment", "RoomEditor.Environment"), terrainProvenance } }),
            Field(nameof(L.MapRoomColor), EditorText("MapRoomColor", "RoomEditor.Color")),
            Field(nameof(L.MapRoomSymbol), EditorText("MapRoomSymbol", "RoomEditor.Symbol")),
            Field(nameof(L.MapRoomNotes), EditorText("MapRoomNotes", "RoomEditor.Notes", true)),
            Field(nameof(L.MapRoomWeight), EditorText("MapRoomWeight", "RoomEditor.Weight")),
            Check(nameof(L.MapRoomLocked), "MapRoomLocked", "RoomEditor.IsLocked"),
            new WrapPanel { Children =
            {
                EditorAction(nameof(L.MapSaveRoom), "MapSaveRoom", Model.SaveRoomCommand),
                EditorAction(nameof(L.MapDeleteRoom), "MapDeleteRoom", Model.DeleteRoomCommand),
                EditorAction(nameof(L.MapSetCurrentRoom), "MapSetCurrentRoom", Model.SetCurrentRoomCommand)
            } },
            Field(nameof(L.MapMergeTarget), RoomChoice("MapMergeTarget", nameof(Model.MergeTarget))),
            EditorAction(nameof(L.MapMergeRoom), "MapMergeRoom", Model.MergeRoomCommand)
        } };
        room.Bind(IsVisibleProperty, new Binding(nameof(Model.ShowRoomEditor)));
        return room;
    }
    private Control CreateExitEditor()
    {
        var exits = new ComboBox
        {
            Name = "MapExitChoice", HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemTemplate = new FuncDataTemplate<MapLink>((link, _) => new TextBlock
            { Text = link is null ? "" : $"{link.Direction} → {Model.Snapshot.Rooms.FirstOrDefault(r => r.Id == link.ToId)?.Name ?? link.ToId}", TextTrimming = TextTrimming.CharacterEllipsis })
        };
        exits.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Model.SelectedRoomExits)));
        exits.Bind(SelectingItemsControl.SelectedItemProperty, new Binding(nameof(Model.SelectedExit)) { Mode = BindingMode.TwoWay });
        var doors = new ComboBox { Name = "MapExitDoor", ItemsSource = Model.DoorChoices, HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemTemplate = new FuncDataTemplate<MapDoorChoice>((door, _) => door is null ? null : Ui.TextKey(door.Value switch { MapDoorState.Open => nameof(L.MapDoorOpen), MapDoorState.Closed => nameof(L.MapDoorClosed), MapDoorState.Locked => nameof(L.MapDoorLocked), _ => nameof(L.MapDoorNone) })) };
        doors.Bind(SelectingItemsControl.SelectedItemProperty, new Binding("ExitEditor.Door") { Mode = BindingMode.TwoWay });
        var returnDirection = Field(nameof(L.MapReturnDirection), EditorText("MapReturnDirection", "ExitEditor.ReturnDirection"));
        returnDirection.Bind(IsVisibleProperty, new Binding("ExitEditor.CreateReturnExit"));
        var content = new StackPanel { Spacing = 7, Children =
        {
            exits, EditorAction(nameof(L.MapNewExit), "MapNewExit", Model.NewExitCommand),
            Field(nameof(L.MapExitDirection), EditorText("MapExitDirection", "ExitEditor.Direction")),
            Field(nameof(L.MapExitDestination), RoomChoice("MapExitDestination", "ExitEditor.Destination")),
            Field(nameof(L.MapExitCommand), EditorText("MapExitCommand", "ExitEditor.Command")),
            Field(nameof(L.MapExitWeight), EditorText("MapExitWeight", "ExitEditor.Weight")),
            Field(nameof(L.MapExitDoor), doors), Check(nameof(L.MapExitLocked), "MapExitLocked", "ExitEditor.IsLocked"),
            Check(nameof(L.MapCreateReturnExit), "MapCreateReturnExit", "ExitEditor.CreateReturnExit"), returnDirection,
            new WrapPanel { Children =
            {
                EditorAction(nameof(L.MapSaveExit), "MapSaveExit", Model.SaveExitCommand),
                EditorAction(nameof(L.MapDeleteExit), "MapDeleteExit", Model.DeleteExitCommand)
            } }
        } };
        var panel = new Expander { [!Expander.HeaderProperty] = LocalizedText.Binding(nameof(L.MapEditExits)), Name = "MapExitEditor", Content = content, HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Bind(IsVisibleProperty, new Binding(nameof(Model.ShowExitEditor)));
        return panel;
    }
    private Control CreateMapTools()
    {
        var search = EditorText("MapSearch", nameof(Model.SearchQuery)); search.Bind(TextBox.PlaceholderTextProperty, LocalizedText.Binding(nameof(L.MapSearchRooms)));
        var results = new ListBox { Name = "MapSearchResults", ItemTemplate = RoomTemplate, MaxHeight = 128 };
        results.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Model.SearchResults)));
        results.Bind(SelectingItemsControl.SelectedItemProperty, new Binding(nameof(Model.SelectedSearchResult)) { Mode = BindingMode.TwoWay });
        results.Bind(IsVisibleProperty, new Binding(nameof(Model.HasSearchQuery)));
        var noResults = Ui.TextKey(nameof(L.MapNoSearchResults), 11, "muted"); noResults.Bind(IsVisibleProperty, new Binding(nameof(Model.HasNoSearchResults)));
        var editHint = Ui.TextKey(nameof(L.MapEditHint), 11, "muted"); editHint.Bind(IsVisibleProperty, new Binding(nameof(Model.IsEditMode)));
        var message = Ui.Text("", 11); message.Bind(TextBlock.TextProperty, new Binding(nameof(Model.EditMessage)));
        var roomEditor = new Expander { [!Expander.HeaderProperty] = LocalizedText.Binding(nameof(L.MapRoomEditor)), Name = "MapRoomEditor", IsExpanded = true, Content = CreateRoomEditor(), HorizontalAlignment = HorizontalAlignment.Stretch };
        roomEditor.Bind(IsVisibleProperty, new Binding(nameof(Model.ShowRoomEditor)));
        var panel = new StackPanel { Spacing = 8, Children =
        {
            search, results, noResults, Check(nameof(L.MapEditMode), "MapEditMode", nameof(Model.IsEditMode)), editHint,
            new WrapPanel { Children =
            {
                EditorAction(nameof(L.MapAddRoom), "MapAddRoom", Model.AddRoomCommand),
                EditorAction(nameof(L.Undo), "MapUndo", Model.UndoMapCommand), EditorAction(nameof(L.Redo), "MapRedo", Model.RedoMapCommand)
            } }, message, roomEditor, CreateExitEditor()
        } };
        return new Expander { Name = "MapTools", [!Expander.HeaderProperty] = LocalizedText.Binding(nameof(L.MapSearchAndEdit)), Content = panel, HorizontalAlignment = HorizontalAlignment.Stretch };
    }
    private Control CreateRouteTools()
    {
        var status = Ui.Text("", 11, "muted"); status.Bind(TextBlock.TextProperty, new Binding(nameof(Model.RouteStatus)));
        var commands = Ui.Text("", 11); commands.Bind(TextBlock.TextProperty, new Binding(nameof(Model.RouteCommands)));
        var panel = new StackPanel { Spacing = 6, Children =
        {
            Check(nameof(L.MapAllowInferred), "MapAllowInferred", nameof(Model.AllowInferredRoutes)),
            new WrapPanel { Children =
            {
                EditorAction(nameof(L.MapPlanRoute), "MapPlanRoute", Model.PlanRouteCommand),
                EditorAction(nameof(L.MapClearRoute), "MapClearRoute", Model.ClearRouteCommand)
            } }, status, commands
        } };
        AddNavigationControls(panel);
        return new Expander { Name = "MapRouteTools", [!Expander.HeaderProperty] = LocalizedText.Binding(nameof(L.MapRouteTools)), Content = panel, HorizontalAlignment = HorizontalAlignment.Stretch };
    }
}
