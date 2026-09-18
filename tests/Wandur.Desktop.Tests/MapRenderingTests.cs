using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Mapping;
using Wandur.Desktop.ViewModels;
using Wandur.Desktop.Views;

namespace Wandur.Desktop.Tests;

public sealed class MapRenderingTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExitLightsAppearOnRoomEdgesForUnmappedAndMappedExits(bool gridMode)
    {
        var tracker = new RoomMapTracker(new MapSnapshot(
            [new("room", "Cockpit", "", null, 0, 0, 0, false) { KnownExits = ["south", "northeast", "up"] },
             new("other", "Other", "", null, -1, 0, 0, false)],
            [new("room", "other", "west", true)], [], null, MapTrackingState.Unknown, RoomDataSource.Gmcp, 0));
        var model = new MapViewModel(tracker); model.Attach(); model.IsGridMode = gridMode;
        var canvas = new RoomMapControl { Model = model };
        var window = new Window { Content = canvas, Width = 500, Height = 400 };
        try
        {
            window.Show(); model.Zoom = 1; Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
            using var stream = new MemoryStream(); frame.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            using var pixels = SkiaSharp.SKBitmap.Decode(stream.ToArray());
            var center = model.CreateViewport(canvas.Bounds.Width, canvas.Bounds.Height).Project(0, 0);
            var half = gridMode ? 48 : 15.36;
            bool HasLight(Point point)
            {
                var x = (int)Math.Round(point.X); var y = (int)Math.Round(point.Y);
                return Enumerable.Range(-3, 7).Any(dx => Enumerable.Range(-3, 7).Any(dy =>
                { var pixel = pixels.GetPixel(x + dx, y + dy); return pixel.Green > 190 && pixel.Red < 160 && pixel.Blue > 160; }));
            }
            Assert.True(HasLight(center + new Vector(0, half)), "Unmapped south exit must be lit at the bottom edge.");
            Assert.True(HasLight(center + new Vector(-half, 0)), "Mapped west exit must remain lit.");
            Assert.True(HasLight(center + new Vector(half, -half)), "Northeast belongs in the top-right corner.");
            Assert.True(HasLight(center + new Vector(-half * .55, -half)), "An unmapped upward exit must remain visible.");
            Assert.False(HasLight(center + new Vector(0, -half)), "No north light without a north exit.");
            var directory = Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR");
            if (!string.IsNullOrEmpty(directory)) { Directory.CreateDirectory(directory); frame.Save(Path.Combine(directory, gridMode ? "exit-lights-grid.png" : "exit-lights-rooms.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions()); }
        }
        finally { window.Close(); model.Detach(); }
    }

    [AvaloniaTheory]
    [InlineData(1)]
    [InlineData(.37)]
    [InlineData(.125)]
    public void GridLinesFollowTileEdgesAfterPanAndZoom(double zoom)
    {
        var tracker = new RoomMapTracker(new MapSnapshot(
            [new("room", "Room", "", null, 0, 0, 0, false)], [], [], null,
            MapTrackingState.Unknown, RoomDataSource.Text, 0));
        var model = new MapViewModel(tracker); model.Attach(); model.IsGridMode = true;
        var canvas = new RoomMapControl { Model = model };
        canvas.Resources["MapCanvasBrush"] = Avalonia.Media.Brushes.Black;
        canvas.Resources["MapGridBrush"] = Avalonia.Media.Brushes.White;
        var window = new Window { Content = canvas, Width = 500, Height = 400 };
        try
        {
            window.Show(); model.Zoom = zoom; model.Pan(7, -9);
            Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
            using var stream = new MemoryStream();
            frame.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            using var pixels = SkiaSharp.SKBitmap.Decode(stream.ToArray());
            var viewport = model.CreateViewport(canvas.Bounds.Width, canvas.Bounds.Height);
            byte Brightness(Point point)
            {
                var x = (int)Math.Round(point.X * pixels.Width / canvas.Bounds.Width);
                var y = (int)Math.Round(point.Y * pixels.Height / canvas.Bounds.Height);
                return pixels.GetPixel(x, y).Red;
            }
            var vertical = viewport.Project(.5, -1.2);
            var horizontal = viewport.Project(1.2, -.5);
            Assert.True(Enumerable.Range(-1, 3).Max(d => Brightness(vertical + new Vector(d, 0))) > 40,
                "Vertical grid line must continue along the tile edge.");
            Assert.True(Enumerable.Range(-1, 3).Max(d => Brightness(horizontal + new Vector(0, d))) > 40,
                "Horizontal grid line must continue along the tile edge.");
            Assert.Equal(0, Brightness(viewport.Project(1, -1.2)));
            Assert.Equal(0, Brightness(viewport.Project(1.2, -1)));
        }
        finally { window.Close(); model.Detach(); }
    }

    [Fact]
    public void GridPreservesCoordinateDistancesAndStoredData()
    {
        var rooms = new MapRoom[]
        {
            new("a", "A", "", null, 105, 205, 0, false), new("b", "B", "", null, 205, 205, 0, false),
            new("far", "Far", "", null, 505, 205, 0, false)
        };
        var tracker = new RoomMapTracker(new MapSnapshot(rooms, [new("a", "b", "east", true)], [], null, MapTrackingState.Unknown, RoomDataSource.Gmcp, 0));
        var model = new MapViewModel(tracker); model.Attach();
        try
        {
            model.IsGridMode = true;
            var before = System.Text.Json.JsonSerializer.Serialize(tracker.Snapshot.Rooms);
            model.SetViewportSize(280, 300); model.FitFloorCommand.Execute(null);
            var viewport = model.CreateViewport(280, 300);
            var a = viewport.Project(105, 205); var b = viewport.Project(205, 205); var far = viewport.Project(505, 205);
            Assert.Equal(viewport.Scale * 100, b.X - a.X, 8);
            Assert.Equal(viewport.Scale * 300, far.X - b.X, 8);
            foreach (var room in rooms)
            {
                var point = viewport.Project(room.X, room.Y);
                Assert.Equal(new Point(room.X, room.Y), viewport.Unproject(point));
            }
            Assert.Equal(506, viewport.Unproject(far + new Vector(viewport.Scale, 0)).X, 8);
            model.SelectRoom("b");
            Assert.Equal(new Point(140, 150), model.CreateViewport(280, 300).Project(205, 205));
            model.IsGridMode = false;
            Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(tracker.Snapshot.Rooms));
            Assert.Equal(rooms.Select(r => (r.X, r.Y)), tracker.Snapshot.Rooms.Select(r => (r.X, r.Y)));
            Assert.Single(tracker.Snapshot.Links);
        }
        finally { model.Detach(); }
    }

    [AvaloniaFact]
    public void GridDragKeepsItsVisibleDistanceAndUndoRestoresTheTile()
    {
        var tracker = new RoomMapTracker(new MapSnapshot(
            [new("west", "West", "", null, 0, 0, 0, false), new("east", "East", "", null, 1, 0, 0, false)],
            [new("west", "east", "east", true)], [], null, MapTrackingState.Unknown, RoomDataSource.Gmcp, 0));
        var model = new MapViewModel(tracker); model.Attach(); model.IsGridMode = true;
        var canvas = new RoomMapControl { Model = model }; var window = new Window { Content = canvas, Width = 500, Height = 400 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            var projection = model.CreateViewport(canvas.Bounds.Width, canvas.Bounds.Height);
            var boundary = projection.Project(.5, 0);
            Click(window, boundary - new Vector(1, 0)); Assert.Equal("west", model.SelectedRoomId);
            Click(window, boundary + new Vector(1, 0)); Assert.Equal("east", model.SelectedRoomId);
            model.IsEditMode = true;
            var from = projection.Project(1, 0); var to = from + new Vector(projection.Scale, 0);
            window.MouseDown(from, MouseButton.Left); window.MouseMove(to); window.MouseUp(to, MouseButton.Left);
            var moved = tracker.Snapshot.Rooms.Single(r => r.Id == "east");
            Assert.Equal(2, moved.X); Assert.Equal(0, moved.Y);
            var updated = model.CreateViewport(canvas.Bounds.Width, canvas.Bounds.Height);
            Assert.Equal(updated.Scale * 2, updated.Project(moved.X, moved.Y).X - updated.Project(0, 0).X, 8);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            Click(window, updated.Project(1, 0)); Assert.Null(model.SelectedRoomId);
            Click(window, updated.Project(2, 0)); Assert.Equal("east", model.SelectedRoomId);
            Assert.Single(tracker.Snapshot.Links);
            Assert.True(tracker.Undo()); Assert.Equal(1, tracker.Snapshot.Rooms.Single(r => r.Id == "east").X);
        }
        finally { window.Close(); model.Detach(); }
    }

    [Fact]
    public void ProjectionRoundTripsWithPanAndZoom()
    {
        var projection = new MapViewport(3, -5, 72, -41, .37, 765, 440);
        var point = projection.Unproject(projection.Project(-11, 23));
        Assert.Equal(-11, point.X, 8); Assert.Equal(23, point.Y, 8);
    }

    [Fact]
    public void ImportRevealsNewAreaAndRejectsMalformedFileWithoutMutation()
    {
        var tracker = new RoomMapTracker(new MapSnapshot([new("old", "Old room", "", "Old area", 0, 0, 0, false)], [], [], null, MapTrackingState.Unknown, RoomDataSource.Text, 0));
        var model = new MapViewModel(tracker); model.Attach();
        try
        {
            var map = new MapSnapshot([new("new", "New room", "", "New area", 12, 6, 3, false)], [], [], null, MapTrackingState.Unknown, RoomDataSource.Text, 0);
            model.ImportMap(MapFileFormat.Serialize(map));
            Assert.Equal("New area", model.SelectedArea); Assert.Equal(3, model.SelectedFloor);
            Assert.Equal("new", Assert.Single(model.VisibleRooms).Id);
            var exported = model.ExportMap();
            Assert.Throws<FormatException>(() => model.ImportMap("{}"));
            Assert.Equal(exported, model.ExportMap());
            Assert.True(tracker.Undo()); Assert.Equal("old", Assert.Single(tracker.Snapshot.Rooms).Id);
        }
        finally { model.Detach(); }
    }

    [AvaloniaFact]
    public void GridAdjacentTilesSelectIndependentlyAndDoNotCreateConnections()
    {
        var tracker = new RoomMapTracker(new MapSnapshot(
            [new("west", "Western forest", "Trees", "Island", 0, 0, 0, false) { Environment = "forest" },
             new("east", "Eastern water", "Water", "Island", 1, 0, 0, false) { Environment = "water" }],
            [], [], null, MapTrackingState.Unknown, RoomDataSource.Text, 0));
        var model = new MapViewModel(tracker); model.Attach(); model.IsGridMode = true;
        var canvas = new RoomMapControl { Model = model, ClipToBounds = true };
        var window = new Window { Content = canvas, Width = 500, Height = 400 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            var projection = new MapViewport(model.CenterX, model.CenterY, model.PanX, model.PanY, model.Zoom, canvas.Bounds.Width, canvas.Bounds.Height);
            var boundary = projection.Project(.5, 0);
            Click(window, new Point(boundary.X - 1, boundary.Y)); Assert.Equal("west", model.SelectedRoomId);
            Click(window, new Point(boundary.X + 1, boundary.Y)); Assert.Equal("east", model.SelectedRoomId);
            Assert.Empty(tracker.Snapshot.Links);
        }
        finally { window.Close(); model.Detach(); }
    }

    [AvaloniaFact]
    public void EditDragSnapsRoomAndUndoRestoresCoordinates()
    {
        var tracker = new RoomMapTracker(new MapSnapshot([new("room", "Room", "", null, 0, 0, 0, false)], [], [], null, MapTrackingState.Unknown, RoomDataSource.Text, 0));
        var model = new MapViewModel(tracker); model.Attach(); model.IsEditMode = true;
        var canvas = new RoomMapControl { Model = model };
        var window = new Window { Content = canvas, Width = 500, Height = 400 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            var projection = new MapViewport(model.CenterX, model.CenterY, model.PanX, model.PanY, model.Zoom, canvas.Bounds.Width, canvas.Bounds.Height);
            var from = projection.Project(0, 0); var to = projection.Project(2.1, 1.2);
            window.MouseDown(from, MouseButton.Left); window.MouseMove(to); window.MouseUp(to, MouseButton.Left);
            Assert.Equal(2, tracker.Snapshot.Rooms[0].X); Assert.Equal(1, tracker.Snapshot.Rooms[0].Y);
            Assert.Equal(0, model.PanX); Assert.Equal(0, model.PanY);
            Assert.True(tracker.Undo()); Assert.Equal(0, tracker.Snapshot.Rooms[0].X); Assert.Equal(0, tracker.Snapshot.Rooms[0].Y);
        }
        finally { window.Close(); model.Detach(); }
    }

    [AvaloniaFact]
    public void TerrainGridAndConnectedRoomsRenderWithEditorControls()
    {
        var rooms = new List<MapRoom>(); var links = new List<MapLink>();
        for (var y = 0; y < 15; y++)
            for (var x = 0; x < 22; x++)
            {
                var terrain = y < 2 || x < 2 || x > 19 ? "water" : x == 10 || y == 7 ? "road" : x < 6 ? "desert" : (x + y) % 7 < 3 ? "forest" : "grassland";
                var id = $"s:r{x}-{y}";
                rooms.Add(new(id, $"{terrain} {x}, {y}", "A room in the local rendering fixture.", "The Emerald Isle", x, y, 0, false, $"r{x}-{y}")
                { Environment = terrain, Symbol = x == 15 && y == 11 ? "△" : null, Notes = x == 9 && y == 8 ? "The northern gate" : "" });
                if (x > 0) { links.Add(new(id, $"s:r{x-1}-{y}", "west", true)); links.Add(new($"s:r{x-1}-{y}", id, "east", true)); }
                if (y > 0) { links.Add(new(id, $"s:r{x}-{y-1}", "south", true)); links.Add(new($"s:r{x}-{y-1}", id, "north", true)); }
            }
        var tracker = new RoomMapTracker(new MapSnapshot(rooms, links, [], null, MapTrackingState.Unknown, RoomDataSource.Gmcp, 0));
        tracker.Observe(new RoomObservation("r10-7", "Crossroads", "", new Dictionary<string,string?>(), "The Emerald Isle", RoomDataSource.Gmcp));
        var model = new MapViewModel(tracker);
        var view = new MapView(model); var window = new Window { Content = view, Width = 1000, Height = 900 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); model.IsGridMode = true; model.FitFloorCommand.Execute(null);
            Assert.Equal(330, model.VisibleRooms.Count);
            Assert.True(double.IsFinite(model.CenterX) && double.IsFinite(model.Zoom));
            Capture(window, "map-terrain-grid.png");
            model.SelectRoom("s:r15-11"); model.PlanRouteCommand.Execute(null);
            Capture(window, "map-terrain-route.png");
            Assert.NotNull(model.PlannedRoute);
            model.IsGridMode = false; model.Zoom = .47;
            Capture(window, "map-connected-rooms.png");
            view.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().Single(c => c.Name == "MapToolsToggle").IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(view.GetVisualDescendants().OfType<Expander>().FirstOrDefault(c => c.Name == "MapRouteTools"));
        }
        finally { window.Close(); }
    }
    private static void Click(Window window, Point point) { window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs(); }
    private static void Capture(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2); Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
        var directory = Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory); frame.Save(Path.Combine(directory, name), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
