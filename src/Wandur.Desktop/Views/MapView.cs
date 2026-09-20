using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Wandur.Core.Mapping;
using Wandur.Desktop.ViewModels;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.Views;

public sealed partial class MapView : UserControl
{
    public MapViewModel Model { get; }
    public MapView(WorkspaceController controller, Action<MapViewModel>? editMap = null) : this(new MapViewModel(controller), editMap: editMap) { }
    public MapView(MapViewModel model, bool editingWorkspace = false, Action<MapViewModel>? editMap = null)
    {
        Model = model; DataContext = model;
        TextBlock Label(string property, double size = 11, string? style = "muted")
        {
            var label = Ui.Text("", size, style); label.Bind(TextBlock.TextProperty, new Binding(property)); return label;
        }
        Button Action(string label, string name, System.Windows.Input.ICommand command)
        {
            var button = new Button { Name = name, Command = command, FontSize = 11, Padding = new Thickness(8, 5) };
            if (label.Length > 0) button.Bind(ContentControl.ContentProperty, LocalizedText.Binding(label));
            button.Classes.Add("app-button"); button.Classes.Add("quiet"); return button;
        }
        var mode = Label(nameof(model.ModeLabel), 10); mode.LetterSpacing = 1;
        var status = Label(nameof(model.Status), 13, null); status.FontWeight = FontWeight.SemiBold;
        var header = new StackPanel { Spacing = 4, Children = { mode, status, Label(nameof(model.Counts), 10) } };
        var area = new ComboBox
        {
            Name = "MapAreaChoice", HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 30,
            ItemTemplate = new FuncDataTemplate<MapAreaChoice>((choice, _) => new TextBlock { Text = choice?.Label })
        };
        area.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(model.Areas)));
        area.Bind(SelectingItemsControl.SelectedItemProperty, new Binding(nameof(model.SelectedAreaChoice)) { Mode = BindingMode.TwoWay });
        header.Children.Add(Field(nameof(L.MapArea), area));
        header.Children.Add(Check(nameof(L.MapGridMode), "MapGridMode", nameof(model.IsGridMode)));
        var floorLabel = Label(nameof(model.FloorLabel), 12, null);
        floorLabel.VerticalAlignment = VerticalAlignment.Center;
        var floorUp = Ui.ToolbarIconKey(new Button { Name = "MapFloorUp", Command = model.FloorUpCommand }, "M 3,8 L 8,3 L 13,8 M 8,3 V 14", nameof(L.MapFloorUp));
        var floorDown = Ui.ToolbarIconKey(new Button { Name = "MapFloorDown", Command = model.FloorDownCommand }, "M 3,8 L 8,13 L 13,8 M 8,13 V 2", nameof(L.MapFloorDown));
        var canvas = new RoomMapControl { Name = "RoomMap", Model = model, MinHeight = 180, ClipToBounds = true };
        canvas.SizeChanged += (_, args) => model.SetViewportSize(args.NewSize.Width, args.NewSize.Height);
        var empty = Ui.TextKey(nameof(L.MapEmpty), 12, "muted"); empty.HorizontalAlignment = HorizontalAlignment.Center; empty.VerticalAlignment = VerticalAlignment.Center; empty.Margin = new Thickness(18);
        empty.Bind(IsVisibleProperty, new Binding(nameof(model.IsEmpty)));
        var searchDropdown = CreateRoomSearchOtherFloorDropdown();
        var map = new Grid { Children = { canvas, empty, searchDropdown } };
        var autoCenter = Ui.ToolbarIconKey(new ToggleButton { Name = "MapAutoCenterToggle" },
            "M 8,2 V 5 M 8,11 V 14 M 2,8 H 5 M 11,8 H 14 M 8,6 A 2,2 0 1 0 8,10 A 2,2 0 1 0 8,6", nameof(L.MapAutoCenter));
        autoCenter.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(model.AutoCenter)) { Mode = BindingMode.TwoWay });
        var exercise = Action("", "ToggleMapExercise", model.ToggleExerciseCommand);
        exercise.Bind(ContentControl.ContentProperty, new Binding(nameof(model.ExerciseButtonLabel)));
        var recheck = Action(nameof(L.MapRecheckPosition), "RecheckMapPosition", model.RecheckPositionCommand);
        recheck.Bind(IsVisibleProperty, new Binding(nameof(model.IsLive)));
        recheck.Bind(ToolTip.TipProperty, LocalizedText.Binding(nameof(L.MapRecheckHint))); recheck.Margin = new Thickness(0, 0, 6, 4);
        var fit = Ui.ToolbarIconKey(new Button { Name = "FitMapFloor", Command = model.FitFloorCommand },
            "M 1,6 V 1 H 6 M 10,1 H 15 V 6 M 15,10 V 15 H 10 M 6,15 H 1 V 10", nameof(L.MapFitFloor));
        var stop = Ui.ToolbarIconKey(new Button { Name = "MapStopWalkingToolbar", Command = model.StopWalkingCommand }, "M 3,3 H 13 V 13 H 3 Z", nameof(L.MapStopWalk));
        stop.Bind(IsVisibleProperty, new Binding(nameof(model.IsWalking)));
        var gridToggle = Ui.ToolbarIconKey(new ToggleButton { Name = "MapGridToggle" },
            "M 1,1 H 15 V 15 H 1 Z M 1,6 H 15 M 1,10 H 15 M 6,1 V 15 M 10,1 V 15", nameof(L.MapGridMode));
        gridToggle.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(model.IsGridMode)) { Mode = BindingMode.TwoWay });
        var actions = new WrapPanel { Children = { recheck, exercise } };
        var next = Action(nameof(L.MapNextObservation), "NextMapObservation", model.NextExerciseCommand);
        var exercisePanel = new StackPanel { Spacing = 5, Children = { Label(nameof(model.ExerciseProgress), 10), Label(nameof(model.ExerciseInstruction), 11, null), next } };
        exercisePanel.Bind(IsVisibleProperty, new Binding(nameof(model.IsExercise)));
        var selected = Label(nameof(model.Selection), 12, null); selected.TextTrimming = TextTrimming.CharacterEllipsis; selected.MaxLines = 2;
        var description = Label(nameof(model.SelectionDescription), 11); description.MaxLines = 3;
        description.TextTrimming = TextTrimming.CharacterEllipsis;
        var evidence = Label(nameof(model.Evidence), 10); evidence.Bind(IsVisibleProperty, new Binding(nameof(model.IsLive)));
        var legend = Label(nameof(model.Legend), 10);
        var gestures = Label(nameof(model.Gestures), 10);
        var toggle = Ui.ToolbarIconKey(new ToggleButton { Name = "MapToolsToggle" }, "M 2,4 H 14 M 2,8 H 14 M 2,12 H 14", nameof(L.MapToolsToggle));
        var footer = new StackPanel { Margin = new Thickness(12), Spacing = 8, Children =
        {
            header, selected, description,
            legend, Ui.TextKey(nameof(L.MapLinksLegend), 10, "muted"), gestures, actions, exercisePanel,
            evidence, Label(nameof(model.ProtocolStatus), 10), Label(nameof(model.RoomFieldsStatus), 10)
        } };
        if (editingWorkspace)
        {
            // Keep the inspector focused on editing; room prose is already in the form.
            header.Children.RemoveRange(0, 3);
            footer.Children.Remove(selected); footer.Children.Remove(description);
            var editing = (Expander)CreateMapTools();
            editing.IsExpanded = true;
            footer.Children.Insert(1, editing);
            footer.Children.Insert(2, CreateFileControls());
        }
        else if (editMap is not null)
        {
            var open = EditorAction(nameof(L.MapOpenEditor), "OpenMapEditor",
                new CommunityToolkit.Mvvm.Input.RelayCommand(() => { toggle.IsChecked = false; editMap(model); }));
            open.Bind(IsEnabledProperty, new Binding(nameof(model.CanOpenEditor)));
            footer.Children.Insert(0, open);
        }
        if (!editingWorkspace && model.HasClassification)
        {
            var statusText = Ui.Text("", 11, "muted"); statusText.Name = "MapInferenceStatus";
            statusText.Bind(TextBlock.TextProperty, new Binding(nameof(model.ClassificationStatus)));
            var download = Action(nameof(L.MapInferenceDownload), "MapInferenceDownload", model.DownloadModelCommand);
            download.Bind(IsEnabledProperty, new Binding(nameof(model.CanDownloadModel)));
            var install = Action(nameof(L.MapInferenceInstallFile), "MapInferenceInstallFile", model.InstallModelFromFileCommand);
            model.PickModelFile = async () =>
            {
                var top = TopLevel.GetTopLevel(this);
                if (top is null) return null;
                var files = await top.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
                { AllowMultiple = false, FileTypeFilter = [new Avalonia.Platform.Storage.FilePickerFileType("Model package") { Patterns = ["*.zip"] }] });
                return files.Count == 1 ? files[0].TryGetLocalPath() : null;
            };
            var section = new Expander
            {
                Name = "MapInferenceSection", IsExpanded = false,
                Content = new StackPanel { Spacing = 6, Children = { statusText, Check(nameof(L.MapInferenceEnable), "MapInferenceEnable", nameof(model.ClassifyRoomsLocally)),
                    new WrapPanel { Orientation = Orientation.Horizontal, Children = { download, install } } } }
            };
            section.Bind(HeaderedContentControl.HeaderProperty, LocalizedText.Binding(nameof(L.MapInferenceSection)));
            footer.Children.Add(section);
        }
        footer.Children.Add(CreateRouteTools());
        var tools = new Border
        {
            Name = "MapToolsPanel", IsVisible = false, Width = 300, MaxWidth = 360,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(6),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = new ScrollViewer { Content = footer, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }
        };
        tools.Bind(Border.BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("PanelBrush"));
        tools.Bind(Border.BorderBrushProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("LineBrush"));
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { floorDown, floorLabel, floorUp, autoCenter, fit, gridToggle, stop } };
        Control workspace = map;
        if (editingWorkspace)
        {
            tools.Name = "MapEditorInspector";
            tools.IsVisible = true;
            tools.Width = double.NaN; tools.MaxWidth = double.PositiveInfinity;
            tools.HorizontalAlignment = HorizontalAlignment.Stretch;
            tools.Margin = default; tools.CornerRadius = default;
            tools.BorderThickness = new Thickness(1, 0, 0, 0);
            var split = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch };
            Grid.SetColumn(split, 1); Grid.SetColumn(tools, 2);
            workspace = new Grid { ColumnDefinitions = new ColumnDefinitions("*,5,300"), Children = { map, split, tools } };
        }
        else
        {
            tools.Bind(IsVisibleProperty, new Binding(nameof(ToggleButton.IsChecked)) { Source = toggle });
            SizeChanged += (_, args) => tools.Width = Math.Max(0, Math.Min(360, args.NewSize.Width - 12));
            buttons.Children.Add(toggle);
            map.Children.Add(tools);
        }
        var searchRow = CreateRoomSearchRow();
        var toolbarContent = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children = { searchRow, buttons } };
        Grid.SetColumn(buttons, 1);
        var toolbar = Ui.Toolbar(toolbarContent, "MapToolbar");
        var protocols = Label(nameof(model.ProtocolStatus), 10);
        protocols.Name = "MapProtocolStatus";
        protocols.TextWrapping = TextWrapping.NoWrap;
        protocols.TextTrimming = TextTrimming.CharacterEllipsis;
        ToolTip.SetTip(protocols, new StackPanel
        {
            DataContext = model,
            Spacing = 4,
            Children = { Label(nameof(model.ProtocolStatus), 11), Label(nameof(model.RoomFieldsStatus), 11) }
        });
        var walkStatus = Label(nameof(model.NavigationFeedback), 10);
        walkStatus.Name = "MapWalkStatus";
        walkStatus.Bind(IsVisibleProperty, new Binding(nameof(model.HasWalkFeedback)));
        var zoomSlider = new Slider
        {
            Name = "MapZoomSlider", Minimum = 0.05, Maximum = 4, Width = 100,
            VerticalAlignment = VerticalAlignment.Center,
            TickPlacement = TickPlacement.None
        };
        // The Fluent theme's default horizontal slider template wants a 20 px thumb inside a much taller
        // (~50 px) row, which the compact status bar does not have room for; forcing the control down to a
        // small fixed Height without matching overrides clipped the thumb into a half-circle and left the
        // track pinned to the control's bottom edge. These per-instance resources (the theme's own keys,
        // scoped to just this control) size a smaller 14 px thumb and 3 px track and shrink the reserved
        // space around them to match, so the whole template fits a compact ~22 px row with the thumb and
        // track centered instead of clipped. SliderPreContentMargin/PostContentMargin are GridLength values
        // here (row sizes in the template), not Thickness margins.
        zoomSlider.Resources["SliderHorizontalThumbWidth"] = 14.0;
        zoomSlider.Resources["SliderHorizontalThumbHeight"] = 14.0;
        zoomSlider.Resources["SliderTrackThemeHeight"] = 3.0;
        zoomSlider.Resources["SliderPreContentMargin"] = new GridLength(4);
        zoomSlider.Resources["SliderPostContentMargin"] = new GridLength(4);
        zoomSlider.Resources["SliderHorizontalHeight"] = 22.0;
        zoomSlider.Bind(RangeBase.ValueProperty, new Binding(nameof(model.Zoom)) { Mode = BindingMode.TwoWay });
        zoomSlider.Bind(ToolTip.TipProperty, LocalizedText.Binding(nameof(L.MapZoom)));
        zoomSlider.Bind(Avalonia.Automation.AutomationProperties.NameProperty, LocalizedText.Binding(nameof(L.MapZoom)));
        var zoomOutGlyph = Ui.Text("-", 11, "muted"); zoomOutGlyph.VerticalAlignment = VerticalAlignment.Center;
        var zoomInGlyph = Ui.Text("+", 11, "muted"); zoomInGlyph.VerticalAlignment = VerticalAlignment.Center;
        var zoomBar = new StackPanel
        {
            Name = "MapZoomBar", Orientation = Orientation.Horizontal, Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { zoomOutGlyph, zoomSlider, zoomInGlyph }
        };
        protocols.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(protocols, 0);
        Grid.SetColumn(zoomBar, 1);
        var statusLine = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { protocols, zoomBar } };
        var statusBar = new Border
        {
            Name = "MapStatusBar",
            Padding = new Thickness(8, 4),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = new StackPanel { Spacing = 2, Children = { walkStatus, statusLine } }
        };
        statusBar.Bind(Border.BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("ShellBrush"));
        statusBar.Bind(Border.BorderBrushProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("LineBrush"));
        Grid.SetRow(workspace, 1);
        Grid.SetRow(statusBar, 2);
        Content = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Children = { toolbar, workspace, statusBar } };
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); Model.Attach(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { Model.Detach(); base.OnDetachedFromVisualTree(e); }

    /// <summary>The magnifier reveals a text box, a live match count and a Next button, all in the one toolbar row.</summary>
    private Control CreateRoomSearchRow()
    {
        var toggle = Ui.ToolbarIconKey(new ToggleButton { Name = "MapSearchToggle" },
            "M 6,2.5 A 3.5,3.5 0 1 0 6,9.5 A 3.5,3.5 0 1 0 6,2.5 M 8.5,8.5 L 13,13", nameof(L.MapRoomSearchToggle));
        toggle.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(Model.IsRoomSearchVisible)) { Mode = BindingMode.TwoWay });
        var box = new TextBox { Name = "MapSearchBox", Width = 150, FontSize = 11, MinHeight = 0, Padding = new Thickness(8, 3), VerticalAlignment = VerticalAlignment.Center };
        box.Bind(TextBox.PlaceholderTextProperty, LocalizedText.Binding(nameof(L.MapRoomSearchPlaceholder)));
        box.Bind(TextBox.TextProperty, new Binding(nameof(Model.RoomSearchQuery)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        box.Bind(IsVisibleProperty, new Binding(nameof(Model.IsRoomSearchVisible)));
        box.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter && args.KeyModifiers == KeyModifiers.None)
            { Model.NextRoomSearchMatchCommand.Execute(null); args.Handled = true; }
            else if (args.Key == Key.Escape && args.KeyModifiers == KeyModifiers.None)
            { Model.ClearRoomSearchCommand.Execute(null); args.Handled = true; }
        };
        var count = Ui.Text("", 10, "muted");
        count.Name = "MapSearchCount";
        count.VerticalAlignment = VerticalAlignment.Center;
        count.Bind(TextBlock.TextProperty, new Binding(nameof(Model.RoomSearchCountLabel)));
        count.Bind(IsVisibleProperty, new Binding(nameof(Model.IsRoomSearchActive)));
        var next = Ui.ToolbarIconKey(new Button { Name = "MapSearchNext", Command = Model.NextRoomSearchMatchCommand }, "M 5,3 L 10,8 L 5,13", nameof(L.MapRoomSearchNext));
        next.Bind(IsVisibleProperty, new Binding(nameof(Model.IsRoomSearchVisible)));
        return new StackPanel { Name = "MapSearchRow", Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Children = { toggle, box, next, count } };
    }

    /// <summary>Matches on a floor other than the one shown, reachable without stepping through every match.</summary>
    private Control CreateRoomSearchOtherFloorDropdown()
    {
        var list = new ListBox
        {
            Name = "MapSearchOtherFloor", MaxHeight = 160,
            ItemTemplate = new FuncDataTemplate<MapRoom>((room, _) => room is null ? null : new TextBlock
            { Text = $"{room.Name} · {(string.IsNullOrEmpty(room.Area) ? L.MapUnassignedArea : room.Area)} · {L.Format(L.MapFloor, room.Z)}", TextTrimming = TextTrimming.CharacterEllipsis })
        };
        list.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Model.RoomSearchOtherFloorMatches)));
        list.Bind(SelectingItemsControl.SelectedItemProperty, new Binding(nameof(Model.SelectedRoomSearchMatch)) { Mode = BindingMode.TwoWay });
        var panel = new Border
        {
            Name = "MapSearchOtherFloorPanel", Padding = new Thickness(8), MaxWidth = 260,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(8, 4, 0, 0), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Child = new StackPanel { Spacing = 4, Children = { Ui.TextKey(nameof(L.MapRoomSearchOtherFloors), 10, "muted"), list } }
        };
        panel.Bind(Border.BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("PanelBrush"));
        panel.Bind(Border.BorderBrushProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("LineBrush"));
        panel.Bind(IsVisibleProperty, new Binding(nameof(Model.HasRoomSearchOtherFloorMatches)));
        return panel;
    }
}
