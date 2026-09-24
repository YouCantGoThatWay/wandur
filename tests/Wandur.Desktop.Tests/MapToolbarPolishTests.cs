using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Mapping;
using Wandur.Core.Settings;
using Wandur.Desktop.Terminal;
using Wandur.Desktop.ViewModels;
using Wandur.Desktop.Views;

namespace Wandur.Desktop.Tests;

public sealed class MapToolbarPolishTests
{
    [AvaloniaFact]
    public void PrimaryActionsFitOneCompactRowWithStopVisibleAt240Dips()
    {
        var model = new MapViewModel(new RoomMapTracker());
        var view = new MapView(model);
        var window = Host(view, 240);
        try
        {
            var stop = Find<Button>(view, "MapStopWalkingToolbar");
            Assert.Same(model.StopWalkingCommand, stop.Command);
            // Reserve the space needed when the existing walking binding reveals Stop.
            stop.IsVisible = true;
            Layout(window);
            var toolbar = Find<Border>(view, "MapToolbar");
            Assert.InRange(toolbar.Bounds.Height, 26, 32);
            var actions = new[] { "MapSearchToggle", "MapAutoCenterToggle", "FitMapFloor", "MapToolsToggle", "MapStopWalkingToolbar" }
                .Select(name => Find<Button>(view, name)).ToArray();
            foreach (var button in actions)
            {
                Assert.Equal(26, button.Bounds.Width);
                Assert.Equal(26, button.Bounds.Height);
                Assert.True(Frame(toolbar, window).Contains(Frame(button, window)), button.Name);
                Assert.Equal(Frame(actions[0], window).Y, Frame(button, window).Y);
            }
            for (var i = 1; i < actions.Length; i++)
                Assert.InRange(Frame(actions[i], window).Left - Frame(actions[i - 1], window).Right, 0, 4);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void SearchExpandsBelowActionsAndRemainsUsableAt240Dips()
    {
        var tracker = new RoomMapTracker();
        tracker.Observe(new RoomObservation("hall", "Copper hall", "A quiet hall.", new Dictionary<string, string?>(), Source: RoomDataSource.Gmcp));
        var model = new MapViewModel(tracker);
        var view = new MapView(model);
        var window = Host(view, 240);
        try
        {
            Find<ToggleButton>(view, "MapSearchToggle").IsChecked = true;
            Layout(window);
            var box = Find<TextBox>(view, "MapSearchBox");
            var next = Find<Button>(view, "MapSearchNext");
            var toolbar = Find<Border>(view, "MapToolbar");
            Assert.True(Frame(box, window).Top >= Frame(Find<Button>(view, "MapToolsToggle"), window).Bottom);
            Assert.InRange(box.Bounds.Width, 140, 210);
            box.Focus(); window.KeyTextInput("copper"); Layout(window);
            Assert.Equal("copper", model.RoomSearchQuery);
            Assert.Equal(1, model.RoomSearchMatchCount);
            foreach (var control in new Control[] { box, next, Find<TextBlock>(view, "MapSearchCount") })
                Assert.True(Frame(toolbar, window).Contains(Frame(control, window)), control.Name);
            Click(window, next);
            Assert.Equal("s:hall", model.SelectedRoomId);
            box.Focus();
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Layout(window);
            Assert.Empty(model.RoomSearchQuery);
            Find<ToggleButton>(view, "MapSearchToggle").IsChecked = false;
            Layout(window);
            Assert.InRange(toolbar.Bounds.Height, 26, 32);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void FloorAndGridControlsLiveInToolsAndKeepTheirBindings(bool editing)
    {
        var tracker = new RoomMapTracker();
        tracker.Observe(new RoomObservation("ground", "Ground", "", new Dictionary<string, string?>(), Source: RoomDataSource.Gmcp));
        tracker.Observe(new RoomObservation("upper", "Upper", "", new Dictionary<string, string?>(), Source: RoomDataSource.Gmcp), "up");
        var model = new MapViewModel(tracker);
        var view = new MapView(model, editingWorkspace: editing);
        var window = Host(view, editing ? 800 : 240);
        try
        {
            var tools = Find<Border>(view, editing ? "MapEditorInspector" : "MapToolsPanel");
            var toolbar = Find<Border>(view, "MapToolbar");
            if (!editing) Click(window, Find<Button>(view, "MapToolsToggle"));
            Assert.True(tools.IsVisible);
            foreach (var name in new[] { "MapFloorDown", "MapFloorUp", "MapGridMode" })
            {
                Assert.DoesNotContain(toolbar.GetVisualDescendants().OfType<Control>(), c => c.Name == name);
                Assert.Contains(tools.GetVisualDescendants().OfType<Control>(), c => c.Name == name);
            }
            var down = Find<Button>(tools, "MapFloorDown");
            var up = Find<Button>(tools, "MapFloorUp");
            Assert.Same(model.FloorDownCommand, down.Command);
            Assert.Same(model.FloorUpCommand, up.Command);
            Click(window, down); Assert.Equal(0, model.SelectedFloor);
            Click(window, up); Assert.Equal(1, model.SelectedFloor);
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<Control>(), c => c.Name == "MapGridToggle");
            Click(window, Find<CheckBox>(tools, "MapGridMode"));
            Assert.True(model.IsGridMode);
            Assert.True(Find<CheckBox>(tools, "MapGridMode").IsChecked);
            Find<CheckBox>(tools, "MapGridMode").IsChecked = false;
            Assert.False(model.IsGridMode);
            if (editing)
            {
                Assert.True(Find<Expander>(tools, "MapTools").IsExpanded);
                Assert.NotNull(Find<Button>(tools, "MapImport"));
            }
            else
            {
                Click(window, Find<Button>(view, "MapToolsToggle"));
                Assert.False(tools.IsVisible);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ToolbarsFollowWorkspaceChromeAndKeepDarkBodiesAndInputsAcrossThemes()
    {
        await using var controller = new WorkspaceController(new TranscriptDisplayFactory(),
            new SettingsStore(Path.Combine(Path.GetTempPath(), "wandur-toolbar-" + Guid.NewGuid(), "settings.json")),
            new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        using var channelModel = new ChannelsViewModel(controller);
        var channels = new ChannelsView(channelModel);
        var map = new MapView(new MapViewModel(new RoomMapTracker()));
        Grid.SetColumn(channels, 1);
        var window = Host(new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Children = { map, channels } }, 600);
        try
        {
            foreach (var theme in new[] { "Hull", "Slate", "Hull" })
            {
                ThemeService.Apply(new ClientSettings { Theme = theme }); Layout(window);
                var resources = Application.Current!.Resources;
                Assert.Equal(resources["ChromeBrush"], Find<Border>(map, "MapToolbar").Background);
                Assert.Equal(resources["ChromeBrush"], Find<Border>(channels, "ChannelsToolbar").Background);
                Assert.Equal(resources["TextBrush"], Find<Button>(map, "FitMapFloor").Foreground);
                var tab = Find<ListBox>(channels, "ChannelTabs").GetVisualDescendants().OfType<ListBoxItem>().First();
                Assert.Equal(resources["TextBrush"], tab.Foreground);
                Assert.Equal(resources["TerminalBrush"], Find<TextBox>(channels, "ChannelReply").Background);
                Assert.Equal(resources["TerminalBrush"], Find<Border>(channels, "ChannelMessages").Background);
                var canvas = Find<RoomMapControl>(map, "RoomMap");
                Assert.Equal(resources["MapCanvasBrush"], Assert.IsType<Grid>(canvas.Parent).Background);
                Find<ToggleButton>(map, "MapSearchToggle").IsChecked = true; Layout(window);
                var search = Find<TextBox>(map, "MapSearchBox");
                search.Focus(); Layout(window);
                Assert.Equal(resources["TerminalBrush"], search.Background);
                Assert.Equal(resources["TerminalTextBrush"], search.Foreground);
            }
        }
        finally { window.Close(); ThemeService.Apply(new ClientSettings()); }
    }

    [AvaloniaFact]
    public async Task ChannelTabsFitCompactStripAndKeepSelectionAndUnreadBehavior()
    {
        await using var controller = new WorkspaceController(new TranscriptDisplayFactory(),
            new SettingsStore(Path.Combine(Path.GetTempPath(), "wandur-toolbar-tabs-" + Guid.NewGuid(), "settings.json")),
            new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        using var model = new ChannelsViewModel(controller);
        model.Tabs.Add(new ChannelTabViewModel("chat", false) { Unread = 3 });
        model.Tabs.Add(new ChannelTabViewModel("tell", true));
        var view = new ChannelsView(model);
        var window = Host(view, 240);
        try
        {
            var toolbar = Find<Border>(view, "ChannelsToolbar");
            Assert.InRange(toolbar.Bounds.Height, 26, 32);
            var tabs = Find<ListBox>(view, "ChannelTabs").GetVisualDescendants().OfType<ListBoxItem>().ToArray();
            Assert.Equal(3, tabs.Length);
            Assert.All(tabs, tab =>
            {
                Assert.Equal(26, tab.Bounds.Height);
                Assert.True(Frame(toolbar, window).Contains(Frame(tab, window)));
            });
            Click(window, tabs[1]);
            Assert.Equal("chat", model.Selected.Channel);
            Assert.Equal(0, model.Selected.Unread);
        }
        finally { window.Close(); }
    }

    private static Window Host(Control view, double width)
    {
        var window = new Window { Content = view, Width = width, Height = 620 };
        window.Classes.Add("fleet"); window.Show(); Layout(window); return window;
    }

    [AvaloniaTheory]
    [InlineData("Hull")]
    [InlineData("Slate")]
    public async Task ToolbarStatesKeepReadableChromeInkAndGlyphInheritance(string theme)
    {
        await using var controller = new WorkspaceController(new TranscriptDisplayFactory(),
            new SettingsStore(Path.Combine(Path.GetTempPath(), "wandur-toolbar-states-" + Guid.NewGuid(), "settings.json")),
            new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        using var model = new ChannelsViewModel(controller);
        model.Tabs.Add(new ChannelTabViewModel("chat", false));
        var channels = new ChannelsView(model);
        var map = new MapView(new MapViewModel(new RoomMapTracker()));
        Grid.SetColumn(channels, 1);
        var window = Host(new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Children = { map, channels } }, 600);
        try
        {
            ThemeService.Apply(new ClientSettings { Theme = theme }); Layout(window);
            var mapToolbar = Find<Border>(map, "MapToolbar");
            var channelsToolbar = Find<Border>(channels, "ChannelsToolbar");
            var tabs = Find<ListBox>(channels, "ChannelTabs").GetVisualDescendants().OfType<ListBoxItem>().ToArray();
            var more = Find<ToggleButton>(map, "MapToolsToggle");
            for (var state = 0; state < 4; state++)
            {
                more.IsChecked = state == 2;
                more.IsEnabled = state != 3;
                var target = state == 1 ? (Control)more : channels;
                window.MouseMove(target.TranslatePoint(new Point(12, 12), window)!.Value); Layout(window);
                var ink = more.Foreground;
                Assert.Equal(Application.Current!.Resources["TextBrush"], ink);
                Assert.Equal(ink, more.GetVisualDescendants().OfType<ContentPresenter>().Single(p => p.Name == "PART_ContentPresenter").Foreground);
                Assert.Equal(ink, Assert.IsType<Avalonia.Controls.Shapes.Path>(more.Content).Stroke);
                if (state < 3) AssertChromeContrast(more, mapToolbar);
            }
            // Selected, idle, hover and newly selected channel headers must all read on metal.
            AssertChromeContrast(tabs[0], channelsToolbar);
            AssertChromeContrast(tabs[1], channelsToolbar);
            window.MouseMove(tabs[1].TranslatePoint(new Point(12, 12), window)!.Value); Layout(window);
            AssertChromeContrast(tabs[1], channelsToolbar);
            Click(window, tabs[1]);
            AssertChromeContrast(tabs[1], channelsToolbar);
            foreach (var tab in tabs)
                Assert.Equal(tab.Foreground, tab.GetVisualDescendants().OfType<TextBlock>().First().Foreground);
        }
        finally { window.Close(); ThemeService.Apply(new ClientSettings()); }
    }

    private static void AssertChromeContrast(TemplatedControl control, Border toolbar)
    {
        var ink = Assert.IsAssignableFrom<ISolidColorBrush>(control.Foreground).Color;
        var state = Assert.IsAssignableFrom<ISolidColorBrush>(control.Background).Color;
        var chrome = Assert.IsType<LinearGradientBrush>(toolbar.Background);
        var glyph = control.GetVisualDescendants().OfType<Control>()
            .First(c => c is Avalonia.Controls.Shapes.Path or TextBlock);
        var top = glyph.TranslatePoint(default, toolbar)!.Value.Y / toolbar.Bounds.Height;
        var bottom = top + glyph.Bounds.Height / toolbar.Bounds.Height;
        // Sample where ink is painted, excluding the thin bevel at the toolbar's outer edge.
        var offsets = chrome.GradientStops.Select(s => s.Offset).Where(o => o > top && o < bottom).Append(top).Append(bottom);
        foreach (var offset in offsets)
        {
            var before = chrome.GradientStops.Last(s => s.Offset <= offset);
            var after = chrome.GradientStops.First(s => s.Offset >= offset);
            var blend = after.Offset == before.Offset ? 0 : (offset - before.Offset) / (after.Offset - before.Offset);
            var surface = ContrastProbe.Over(after.Color, before.Color, blend);
            Assert.True(ContrastProbe.Contrast(ink, ContrastProbe.Over(state, surface, 1)) >= 4.5,
                $"Toolbar ink {ink} must remain readable over {state} on chrome {surface}.");
        }
    }

    private static T Find<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static Rect Frame(Control control, Window window) =>
        new(control.TranslatePoint(default, window)!.Value, control.Bounds.Size);

    private static void Layout(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }

    private static void Click(Window window, Control control)
    {
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Layout(window);
    }
}
