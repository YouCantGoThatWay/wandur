using Avalonia.Controls;
using Avalonia;
using Avalonia.Input;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Mapping;
using Wandur.Core.Settings;
using Wandur.Desktop;
using Wandur.Desktop.ViewModels;
using Wandur.Desktop.Views;

namespace Wandur.Desktop.Tests;

public sealed class MapViewTests
{
    [AvaloniaFact]
    public void ZoomButtonsAndPinchPreserveThePointUnderTheGesture()
    {
        var model = new MapViewModel(new RoomMapTracker());
        var view = new MapView(model);
        var window = new Window { Content = view, Width = 500, Height = 600 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var canvas = view.GetVisualDescendants().OfType<RoomMapControl>().Single();
            var zoomOut = view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "MapZoomOut");
            zoomOut.Command!.Execute(null);
            Assert.True(model.Zoom < 1);
            var anchor = new Point(90, 130);
            var before = model.CreateViewport(canvas.Bounds.Width, canvas.Bounds.Height).Unproject(anchor);
            var zoom = model.Zoom;
            canvas.RaiseEvent(new PinchEventArgs(1.5, anchor));
            canvas.RaiseEvent(new PinchEventArgs(2, anchor));
            Assert.Equal(zoom * 2, model.Zoom, 8);
            var after = model.CreateViewport(canvas.Bounds.Width, canvas.Bounds.Height).Unproject(anchor);
            Assert.Equal(before.X, after.X, 8); Assert.Equal(before.Y, after.Y, 8);
            canvas.RaiseEvent(new PinchEndedEventArgs());
            canvas.RaiseEvent(new PointerDeltaEventArgs(InputElement.PointerTouchPadGestureMagnifyEvent, canvas,
                new Pointer(100, PointerType.Mouse, true), window, canvas.TranslatePoint(anchor, window)!.Value, 0, new PointerPointProperties(), KeyModifiers.None, new Vector(.25, .25)));
            Assert.Equal(zoom * 2 * 1.25, model.Zoom, 8);
            model.ZoomAt(100, anchor); Assert.Equal(4, model.Zoom);
            model.ZoomAt(.001, anchor); Assert.Equal(.05, model.Zoom);
            model.ZoomAt(double.NaN, anchor); Assert.Equal(.05, model.Zoom);
            after = model.CreateViewport(canvas.Bounds.Width, canvas.Bounds.Height).Unproject(anchor);
            Assert.Equal(before.X, after.X, 8); Assert.Equal(before.Y, after.Y, 8);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ToolbarGridToggleTracksAreaSettingsAndInspector()
    {
        var tracker = new RoomMapTracker();
        var model = new MapViewModel(tracker);
        var view = new MapView(model);
        var window = new Window { Content = view, Width = 480, Height = 700 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var toggle = Assert.Single(view.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>(),
                c => c.Name == "MapGridToggle");
            Assert.False(toggle.IsChecked);
            var point = toggle.TranslatePoint(new Point(toggle.Bounds.Width / 2, toggle.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.True(model.IsGridMode);
            view.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>()
                .Single(c => c.Name == "MapToolsToggle").IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            var inspector = view.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "MapGridMode");
            Assert.True(inspector.IsChecked);
            Assert.True(Assert.Single(tracker.Snapshot.AreaSettings).GridMode);
            model.SelectedArea = "Another area";
            Assert.False(toggle.IsChecked);
            model.SelectedArea = "";
            Assert.True(toggle.IsChecked);
            inspector.IsChecked = false;
            Assert.False(toggle.IsChecked); Assert.False(model.IsGridMode);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void RoomSelectionPanAndFloorBadgesOnlyChangePresentation()
    {
        var tracker = new RoomMapTracker(new MapSnapshot(
            [new("hall", "Entrance hall", "A broad hall with a staircase.", null, 0, 0, 0, false),
             new("garden", "Garden", "Flowers beside the northern gate.", null, 0, 1, 0, false),
             new("library", "Library", "Shelves line the eastern wall.", null, 1, 0, 0, true),
             new("balcony", "Balcony", "A balcony overlooking the hall.", null, 0, 0, 1, false)],
            [new("hall", "garden", "north", true), new("garden", "hall", "south", true),
             new("hall", "library", "east", false), new("hall", "balcony", "up", true)],
            [], null, MapTrackingState.Unknown, RoomDataSource.Text, 0));
        var original = System.Text.Json.JsonSerializer.Serialize(tracker.Snapshot);
        var model = new MapViewModel(tracker);
        var view = new MapView(model);
        var window = new Window { Content = view, Width = 560, Height = 740 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var canvas = view.GetVisualDescendants().OfType<RoomMapControl>().Single();
            Capture(window, "map-2d-overview.png");
            var viewport = new MapViewport(model.CenterX, model.CenterY, model.PanX, model.PanY,
                model.Zoom, canvas.Bounds.Width, canvas.Bounds.Height);
            var hall = viewport.Project(0, 0);
            void Click(Point local)
            {
                var point = canvas.TranslatePoint(local, window)!.Value;
                window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
            }
            Click(hall);
            Assert.Equal("hall", model.SelectedRoomId);
            Assert.Equal("A broad hall with a staircase.", model.SelectionDescription);
            var half = Math.Clamp(viewport.Scale * .16, 4, 26);
            Click(new Point(hall.X + half + 15, hall.Y - half - 12));
            Assert.Equal(1, model.SelectedFloor);
            Assert.Equal("balcony", Assert.Single(model.VisibleRooms).Id);
            Capture(window, "map-2d-upper-floor.png");
            var start = canvas.TranslatePoint(new Point(100, 100), window)!.Value;
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(new Point(start.X + 40, start.Y + 20));
            window.MouseUp(new Point(start.X + 40, start.Y + 20), MouseButton.Left);
            Assert.Equal(40, model.PanX);
            Assert.Equal(20, model.PanY);
            Assert.Equal(original, System.Text.Json.JsonSerializer.Serialize(tracker.Snapshot));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void MapHasFloorNavigationAndCurrentRoomControls()
    {
        var view = new MapView(new MapViewModel(new RoomMapTracker()));
        var window = new Window { Content = view, Width = 480, Height = 700 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            Assert.NotNull(view.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "MapFloorUp"));
            Assert.NotNull(view.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "MapFloorDown"));
            Assert.NotNull(view.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "CenterMap"));
            var canvas = view.GetVisualDescendants().OfType<RoomMapControl>().Single();
            Assert.True(canvas.Bounds.Height > view.Bounds.Height * .9);
            var panel = view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "MapToolsPanel");
            Assert.False(panel.IsVisible);
            var toggle = view.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().Single(b => b.Name == "MapToolsToggle");
            toggle.IsChecked = true; Dispatcher.UIThread.RunJobs();
            Assert.True(panel.IsVisible);
            var controls = panel.GetVisualDescendants().OfType<Control>().ToArray();
            foreach (var name in new[] { "MapAreaChoice", "MapGridMode", "MapRouteTools" })
                Assert.Contains(controls, c => c.Name == name);
            Assert.DoesNotContain(controls, c => c.Name is "MapTools" or "MapImport" or "MapRoomEditor");
            toggle.IsChecked = false; Dispatcher.UIThread.RunJobs();
            Assert.False(panel.IsVisible);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void PanAndZoomPreserveNorthUpAndCenterRestoresTheRoom()
    {
        var tracker = new RoomMapTracker();
        tracker.Observe(new RoomObservation("start", "Start", "", new Dictionary<string, string?>(), Source: RoomDataSource.Gmcp));
        var model = new MapViewModel(tracker);
        model.Attach();
        try
        {
            var viewport = new MapViewport(model.CenterX, model.CenterY, model.PanX, model.PanY, model.Zoom, 500, 400);
            Assert.True(viewport.Project(0, 1).Y < viewport.Project(0, -1).Y);
            Assert.Equal(viewport.Project(0, 1).X, viewport.Project(0, -1).X);
            model.Pan(40, 20);
            model.ChangeZoom(2);
            model.CenterCommand.Execute(null);
            Assert.Equal(0, model.PanX);
            Assert.Equal(0, model.PanY);
            Assert.Equal(1, model.Zoom);
            Assert.Equal(tracker.Snapshot.Rooms.Single().X, model.CenterX);
        }
        finally { model.Detach(); }
    }

    [Fact]
    public void FitFloorKeepsTheWholeFloorVisibleInANarrowDock()
    {
        var model = new MapViewModel(new RoomMapTracker());
        model.SetViewportSize(280, 180);
        model.ToggleExerciseCommand.Execute(null);
        model.FitFloorCommand.Execute(null);
        var viewport = new MapViewport(model.CenterX, model.CenterY, model.PanX, model.PanY, model.Zoom, 280, 180);
        foreach (var room in model.VisibleRooms)
        {
            var point = viewport.Project(room.X, room.Y);
            Assert.InRange(point.X, 30, 250);
            Assert.InRange(point.Y, 30, 150);
        }
    }

    [Fact]
    public void FloorsSeparateStackedRoomsAndFollowOnlyActualFloorChanges()
    {
        var tracker = new RoomMapTracker();
        tracker.Observe(new RoomObservation("ground", "Ground", "", new Dictionary<string, string?>(), Source: RoomDataSource.Gmcp));
        var model = new MapViewModel(tracker);
        model.Attach();
        try
        {
            tracker.Observe(new RoomObservation("upper", "Upper", "", new Dictionary<string, string?>(), Source: RoomDataSource.Gmcp), "up");
            Assert.Equal(1, model.SelectedFloor);
            Assert.Equal("Upper", Assert.Single(model.VisibleRooms).Name);
            Assert.True(model.FloorDownCommand.CanExecute(null));
            model.FloorDownCommand.Execute(null);
            Assert.Equal("Ground", Assert.Single(model.VisibleRooms).Name);
            tracker.Observe(new RoomObservation("upper", "Upper", "", new Dictionary<string, string?>(), Source: RoomDataSource.Gmcp));
            Assert.Equal(0, model.SelectedFloor); // Inspection is not undone by repeated room reports.
            model.CenterCommand.Execute(null);
            Assert.Equal(1, model.SelectedFloor);
            Assert.Equal("Upper", Assert.Single(model.VisibleRooms).Name);
            tracker.Observe(new RoomObservation("ground", "Ground", "", new Dictionary<string, string?>(), Source: RoomDataSource.Gmcp), "down");
            Assert.Equal(0, model.SelectedFloor);
        }
        finally { model.Detach(); }
    }

    [Fact]
    public void LocalExerciseNarrowsRepeatedRoomsWithoutChangingTheLiveMap()
    {
        var live = new RoomMapTracker();
        live.Observe(new RoomObservation("live-id", "A real room", "An untouched session.", new Dictionary<string, string?>(), Source: RoomDataSource.Gmcp));
        var before = System.Text.Json.JsonSerializer.Serialize(live.Snapshot);
        var model = new MapViewModel(live);
        model.Attach();
        try
        {
            model.ToggleExerciseCommand.Execute(null);
            Assert.True(model.IsExercise);
            Assert.Equal(4, model.Snapshot.CandidateRoomIds.Count);
            model.NextExerciseCommand.Execute(null);
            Assert.Equal(2, model.Snapshot.CandidateRoomIds.Count);
            model.NextExerciseCommand.Execute(null);
            Assert.Empty(model.Snapshot.CandidateRoomIds);
            Assert.Equal("c", model.Snapshot.CurrentRoomId);
            model.NextExerciseCommand.Execute(null);
            Assert.Equal("landmark", model.Snapshot.CurrentRoomId);
            Assert.False(model.NextExerciseCommand.CanExecute(null));
            Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(live.Snapshot));
            model.ToggleExerciseCommand.Execute(null);
            Assert.False(model.IsExercise);
            Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(model.Snapshot));
        }
        finally { model.Detach(); }
    }

    [AvaloniaFact]
    public async Task DockedMapFollowsSessionsAndExerciseRendersWithoutSendingCommands()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wandur-map-ui-" + Guid.NewGuid());
        var window = new MainWindow(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), new SettingsStore(Path.Combine(directory, "settings.json")), new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var initial = Assert.Single(window.GetVisualDescendants().OfType<MapView>());
            initial.Model.ToggleExerciseCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(6, initial.Model.Snapshot.Rooms.Count);
            Assert.Empty(window.Controller.Map.Snapshot.Rooms);
            Assert.False(window.Controller.HasSession);
            Capture(window, "map-recognition-ambiguous.png");
            initial.Model.NextExerciseCommand.Execute(null);
            initial.Model.NextExerciseCommand.Execute(null);
            initial.Model.NextExerciseCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Capture(window, "map-recognition-resolved.png");
            window.Sessions.NewTab(); Dispatcher.UIThread.RunJobs();
            var next = Assert.Single(window.GetVisualDescendants().OfType<MapView>());
            Assert.NotSame(initial, next);
            Assert.False(next.Model.IsExercise);
            Assert.Empty(next.Model.Snapshot.Rooms);
            window.ToggleMap(); Dispatcher.UIThread.RunJobs(); Assert.False(window.IsMapVisible);
            window.ResetLayout(); Dispatcher.UIThread.RunJobs(); Assert.True(window.IsMapVisible);
            Assert.Single(window.GetVisualDescendants().OfType<MapView>());
        }
        finally { await window.Sessions.DisposeAsync(); window.Close(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    private static void Capture(Window window, string name)
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var directory = Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory); frame.Save(Path.Combine(directory, name), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
