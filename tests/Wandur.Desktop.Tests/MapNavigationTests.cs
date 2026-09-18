using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Wandur.Core.Mapping;
using Wandur.Core.Settings;
using Wandur.Desktop;

namespace Wandur.Desktop.Tests;

public sealed class MapNavigationTests
{
    [AvaloniaFact]
    public async Task WalkerRecognizesVnumRoomArrivals()
    {
        await using var world = await World.Open();
        var walk = world.Controller.StartMapWalkAsync(world.Route);
        Assert.Equal("north", await world.Command());
        await world.Output(new byte[] { 255, 250, 201 }.Concat(Encoding.UTF8.GetBytes("Room.Info {\"vnum\":2,\"name\":\"Room 2\",\"exits\":{\"east\":\"O\"},\"planet\":\"Ring of Kafrene\"}")).Concat(new byte[] { 255, 240 }).ToArray());
        Assert.Equal("east", await world.Command());
        await world.Output(new byte[] { 255, 250, 201 }.Concat(Encoding.UTF8.GetBytes("Room.Info {\"vnum\":3,\"name\":\"Room 3\",\"planet\":\"Ring of Kafrene\"}")).Concat(new byte[] { 255, 240 }).ToArray());
        await walk;
        Assert.Equal("s:3", world.Controller.Map.Snapshot.CurrentRoomId);
        Assert.False(world.Controller.IsMapWalking);
        Assert.DoesNotContain(world.Controller.Map.Snapshot.Links, link => link.ToId == "s:O");
    }

    [AvaloniaFact]
    public async Task DoubleClickingARoomWalksAndToolbarCanStop()
    {
        await using var world = await World.Open();
        var view = new Wandur.Desktop.Views.MapView(world.Controller);
        var window = new Avalonia.Controls.Window { Content = view, Width = 550, Height = 600 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            var canvas = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(view).OfType<Wandur.Desktop.Views.RoomMapControl>().Single();
            var point = canvas.TranslatePoint(view.Model.CreateViewport(canvas.Bounds.Width, canvas.Bounds.Height).Project(1, 1), window)!.Value;
            for (var i = 0; i < 2; i++)
            {
                Avalonia.Headless.HeadlessWindowExtensions.MouseDown(window, point, Avalonia.Input.MouseButton.Left);
                Avalonia.Headless.HeadlessWindowExtensions.MouseUp(window, point, Avalonia.Input.MouseButton.Left);
            }
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("north", await world.Command());
            await world.Quiet();
            await world.Room("2");
            Assert.Equal("east", await world.Command());
            var stop = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(view).OfType<Avalonia.Controls.Button>().Single(b => b.Name == "MapStopWalkingToolbar");
            Assert.True(stop.IsVisible);
            stop.Command!.Execute(null);
            Assert.False(world.Controller.IsMapWalking);
            await world.Room("3"); await world.Quiet();
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task WalkSendsOnlyOneStepUntilExpectedAuthoritativeArrival()
    {
        await using var world = await World.Open();
        var walk = world.Controller.StartMapWalkAsync(world.Route);
        Assert.Equal("north", await world.Command());
        await world.Quiet();
        Assert.True(world.Controller.IsMapWalking);
        await world.Room("2");
        Assert.Equal("east", await world.Command());
        await world.Room("3");
        await walk;
        Assert.False(world.Controller.IsMapWalking);
        Assert.Equal("s:3", world.Controller.Map.Snapshot.CurrentRoomId);
    }

    [AvaloniaFact]
    public async Task RoomShortcutDoesNotWalkFromEditorOrAcrossAnUnknownRoute()
    {
        await using var world = await World.Open();
        var model = new Wandur.Desktop.ViewModels.MapViewModel(world.Controller);
        model.Attach();
        try
        {
            model.IsEditMode = true;
            await model.WalkToRoomCommand.ExecuteAsync("s:3");
            Assert.False(world.Controller.IsMapWalking); await world.Quiet();
            model.IsEditMode = false;
            Assert.True(world.Controller.Map.RemoveLink("s:2", "east"));
            await model.WalkToRoomCommand.ExecuteAsync("s:3");
            Assert.Null(model.PlannedRoute); Assert.True(model.HasWalkFeedback);
            Assert.Equal(Wandur.Core.Localization.Strings.MapRouteUnavailable, model.NavigationFeedback);
            await world.Quiet();
        }
        finally { model.Detach(); }
    }

    [AvaloniaFact]
    public async Task TwoQuickClicksOnDifferentRoomsDoNotStartWalking()
    {
        await using var world = await World.Open();
        var view = new Wandur.Desktop.Views.MapView(world.Controller);
        var window = new Avalonia.Controls.Window { Content = view, Width = 550, Height = 600 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            var canvas = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(view).OfType<Wandur.Desktop.Views.RoomMapControl>().Single();
            var pointer = new Avalonia.Input.Pointer(101, Avalonia.Input.PointerType.Mouse, true);
            void Click(double x, int count)
            {
                var point = canvas.TranslatePoint(view.Model.CreateViewport(canvas.Bounds.Width, canvas.Bounds.Height).Project(x, 1), window)!.Value;
                canvas.RaiseEvent(new Avalonia.Input.PointerPressedEventArgs(canvas, pointer, window, point, (ulong)count,
                    new Avalonia.Input.PointerPointProperties(Avalonia.Input.RawInputModifiers.LeftMouseButton, Avalonia.Input.PointerUpdateKind.LeftButtonPressed), Avalonia.Input.KeyModifiers.None, count));
                canvas.RaiseEvent(new Avalonia.Input.PointerReleasedEventArgs(canvas, pointer, window, point, (ulong)count,
                    new Avalonia.Input.PointerPointProperties(), Avalonia.Input.KeyModifiers.None, Avalonia.Input.MouseButton.Left));
            }
            Click(0, 1); Assert.Equal("s:2", view.Model.SelectedRoomId);
            Click(1, 2); Assert.Equal("s:3", view.Model.SelectedRoomId);
            Assert.False(world.Controller.IsMapWalking); await world.Quiet();
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task WrongRoomAndMultipleArrivalsInOneBurstNeverAdvance()
    {
        await using var world = await World.Open();
        var walk = world.Controller.StartMapWalkAsync(world.Route);
        Assert.Equal("north", await world.Command());
        await world.Output(World.RoomBytes("2").Concat(World.RoomBytes("1")).ToArray());
        await walk;
        Assert.False(world.Controller.IsMapWalking);
        await world.Quiet();
    }

    [AvaloniaTheory]
    [InlineData("The door is closed.\n> ")]
    [InlineData("Password: ")]
    public async Task FailureOrPasswordInTheArrivalBurstPreventsTheNextMove(string text)
    {
        await using var world = await World.Open();
        var walk = world.Controller.StartMapWalkAsync(world.Route);
        Assert.Equal("north", await world.Command());
        await world.Output(World.RoomBytes("2").Concat(Encoding.UTF8.GetBytes(text)).ToArray());
        await walk;
        Assert.False(world.Controller.IsMapWalking);
        await world.Quiet();
    }

    [AvaloniaTheory]
    [InlineData("blocked")]
    [InlineData("private")]
    [InlineData("echo-burst")]
    [InlineData("stop")]
    [InlineData("disconnect")]
    [InlineData("timeout")]
    public async Task UnsafeOrUnacknowledgedStepsStopWithoutAnotherCommand(string reason)
    {
        await using var world = await World.Open();
        world.Controller.MapWalkStepTimeout = TimeSpan.FromMilliseconds(150);
        var walk = world.Controller.StartMapWalkAsync(world.Route);
        Assert.Equal("north", await world.Command());
        switch (reason)
        {
            case "blocked": await world.Output(Encoding.UTF8.GetBytes("The door is closed.\n> ")); break;
            case "private": world.Controller.SetManualPrivate(true); break;
            case "echo-burst": await world.Output(new byte[] { 255, 251, 1, 255, 252, 1 }.Concat(World.RoomBytes("2")).ToArray()); break;
            case "stop": world.Controller.StopMapWalk(); break;
            case "disconnect": await world.Controller.DisconnectAsync(); break;
        }
        await walk;
        Assert.False(world.Controller.IsMapWalking);
        if (reason is not ("disconnect" or "echo-burst")) await world.Quiet();
    }

    [AvaloniaFact]
    public async Task UnsupportedLaterCommandRejectsTheWholeRouteBeforeSending()
    {
        await using var world = await World.Open();
        Assert.True(world.Controller.Map.UpsertLink(world.Route.Steps[1] with { Command = "drop all" }));
        var route = MapRoutePlanner.FindRoute(world.Controller.Map.Snapshot, "s:1", "s:3");
        Assert.NotNull(route);
        await world.Controller.StartMapWalkAsync(route);
        Assert.False(world.Controller.IsMapWalking);
        await world.Quiet();
    }

    [AvaloniaFact]
    public async Task ManualCommandTakesOverAndLateArrivalCannotRestartWalking()
    {
        await using var world = await World.Open();
        var walk = world.Controller.StartMapWalkAsync(world.Route);
        Assert.Equal("north", await world.Command());
        await world.Controller.SendAsync("look");
        Assert.Equal("look", await world.Command());
        await world.Room("2");
        await walk;
        await world.Quiet();
    }

    [AvaloniaFact]
    public async Task EditingTheGraphStopsAnOutstandingStep()
    {
        await using var world = await World.Open();
        var walk = world.Controller.StartMapWalkAsync(world.Route);
        Assert.Equal("north", await world.Command());
        Assert.True(world.Controller.Map.RemoveLink("s:2", "east"));
        await walk;
        Assert.False(world.Controller.IsMapWalking);
        await world.Room("2");
        await world.Quiet();
    }

    [AvaloniaFact]
    public async Task AutomaticLoginPreventsWalkingBeforeAnyMovementIsSent()
    {
        await using var world = await World.Open(autoLogin: true);
        await world.Controller.StartMapWalkAsync(world.Route);
        Assert.False(world.Controller.IsMapWalking);
        await world.Quiet();
    }

    [AvaloniaFact]
    public async Task AnUnacknowledgedManualMoveCannotBeUsedAsTheStartOfAWalk()
    {
        await using var world = await World.Open();
        await world.Controller.SendAsync("north");
        Assert.Equal("north", await world.Command());
        await world.Controller.StartMapWalkAsync(world.Route);
        Assert.False(world.Controller.IsMapWalking);
        await world.Quiet();
    }

    [AvaloniaFact]
    public async Task EnteringTheLocalExerciseStopsLiveWalking()
    {
        await using var world = await World.Open();
        var walk = world.Controller.StartMapWalkAsync(world.Route);
        Assert.Equal("north", await world.Command());
        var model = new Wandur.Desktop.ViewModels.MapViewModel(world.Controller);
        model.ToggleExerciseCommand.Execute(null);
        Assert.True(model.IsExercise);
        await walk;
        Assert.False(world.Controller.IsMapWalking);
        await world.Room("2");
        await world.Quiet();
    }

    [AvaloniaFact]
    public async Task RejectedRoomMetadataCannotCompleteTheLastStep()
    {
        await using var world = await World.Open();
        var walk = world.Controller.StartMapWalkAsync(new([world.Route.Steps[0]], 1));
        Assert.Equal("north", await world.Command());
        var message = "Room.Info {\"num\":2,\"name\":\"" + new string('x', 513) + "\"}";
        await world.Output(new byte[] { 255, 250, 201 }.Concat(Encoding.UTF8.GetBytes(message)).Concat(new byte[] { 255, 240 }).ToArray());
        await walk;
        Assert.False(world.Controller.IsMapWalking);
        Assert.Equal("s:1", world.Controller.Map.Snapshot.CurrentRoomId);
        Assert.NotEqual(Wandur.Core.Localization.Strings.MapWalkComplete, world.Controller.MapWalkStatus);
        await world.Quiet();
    }

    [AvaloniaFact]
    public async Task EvidenceDistinguishesNegotiationFromReceivedFields()
    {
        await using var world = await World.Open();
        var evidence = world.Controller.ProtocolEvidence;
        Assert.Equal(Wandur.Core.Protocol.TelnetOptionState.Enabled, evidence.Gmcp);
        Assert.Equal(Wandur.Core.Protocol.TelnetOptionState.Unknown, evidence.Msdp);
        Assert.True(evidence.ReceivedRoomId);
        Assert.False(evidence.ReceivedExits);
        Assert.False(evidence.ReceivedTerrain);
        Assert.False(evidence.ReceivedCoordinates);
        await world.Output(new byte[] { 255, 250, 201 }.Concat(Encoding.UTF8.GetBytes("Room.Info {\"num\":1,\"name\":\"Room 1\",\"exits\":{},\"environment\":\"forest\",\"coords\":{\"x\":2,\"y\":3,\"z\":0}}" )).Concat(new byte[] { 255, 240 }).ToArray());
        evidence = world.Controller.ProtocolEvidence;
        Assert.True(evidence.ReceivedExits);
        Assert.True(evidence.ReceivedTerrain);
        Assert.True(evidence.ReceivedCoordinates);
        Assert.Equal(RoomDataSource.Gmcp, evidence.LastRoomSource);
    }

    private sealed class World(TcpListener listener, TcpClient socket, WorkspaceController controller, MapRoute route) : IAsyncDisposable
    {
        public WorkspaceController Controller { get; } = controller;
        public MapRoute Route { get; } = route;
        public static async Task<World> Open(bool autoLogin = false)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var profile = new ConnectionProfile { Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port };
            var maps = new MemoryRoomMapStore();
            MapRoom[] rooms = [new("s:1", "Room 1", "", null, 0, 0, 0, false, "1"), new("s:2", "Room 2", "", null, 0, 1, 0, false, "2"), new("s:3", "Room 3", "", null, 1, 1, 0, false, "3")];
            MapLink[] links = [new("s:1", "s:2", "north", true), new("s:2", "s:3", "east", true)];
            maps.Save(profile.Host, profile.Port, new(rooms, links, [], null, MapTrackingState.Unknown, RoomDataSource.Gmcp, 0));
            var controller = new WorkspaceController(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), new SettingsStore(Path.Combine(Path.GetTempPath(), "wandur-navigation-" + Guid.NewGuid(), "settings.json")), new MemoryPasswordVault(), maps, new RecordingScriptFactory(), new MemoryScriptLibraryStore());
            if (autoLogin)
            {
                await controller.SaveWorldAsync(profile with { Username = "tester", AutoLogin = true }, "test-secret", true);
                profile = Assert.Single(controller.Settings.Profiles);
            }
            await controller.StartAsync(profile);
            var socket = await listener.AcceptTcpClientAsync();
            var world = new World(listener, socket, controller, new(links, 2));
            await world.Output(new byte[] { 255, 251, 201 }.Concat(RoomBytes("1")).ToArray());
            await ScriptSessionTests.WaitFor(() => controller.Map.Snapshot.State == MapTrackingState.Confirmed);
            // Discard negotiation responses before checking movement commands.
            while (socket.Available > 0) await socket.GetStream().ReadExactlyAsync(new byte[socket.Available]);
            return world;
        }
        public static byte[] RoomBytes(string id) => new byte[] { 255, 250, 201 }.Concat(Encoding.UTF8.GetBytes($"Room.Info {{\"num\":\"{id}\",\"name\":\"Room {id}\"}}")).Concat(new byte[] { 255, 240 }).ToArray();
        public async Task Room(string id) { await Output(RoomBytes(id)); await ScriptSessionTests.WaitFor(() => Controller.Map.Snapshot.CurrentRoomId == "s:" + id); }
        public async Task Output(byte[] bytes)
        {
            await socket.GetStream().WriteAsync(bytes);
            for (var i = 0; i < 5; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); Controller.FlushOutput(); }
        }
        public async Task<string> Command()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var bytes = new List<byte>();
            while (true)
            {
                var b = new byte[1];
                Assert.Equal(1, await socket.GetStream().ReadAsync(b, timeout.Token));
                if (b[0] == 10) return Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\r');
                bytes.Add(b[0]);
            }
        }
        public async Task Quiet()
        {
            await Task.Delay(60); Dispatcher.UIThread.RunJobs(); Controller.FlushOutput();
            Assert.Equal(0, socket.Available);
        }
        public async ValueTask DisposeAsync() { await Controller.DisposeAsync(); socket.Dispose(); listener.Dispose(); }
    }
}
