using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Mapping;
using Wandur.Core.Settings;
using Wandur.Desktop.Terminal;
using Wandur.Desktop.Views;
using Wandur.Models;

namespace Wandur.Desktop.Tests;

/// <summary>A real client capture for wandur-site, with original fictional content and loopback-only traffic.</summary>
public sealed class SiteScreenshotTests
{
    [AvaloniaFact]
    public async Task MockMudRendersCurrentSkinMapChannelsAndVitals()
    {
        var path = Path.Combine(Path.GetTempPath(), "wandur-site-capture-" + Guid.NewGuid());
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var mapping = new WorldMapping
        {
            WorldId = "starfall-reach-demo", Endpoint = new("127.0.0.1", port),
            SchemaFingerprint = new('a', 64), GeneratedAt = DateTimeOffset.UtcNow,
            Bindings = [
                new() { Source = new("GMCP", "Char.Vitals", "/hp"), Target = new("character", "resource", "health", "current"), Label = "Health" },
                new() { Source = new("GMCP", "Char.Vitals", "/maxhp"), Target = new("character", "resource", "health", "maximum"), Label = "Health" },
                new() { Source = new("GMCP", "Char.Vitals", "/energy"), Target = new("character", "resource", "energy", "current"), Label = "Energy" },
                new() { Source = new("GMCP", "Char.Vitals", "/maxenergy"), Target = new("character", "resource", "energy", "maximum"), Label = "Energy" },
                new() { Source = new("GMCP", "Char.Vitals", "/movement"), Target = new("character", "resource", "movement", "current"), Label = "Movement" },
                new() { Source = new("GMCP", "Char.Vitals", "/maxmovement"), Target = new("character", "resource", "movement", "maximum"), Label = "Movement" }
            ]
        };
        var profile = new ConnectionProfile { Name = "Starfall Reach", Host = "127.0.0.1", Port = port, ProtocolMapping = mapping };
        var store = new SettingsStore(Path.Combine(path, "settings.json"));
        store.Save(new ClientSettings
        {
            Theme = "Slate", Language = "en", FontSize = 16, UseWorldThemes = false,
            ClassifyRoomsLocally = false,
            Profiles = [profile,
                new() { Name = "The Verdant Roads", Host = "verdant.example" },
                new() { Name = "Emberwake", Host = "emberwake.example" }]
        });
        var window = new MainWindow(new TranscriptDisplayFactory(), store, new MemoryPasswordVault(),
            new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore())
        { Width = 1600, Height = 1000 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var opening = window.Sessions.OpenAsync(profile);
            using var peer = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await opening;
            window.Controller.ClearTranscript();
            var stream = peer.GetStream();
            await stream.WriteAsync(new byte[] { 255, 251, 201 });
            await stream.WriteAsync(Gmcp("Char.Vitals", new { hp = 842, maxhp = 960, energy = 318, maxenergy = 400, movement = 176, maxmovement = 200 }));
            foreach (var (speaker, text) in new[]
            {
                ("Mira", "Beacon is online. We have a route through the storm."),
                ("Tarin", "Bringing the survey skiff around to the eastern berth."),
                ("Sable", "I saved you a seat. And a spare oxygen cell."),
                ("Mira", "Meet at the observation deck when you're ready."),
                ("Tarin", "Copy that. Let's see what's out there.")
            })
                await stream.WriteAsync(Gmcp("Comm.Channel.Text", new { channel = "crew", talker = speaker, text }));

            // Observations are the same inputs the GMCP decoder supplies. Explicit coordinates keep
            // this fictional chart stable across capture runs, without editing any real user's map.
            var rooms = new (string Id, string Name, int X, int Y, string[] Neighbors)[]
            {
                ("beacon", "Beacon Anchorage", 0, 0, ["north:deck", "east:berths", "west:concourse", "south:transit"]),
                ("deck", "Observation Deck", 0, -1, ["south:beacon", "east:array"]),
                ("array", "Signal Array", 1, -1, ["west:deck", "south:berths"]),
                ("berths", "Eastern Berths", 1, 0, ["north:array", "west:beacon", "south:cargo"]),
                ("cargo", "Cargo Exchange", 1, 1, ["north:berths", "west:transit"]),
                ("concourse", "Concourse", -1, 0, ["east:beacon", "north:garden"]),
                ("garden", "Sky Garden", -1, -1, ["south:concourse"]),
                ("transit", "Transit Ring", 0, 1, ["north:beacon", "east:cargo"])
            };
            foreach (var room in rooms.Append(rooms[0]))
                window.Controller.Map.Observe(new RoomObservation(room.Id, room.Name, "", room.Neighbors
                    .Select(n => n.Split(':')).ToDictionary(n => n[0], n => (string?)n[1]), "Beacon Station", RoomDataSource.Gmcp)
                { X = room.X, Y = -room.Y, Z = 0, Environment = room.Id == "garden" ? "forest" : "inside" });

            await stream.WriteAsync(Encoding.UTF8.GetBytes(Transcript.Replace("\n", "\r\n", StringComparison.Ordinal)));
            for (var attempt = 0; attempt < 100; attempt++)
            {
                await Task.Delay(20);
                Dispatcher.UIThread.RunJobs(); window.Controller.FlushOutput();
                if (window.Controller.Terminal.PlainText.Contains("Beacon Anchorage >", StringComparison.Ordinal)) break;
            }
            Assert.Contains("Beacon Anchorage >", window.Controller.Terminal.PlainText);
            Assert.Equal(8, window.Controller.Map.Snapshot.Rooms.Count);
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var mapControl = window.GetVisualDescendants().OfType<RoomMapControl>().Single();
            mapControl.Model!.FitFloorCommand.Execute(null);
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Equal(3, window.GetVisualDescendants().OfType<ResourceBarsView>().Single()
                .GetVisualDescendants().OfType<ProgressBar>().Count());
            Assert.Equal(5, window.GetVisualDescendants().OfType<ChannelMessageList>().Single().Rows.Count);
            Assert.Equal("STARFALL REACH", window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "AppTitle").Text);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(4);
            using var image = window.CaptureRenderedFrame();
            Assert.NotNull(image);
            Assert.Equal(1600, image.PixelSize.Width);
            Assert.Equal(1000, image.PixelSize.Height);
            if (Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR") is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
                image.Save(Path.Combine(directory, "client-slate.png"), new PngBitmapEncoderOptions());
            }
        }
        finally { await window.Sessions.DisposeAsync(); window.Close(); }
    }

    private static byte[] Gmcp(string package, object payload) =>
        [255, 250, 201, .. Encoding.UTF8.GetBytes(package + " " + JsonSerializer.Serialize(payload)), 255, 240];

    private static readonly string Transcript = """
        \e[36mS T A R F A L L   R E A C H\e[0m
        A thousand distant suns. One way forward.

        \e[90m> look\e[0m

        \e[1;36mBEACON ANCHORAGE\e[0m
        \e[90mBeacon Station / Outer Reach\e[0m

        Beyond the glass, a silver freighter slips out of the nebula.
        Its running lights scatter across the observation deck like
        sparks on dark water. Somewhere below, the station wakes.

        A courier leans against the rail, turning a brass compass in
        her hands. The needle points toward the uncharted stars.

        \e[33mMira says, "The beacon found something. You should see this."\e[0m

        \e[37mExits:\e[0m \e[32mnorth\e[0m, \e[32meast\e[0m, \e[32msouth\e[0m, \e[32mwest\e[0m
        \e[37mNearby:\e[0m Mira, a station courier, a maintenance drone

        \e[90m> examine compass\e[0m

        The case is warm. Beneath its scratched crystal, a tiny
        star chart unfolds, tracing a path beyond the shipping lanes.

        \e[1;33mNew discovery: The Lantern Route\e[0m
        \e[36mYour chart has been updated. +120 exploration experience.\e[0m

        \e[90m> crew On my way. Keep the engines warm.\e[0m
        \e[36m[Crew] You:\e[0m On my way. Keep the engines warm.

        \e[32m842/960 hp\e[0m  \e[36m318/400 energy\e[0m  \e[33m176/200 move\e[0m
        \e[90mBeacon Anchorage >\e[0m

        """.Replace("\\e", "\u001b", StringComparison.Ordinal);
}
