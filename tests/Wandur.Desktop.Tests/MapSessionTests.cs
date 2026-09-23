using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Wandur.Core.Mapping;
using Wandur.Core.Settings;
using Wandur.Desktop;

namespace Wandur.Desktop.Tests;

public sealed class MapSessionTests
{
    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ProtocolRelocationWithoutADirectionUpdatesLocationWithoutCreatingAnExit(bool msdp, bool interruptedMove)
    {
        var directory = Path.Combine(Path.GetTempPath(), "wandur-map-session-" + Guid.NewGuid());
        await using var controller = new WorkspaceController(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(),
            new SettingsStore(Path.Combine(directory, "settings.json")), new MemoryPasswordVault(),
            new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        using var server = new TcpListener(IPAddress.Loopback, 0); server.Start();
        await controller.StartAsync(new ConnectionProfile { Host = "127.0.0.1", Port = ((IPEndPoint)server.LocalEndpoint).Port });
        using var socket = await server.AcceptTcpClientAsync();
        async Task Output(byte[] bytes)
        {
            await socket.GetStream().WriteAsync(bytes);
            for (var i = 0; i < 10; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); controller.FlushOutput(); }
        }
        byte protocol = msdp ? (byte)69 : (byte)201;
        byte[] Room(int id) => new byte[] { 255, 250, protocol }
            .Concat(Encoding.UTF8.GetBytes(msdp
                ? $"\u0001ROOM_VNUM\u0002{id}\u0001ROOM_NAME\u0002Room {id}"
                : $"Room.Info {{\"num\":{id},\"name\":\"Room {id}\"}}"))
            .Concat(new byte[] { 255, 240 }).ToArray();
        await Output(new byte[] { 255, 251, protocol }.Concat(Room(1)).ToArray());
        if (interruptedMove)
        {
            await controller.SendAsync("north");
            await Output(Room(1)); // old room metadata does not acknowledge the move
            await controller.SendAsync("enter portal");
        }
        await Output(Room(2)); // authoritative location, with no direction for this transition
        Assert.Equal("s:2", controller.Map.Snapshot.CurrentRoomId);
        Assert.Equal(MapTrackingState.Confirmed, controller.Map.Snapshot.State);
        Assert.Equal(2, controller.Map.Snapshot.Rooms.Count);
        Assert.Empty(controller.Map.Snapshot.Links);
        await controller.SendAsync("east");
        await Output(Room(3));
        var link = Assert.Single(controller.Map.Snapshot.Links);
        Assert.Equal("s:2", link.FromId);
        Assert.Equal("s:3", link.ToId);
        Assert.Equal("east", link.Direction);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FlagOnlyExitsLearnRoutesFromMovementDespiteRepeatedOriginUpdates(bool repeatedOrigin)
    {
        var directory = Path.Combine(Path.GetTempPath(), "wandur-map-session-" + Guid.NewGuid());
        await using var controller = new WorkspaceController(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(),
            new SettingsStore(Path.Combine(directory, "settings.json")), new MemoryPasswordVault(),
            new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        using var server = new TcpListener(IPAddress.Loopback, 0); server.Start();
        await controller.StartAsync(new ConnectionProfile { Host = "127.0.0.1", Port = ((IPEndPoint)server.LocalEndpoint).Port });
        using var socket = await server.AcceptTcpClientAsync();
        async Task Output(byte[] bytes)
        {
            await socket.GetStream().WriteAsync(bytes);
            for (var i = 0; i < 10; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); controller.FlushOutput(); }
        }
        static byte[] Room(int vnum, string name, string exit) => new byte[] { 255, 250, 201 }
            .Concat(Encoding.UTF8.GetBytes($"Room.Info {{\"vnum\":{vnum},\"name\":\"{name}\",\"planet\":\"Ring of Kafrene\",\"exits\":{{\"{exit}\":\"O\"}}}}"))
            .Concat(new byte[] { 255, 240 }).ToArray();
        var cockpit = Room(561, "The Cockpit", "south");
        var bunks = Room(562, "Passenger Bunks", "north");
        await Output(new byte[] { 255, 251, 201 }.Concat(cockpit).ToArray());
        await controller.SendAsync("south");
        if (repeatedOrigin) await Output(cockpit);
        await Output(bunks);
        Assert.NotNull(MapRoutePlanner.FindRoute(controller.Map.Snapshot, "s:561", "s:562"));
        Assert.Null(MapRoutePlanner.FindRoute(controller.Map.Snapshot, "s:562", "s:561"));
        await controller.SendAsync("north");
        if (repeatedOrigin) await Output(bunks);
        await Output(cockpit);
        Assert.NotNull(MapRoutePlanner.FindRoute(controller.Map.Snapshot, "s:562", "s:561"));
        Assert.Equal(2, controller.Map.Snapshot.Rooms.Count);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StructuredRoomsWithoutIdsDoNotAlsoMapTheTutorialText(bool textArrivesFirst)
    {
        var directory = Path.Combine(Path.GetTempPath(), "wandur-map-session-" + Guid.NewGuid());
        await using var controller = new WorkspaceController(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(),
            new SettingsStore(Path.Combine(directory, "settings.json")), new MemoryPasswordVault(),
            new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        using var server = new TcpListener(IPAddress.Loopback, 0); server.Start();
        await controller.StartAsync(new ConnectionProfile { Host = "127.0.0.1", Port = ((IPEndPoint)server.LocalEndpoint).Port });
        using var socket = await server.AcceptTcpClientAsync();
        async Task Output(byte[] bytes)
        {
            await socket.GetStream().WriteAsync(bytes);
            for (var i = 0; i < 10; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); controller.FlushOutput(); }
        }
        static byte[] Room(string name, string exit) => new byte[] { 255, 250, 201 }
            .Concat(Encoding.UTF8.GetBytes($"Room.Info {{\"name\":\"{name}\",\"exits\":[\"{exit}\"]}}"))
            .Concat(new byte[] { 255, 240 }).ToArray();
        var tutorial = Encoding.UTF8.GetBytes("\u001b[1mLEGENDS OF THE JEDI\u001b[0m\nA welcome tutorial.\nExits: north\n> ");
        if (textArrivesFirst) await Output(tutorial);
        await Output(new byte[] { 255, 251, 201 }.Concat(Room("Passenger Bunks", "north"))
            .Concat(tutorial).ToArray());
        Assert.Equal("Passenger Bunks", Assert.Single(controller.Map.Snapshot.Rooms).Name);
        Assert.True(controller.ProtocolEvidence.ReceivedName);
        Assert.False(controller.ProtocolEvidence.ReceivedRoomId);
        for (var i = 0; i < 3; i++)
        {
            await controller.SendAsync("north");
            await Output(Room("The Cockpit", "south"));
            // Text arrives separately after metadata, with different title/description formatting.
            await Output(Encoding.UTF8.GetBytes("Welcome to Legends of the Jedi! [Hotel]\nA tutorial replaces the room description.\nExits: south\n> "));
            await controller.SendAsync("south");
            await Output(Room("Passenger Bunks", "north"));
            await Output(Encoding.UTF8.GetBytes("Passenger Bunks [HOSPITAL][Hotel][ENGINE]\nBunks line the walls.\nExits: north\n> "));
        }
        var map = controller.Map.Snapshot;
        Assert.Equal(2, map.Rooms.Count);
        Assert.Equal("Passenger Bunks", map.Rooms.Single(r => r.Id == map.CurrentRoomId).Name);
        Assert.Equal(2, map.Links.Count);
        var bunks = map.Rooms.Single(r => r.Name == "Passenger Bunks");
        var cockpit = map.Rooms.Single(r => r.Name == "The Cockpit");
        Assert.Equal(bunks.X, cockpit.X); Assert.Equal(bunks.Y + 1, cockpit.Y);
    }

    [AvaloniaFact]
    public async Task TextMovementRequiresRoomEvidenceAndMapSurvivesReconnect()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wandur-map-session-" + Guid.NewGuid());
        var store = new SettingsStore(Path.Combine(directory, "settings.json"));
        var maps = new MemoryRoomMapStore();
        await using var controller = new WorkspaceController(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), store, new MemoryPasswordVault(), maps, new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        using var server = new TcpListener(IPAddress.Loopback, 0); server.Start();
        var profile = new ConnectionProfile { Host = "127.0.0.1", Port = ((IPEndPoint)server.LocalEndpoint).Port };
        await controller.StartAsync(profile);
        using var socket = await server.AcceptTcpClientAsync();
        async Task Output(string text)
        {
            var version = controller.OutputVersion;
            await socket.GetStream().WriteAsync(Encoding.UTF8.GetBytes(text));
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (controller.OutputVersion == version && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); await Task.Delay(15); }
            Assert.True(controller.OutputVersion > version);
        }
        await Output("Hall\nStone walls.\nExits: north\n> ");
        var first = controller.Map.Snapshot.CurrentRoomId;
        Assert.NotNull(first);
        await controller.SendAsync("north");
        Assert.Equal(first, controller.Map.Snapshot.CurrentRoomId);
        await Output("The door is cl");
        await Output("osed.\n> ");
        Assert.Equal(first, controller.Map.Snapshot.CurrentRoomId);
        await controller.SendAsync("north");
        await Output("Tower\nA brass bell.\nExits: south\n> ");
        Assert.Equal(2, controller.Map.Snapshot.Rooms.Count);
        Assert.Single(controller.Map.Snapshot.Links);
        await controller.DisconnectAsync();
        await controller.StartAsync(profile);
        using var reconnected = await server.AcceptTcpClientAsync();
        // Connecting does not await the background map restore.
        await controller.MapReady;
        Assert.Equal(2, controller.Map.Snapshot.Rooms.Count);
        Assert.Null(controller.Map.Snapshot.CurrentRoomId);
        await controller.DisconnectAsync();
    }
}
