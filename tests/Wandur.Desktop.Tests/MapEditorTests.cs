using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Mapping;
using Wandur.Core.Settings;
using Wandur.Desktop.ViewModels;
using Wandur.Desktop.Views;

namespace Wandur.Desktop.Tests;

public sealed class MapEditorTests
{
    private static RoomMapTracker Map()
    {
        var tracker = new RoomMapTracker(new MapSnapshot(
            [new("a", "Atrium", "Stone entrance", "City", 0, 0, 0, false),
             new("b", "Balcony", "Upper floor", "City", 0, 0, 1, false),
             new("c", "Clearing", "Green path", "Woods", 0, 0, 0, false) { Notes = "A hidden brass key", ServerId = "server-forest" },
             new("d", "Watchtower", "Above the trees", "Woods", 1, 0, 2, false)],
            [new("a", "b", "up", true), new("b", "c", "north", true), new("a", "c", "east", true) { Weight = 10 }],
            [], null, MapTrackingState.Unknown, RoomDataSource.Gmcp, 0));
        tracker.SetCurrentRoom("a"); return tracker;
    }

    [Fact]
    public void AreaGridSettingsAndSearchRevealTheCorrectFloor()
    {
        var tracker = Map(); var model = new MapViewModel(tracker); model.Attach();
        try
        {
            Assert.Equal("City", model.SelectedArea);
            Assert.Equal("a", Assert.Single(model.VisibleRooms).Id);
            model.SelectedArea = "Woods"; model.IsGridMode = true; model.ShowFloor(2);
            Assert.Equal("d", Assert.Single(model.VisibleRooms).Id);
            model.SelectedArea = "City"; Assert.False(model.IsGridMode);
            model.SearchQuery = "brass key";
            model.SelectedSearchResult = Assert.Single(model.SearchResults);
            Assert.Equal("c", model.SelectedRoomId); Assert.Equal("Woods", model.SelectedArea);
            Assert.Equal(0, model.SelectedFloor); Assert.True(model.IsGridMode);
            tracker.SetCurrentRoom("a");
            Assert.Equal("Woods", model.SelectedArea); // Repeated reports preserve inspection.
            model.SearchQuery = "server-forest"; Assert.Equal("c", Assert.Single(model.SearchResults).Id);
            Assert.True(tracker.Snapshot.AreaSettings.Single(a => a.Area == "Woods").GridMode);
        }
        finally { model.Detach(); }
    }

    [Fact]
    public void RoomEditsMoveAcrossAreasAndUndoRedoRestoresMetadata()
    {
        var tracker = Map(); var model = new MapViewModel(tracker); model.Attach();
        try
        {
            model.SelectRoom("b"); model.IsEditMode = true;
            model.RoomEditor.Name = "Observatory"; model.RoomEditor.Area = "Sky";
            model.RoomEditor.X = "4"; model.RoomEditor.Y = "5"; model.RoomEditor.Z = "3";
            model.RoomEditor.Environment = "mountain"; model.RoomEditor.Color = "#AABBCC";
            model.RoomEditor.Symbol = "★"; model.RoomEditor.Notes = "A telescope"; model.RoomEditor.Weight = "2";
            model.RoomEditor.IsLocked = true; model.SaveRoomCommand.Execute(null);
            var edited = tracker.Snapshot.Rooms.Single(r => r.Id == "b");
            Assert.Equal("Observatory", edited.Name); Assert.Equal("Sky", model.SelectedArea); Assert.Equal(3, model.SelectedFloor);
            Assert.Equal(4, edited.X); Assert.Equal(5, edited.Y); Assert.Equal("mountain", edited.Environment);
            Assert.Equal("#AABBCC", edited.Color); Assert.Equal("★", edited.Symbol); Assert.Equal("A telescope", edited.Notes);
            Assert.Equal(2, edited.Weight); Assert.True(edited.IsLocked); Assert.True(edited.IsManuallyEdited);
            model.UndoMapCommand.Execute(null); Assert.Equal("Balcony", tracker.Snapshot.Rooms.Single(r => r.Id == "b").Name);
            model.RedoMapCommand.Execute(null);
            var redone = tracker.Snapshot.Rooms.Single(r => r.Id == "b");
            Assert.Equal(edited with { Revision = redone.Revision }, redone);
            model.RoomEditor.Weight = "NaN"; model.SaveRoomCommand.Execute(null);
            Assert.Equal(redone, tracker.Snapshot.Rooms.Single(r => r.Id == "b")); Assert.NotNull(model.EditMessage);
        }
        finally { model.Detach(); }
    }

    [Fact]
    public void AddDeleteMergeAndIntentionalMovementUseTheActiveTracker()
    {
        var live = Map(); var model = new MapViewModel(live); model.Attach();
        try
        {
            model.MoveRoom("a", 8, 9); Assert.Equal(0, live.Snapshot.Rooms.Single(r => r.Id == "a").X);
            model.IsEditMode = true; model.MoveRoom("a", 8, 9); Assert.Equal(8, live.Snapshot.Rooms.Single(r => r.Id == "a").X);
            model.AddRoomCommand.Execute(null); model.RoomEditor.Name = "New room"; model.SaveRoomCommand.Execute(null);
            var addedId = model.SelectedRoomId; Assert.NotNull(addedId); Assert.Equal(5, live.Snapshot.Rooms.Count);
            model.DeleteRoomCommand.Execute(null); Assert.Equal(4, live.Snapshot.Rooms.Count);
            model.UndoMapCommand.Execute(null); Assert.Equal(5, live.Snapshot.Rooms.Count);
            model.SelectRoom(addedId!); model.MergeTarget = live.Snapshot.Rooms.Single(r => r.Id == "c"); model.MergeRoomCommand.Execute(null);
            Assert.Equal(4, live.Snapshot.Rooms.Count); Assert.Equal("c", model.SelectedRoomId);
            var before = System.Text.Json.JsonSerializer.Serialize(live.Snapshot);
            model.ToggleExerciseCommand.Execute(null); model.IsEditMode = true; model.SelectRoom("a"); model.RoomEditor.Notes = "Exercise only"; model.SaveRoomCommand.Execute(null);
            Assert.NotSame(live, model.Tracker); Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(live.Snapshot));
        }
        finally { model.Detach(); }
    }

    [Fact]
    public void ExitEditingIsDirectedAndReturnLinksAreAnExplicitAtomicChoice()
    {
        var tracker = Map(); var model = new MapViewModel(tracker); model.Attach();
        try
        {
            model.SelectRoom("a"); model.IsEditMode = true; model.NewExitCommand.Execute(null);
            model.ExitEditor.Direction = "portal"; model.ExitEditor.Destination = tracker.Snapshot.Rooms.Single(r => r.Id == "d");
            model.ExitEditor.Command = "enter arch"; model.ExitEditor.Weight = "3";
            model.ExitEditor.Door = model.DoorChoices.Single(d => d.Value == MapDoorState.Closed);
            model.SaveExitCommand.Execute(null);
            var exit = tracker.Snapshot.Links.Single(l => l.FromId == "a" && l.Direction == "portal");
            Assert.Equal("enter arch", exit.Command); Assert.Equal(3, exit.Weight); Assert.Equal(MapDoorState.Closed, exit.DoorState);
            Assert.DoesNotContain(tracker.Snapshot.Links, l => l.FromId == "d" && l.ToId == "a");
            model.ExitEditor.Door = model.DoorChoices.Single(d => d.Value == MapDoorState.Open);
            model.ExitEditor.CreateReturnExit = true; model.ExitEditor.ReturnDirection = "leave"; model.SaveExitCommand.Execute(null);
            Assert.Contains(tracker.Snapshot.Links, l => l.FromId == "d" && l.ToId == "a" && l.Direction == "leave");
            model.UndoMapCommand.Execute(null);
            Assert.DoesNotContain(tracker.Snapshot.Links, l => l.FromId == "d" && l.ToId == "a");
            Assert.Equal(MapDoorState.Closed, tracker.Snapshot.Links.Single(l => l.FromId == "a" && l.Direction == "portal").DoorState);
        }
        finally { model.Detach(); }
    }

    [Fact]
    public void RoutePreviewUsesWeightsAndRecomputesAfterExclusions()
    {
        var tracker = Map(); var model = new MapViewModel(tracker); model.Attach();
        try
        {
            model.SelectRoom("c"); model.PlanRouteCommand.Execute(null);
            Assert.Equal(new[] { "up", "north" }, model.PlannedRoute!.Steps.Select(s => s.Direction));
            Assert.Equal(2, model.PlannedRoute.Cost);
            model.IsEditMode = true; model.RoomEditor.IsLocked = true; model.SaveRoomCommand.Execute(null);
            Assert.Null(model.PlannedRoute);
            model.UndoMapCommand.Execute(null); Assert.NotNull(model.PlannedRoute);
            model.ClearRouteCommand.Execute(null); Assert.Null(model.PlannedRoute);
        }
        finally { model.Detach(); }
    }

    [Fact]
    public void GridFitIncludesTheWholeOuterTiles()
    {
        var tracker = new RoomMapTracker(new MapSnapshot(
            [new("west", "West", "", null, -3, 0, 0, false), new("east", "East", "", null, 3, 0, 0, false)],
            [], [], null, MapTrackingState.Unknown, RoomDataSource.Text, 0));
        var model = new MapViewModel(tracker); model.Attach();
        try
        {
            model.SetViewportSize(280, 180); model.FitFloorCommand.Execute(null); model.IsGridMode = true;
            var viewport = new MapViewport(model.CenterX, model.CenterY, model.PanX, model.PanY, model.Zoom, 280, 180);
            foreach (var room in model.VisibleRooms)
            {
                var center = viewport.Project(room.X, room.Y);
                Assert.InRange(center.X - viewport.Scale / 2, 0, 280);
                Assert.InRange(center.X + viewport.Scale / 2, 0, 280);
            }
        }
        finally { model.Detach(); }
    }

    [AvaloniaFact]
    public async Task StartingAnotherMapDiscardsAnExerciseDraft()
    {
        var folder = Path.Combine(Path.GetTempPath(), "wandur-map-draft-" + Guid.NewGuid());
        await using var controller = new WorkspaceController(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), new SettingsStore(Path.Combine(folder, "settings.json")),
            new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        var model = new MapViewModel(controller); model.Attach();
        try
        {
            model.ToggleExerciseCommand.Execute(null); model.AddRoomCommand.Execute(null); model.RoomEditor.Name = "Unsaved exercise draft";
            Assert.True(model.IsAddingRoom);
            await controller.StartAsync(); Dispatcher.UIThread.RunJobs();
            Assert.False(model.IsExercise); Assert.False(model.IsAddingRoom); Assert.False(model.IsEditMode);
            Assert.Empty(model.RoomEditor.Name); Assert.Null(model.SelectedRoomId);
            Assert.Same(controller.Map, model.Tracker);
        }
        finally { model.Detach(); await controller.DisconnectAsync(); if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public void RoomFormSavesTypedValuesAndSearchNavigatesFromTheRenderedList()
    {
        var tracker = Map(); var model = new MapViewModel(tracker); var view = new MapEditorView(model);
        var window = new Window { Content = view, Width = 960, Height = 860 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            T Find<T>(string name) where T : Control => view.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
            Find<Expander>("MapTools").IsExpanded = true; Dispatcher.UIThread.RunJobs();
            Find<TextBox>("MapSearch").Text = "brass key"; Dispatcher.UIThread.RunJobs();
            Find<ListBox>("MapSearchResults").SelectedIndex = 0; Dispatcher.UIThread.RunJobs();
            Assert.Equal("c", model.SelectedRoomId); Assert.Equal("Woods", model.SelectedArea);
            Find<CheckBox>("MapEditMode").IsChecked = true; Dispatcher.UIThread.RunJobs();
            Find<TextBox>("MapRoomEnvironment").Text = "alien-grove"; Dispatcher.UIThread.RunJobs();
            Assert.Equal("alien-grove", model.RoomEditor.Environment);
            Find<ComboBox>("MapTerrainPreset").SelectedItem = MapEnvironmentPalette.Styles.Single(s => s.Key == "forest");
            Dispatcher.UIThread.RunJobs(); Assert.Equal("forest", model.RoomEditor.Environment);
            Find<TextBox>("MapRoomName").Text = "Secret clearing"; Find<TextBox>("MapRoomNotes").Text = "Marked by hand";
            var save = Find<Button>("MapSaveRoom"); save.BringIntoView(); save.Focus();
            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Secret clearing", tracker.Snapshot.Rooms.Single(r => r.Id == "c").Name);
            Assert.Equal("Marked by hand", tracker.Snapshot.Rooms.Single(r => r.Id == "c").Notes);
            Find<TextBox>("MapRoomName").BringIntoView();
            Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
            if (Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR") is { } directory)
            { Directory.CreateDirectory(directory); frame.Save(Path.Combine(directory, "map-room-editor.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions()); }
        }
        finally { window.Close(); }
    }
}
