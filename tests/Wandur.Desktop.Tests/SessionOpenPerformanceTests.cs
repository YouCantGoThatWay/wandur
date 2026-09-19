using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Data.Sqlite;
using Wandur.Core.Agents;
using Wandur.Core.Discovery;
using Wandur.Core.Mapping;
using Wandur.Core.Scripting;
using Wandur.Core.Settings;
using Wandur.Core.Storage;
using Wandur.Desktop.Services;

namespace Wandur.Desktop.Tests;

/// <summary>
/// Times the session open path against real SQLite storage in a headless window and prints the phase
/// breakdown. The bounds are deliberately generous: they guard against a regression that puts seconds
/// of blocking work back on the open path, not against ordinary machine-to-machine noise.
/// </summary>
public sealed class SessionOpenPerformanceTests
{
    private const int Rooms = 2000;
    private const int Links = 6000;
    private const double TabBudget = 3000;

    private sealed record Timing(string Label, double TabVisible, double Connected, double MapReady,
        IReadOnlyList<(string Phase, double Milliseconds)> Breakdown, IReadOnlyDictionary<string, int> Calls);

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "wandur-open-perf-" + Guid.NewGuid());
        private readonly List<TcpListener> _listeners = [];
        private readonly List<TcpClient> _accepted = [];
        public ClientDatabase Database { get; }
        public SqliteSettingsStore Settings { get; }
        public SqliteRoomMapStore Maps { get; }
        public SqliteWorldScriptLibraryStore Scripts { get; }
        public MainWindow? Window { get; private set; }

        public Harness()
        {
            Directory.CreateDirectory(_directory);
            Database = new(Path.Combine(_directory, "wandur.db"));
            Settings = new(Database, Path.Combine(_directory, "settings.json"));
            Maps = new(Database, Path.Combine(_directory, "maps"));
            Scripts = new(Database, Path.Combine(_directory, "scripts"));
        }

        public ConnectionProfile Listening(string name)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            _listeners.Add(listener);
            _ = AcceptAsync(listener);
            return new() { Name = name, Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port };
        }

        private async Task AcceptAsync(TcpListener listener)
        {
            try { while (true) _accepted.Add(await listener.AcceptTcpClientAsync()); }
            catch (Exception) { /* The listener stops with the harness. */ }
        }

        /// <summary>Saved worlds a regular player accumulates; every one of them is validated on the open path.</summary>
        public void SaveProfiles(params ConnectionProfile[] profiles) =>
            Settings.Save(new ClientSettings { Profiles = [.. profiles, .. Enumerable.Range(0, 10)
                .Select(i => new ConnectionProfile { Name = "Saved world " + i, Host = "world" + i + ".example.test", Port = 4000 + i })] });

        public MainWindow Open()
        {
            SqliteConnection.ClearAllPools(); // Seeding must not leave a warm connection the first open inherits.
            Window = new(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), Settings, new MemoryPasswordVault(),
                Maps, new RecordingScriptFactory(), Scripts,
                catalog: new WorldCatalog(Path.Combine(_directory, "directory.json"), http: OfflineHttp.Client()),
                agents: new AgentClientServices(new SqliteAgentProfileStore(Database), new AgentProviderRegistry([]), new MemoryPasswordVault()))
            { Width = 1200, Height = 800 };
            Window.Show();
            Dispatcher.UIThread.RunJobs();
            return Window;
        }

        public async ValueTask DisposeAsync()
        {
            SessionOpenTrace.Enabled = false;
            if (Window is not null) { await Window.Sessions.DisposeAsync(); Window.Close(); }
            foreach (var listener in _listeners) listener.Stop();
            foreach (var socket in _accepted) socket.Dispose();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_directory, true); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public async Task OpeningAPlainWorldShowsTheTabWithoutWaitingForStorageOrTheConnect()
    {
        await using var harness = new Harness();
        var alpha = harness.Listening("Alpha");
        var beta = harness.Listening("Beta");
        harness.SaveProfiles(alpha, beta);
        var window = harness.Open();
        var timings = new List<Timing>();
        try
        {
            SessionOpenTrace.Enabled = true;
            timings.Add(await MeasureAsync(window, alpha, "plain world, first open (cold)"));
            timings.Add(await MeasureAsync(window, beta, "plain world, second open (warm)"));
            timings.Add(await MeasureAsync(window, alpha, "plain world, re-open"));
        }
        finally { Print("PLAIN WORLD (no stored map, no scripts)", timings); }
        foreach (var timing in timings)
            Assert.True(timing.TabVisible < TabBudget, $"{timing.Label}: the tab took {timing.TabVisible:F0} ms to accept input.");
    }

    [AvaloniaFact]
    public async Task OpeningAWorldWithALargeMapLoadsTheGraphAfterTheTabIsUsable()
    {
        await using var harness = new Harness();
        var alpha = harness.Listening("Alpha");
        var beta = harness.Listening("Beta");
        harness.SaveProfiles(alpha, beta);
        foreach (var profile in new[] { alpha, beta })
        {
            harness.Maps.Save(profile.Host, profile.Port, BuildMap());
            var key = $"{profile.Host}:{profile.Port}:{profile.UseTls}";
            harness.Scripts.Upsert(key, new(Guid.NewGuid(), "Greeter", "mud.echo('hello');", true));
            harness.Scripts.Upsert(key, new(Guid.NewGuid(), "Watcher", "mud.on('line', function () { });", true));
        }
        var window = harness.Open();
        var timings = new List<Timing>();
        try
        {
            SessionOpenTrace.Enabled = true;
            timings.Add(await MeasureAsync(window, alpha, "large map, first open (cold)", Rooms));
            timings.Add(await MeasureAsync(window, beta, "large map, second open (warm)", Rooms));
            timings.Add(await MeasureAsync(window, alpha, "large map, re-open", Rooms));
        }
        finally { Print($"LARGE MAP ({Rooms} rooms, {Links} links, 2 scripts)", timings); }
        foreach (var timing in timings)
        {
            Assert.True(timing.TabVisible < TabBudget, $"{timing.Label}: the tab took {timing.TabVisible:F0} ms to accept input.");
            Assert.True(timing.MapReady < TabBudget, $"{timing.Label}: the map took {timing.MapReady:F0} ms to arrive.");
            // The graph is read off the UI thread, so it can only arrive once the tab is already there.
            Assert.True(timing.MapReady >= timing.TabVisible - 1,
                $"{timing.Label}: the map ({timing.MapReady:F0} ms) preceded the tab ({timing.TabVisible:F0} ms).");
        }
    }

    [AvaloniaFact]
    public async Task TheTabIsUsableWhileAnUnreachableWorldIsStillConnecting()
    {
        await using var harness = new Harness();
        harness.SaveProfiles();
        var window = harness.Open();
        // TEST-NET-1 is reserved and unrouteable: the connect stays pending until the session's timeout.
        var unreachable = new ConnectionProfile { Name = "Unreachable", Host = "192.0.2.1", Port = 4000 };
        var clock = Stopwatch.StartNew();
        var open = window.Sessions.OpenAsync(unreachable);
        var visible = await WaitForInputAsync(window, clock);
        Assert.True(visible < TabBudget, $"the tab took {visible:F0} ms to accept input while connecting.");
        var tab = window.Sessions.Active;
        Assert.True(tab.Controller.IsConnecting);
        Assert.False(tab.Controller.IsConnected);
        Console.WriteLine($"--- unreachable world: tab usable after {visible:F0} ms while the connect is still pending");
        // Closing a connecting tab cancels the attempt; nothing here may throw or hang.
        await window.Sessions.CloseAsync(tab);
        Dispatcher.UIThread.RunJobs();
        await open;
        Assert.DoesNotContain(tab, window.Sessions.Tabs);
        Assert.False(tab.Controller.IsConnected);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), "closing the tab did not cancel the pending connect.");
    }

    private static TextBox? Input(MainWindow window) => window.GetVisualDescendants().OfType<TextBox>()
        .FirstOrDefault(box => box.Name == "CommandInput" && box.IsEffectivelyVisible && box.Bounds.Width > 0);

    private static async Task<double> WaitForInputAsync(MainWindow window, Stopwatch clock)
    {
        while (clock.Elapsed < TimeSpan.FromSeconds(20))
        {
            Dispatcher.UIThread.RunJobs();
            if (Input(window) is not null) return clock.Elapsed.TotalMilliseconds;
            await Task.Delay(1);
        }
        Assert.Fail("The session tab never became usable.");
        return 0;
    }

    private static async Task<Timing> MeasureAsync(MainWindow window, ConnectionProfile profile, string label, int rooms = 0)
    {
        SessionOpenTrace.Reset();
        var clock = Stopwatch.StartNew();
        var open = window.Sessions.OpenAsync(profile);
        var visible = await WaitForInputAsync(window, clock);
        await open;
        Dispatcher.UIThread.RunJobs();
        var connected = clock.Elapsed.TotalMilliseconds;
        var controller = window.Sessions.Active.Controller;
        while (clock.Elapsed < TimeSpan.FromSeconds(20) && !controller.MapLoaded)
        {
            await Task.Delay(1);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.True(controller.MapLoaded);
        Assert.Equal(rooms, controller.Map.Snapshot.Rooms.Count);
        return new(label, visible, connected, clock.Elapsed.TotalMilliseconds, SessionOpenTrace.Breakdown(), SessionOpenTrace.Calls());
    }

    private static void Print(string heading, IReadOnlyList<Timing> timings)
    {
        Console.WriteLine("=== " + heading);
        foreach (var timing in timings)
        {
            Console.WriteLine($"--- {timing.Label}: tab usable {timing.TabVisible:F0} ms, connected {timing.Connected:F0} ms, map ready {timing.MapReady:F0} ms");
            foreach (var (phase, milliseconds) in timing.Breakdown.OrderByDescending(entry => entry.Milliseconds))
                Console.WriteLine($"      {phase,-24} {milliseconds,8:F1} ms" + (timing.Calls.TryGetValue(phase, out var calls) ? $"  x{calls}" : ""));
            foreach (var (phase, calls) in timing.Calls.Where(entry => timing.Breakdown.All(b => b.Phase != entry.Key)))
                Console.WriteLine($"      {phase,-24} {"",8}     x{calls}");
        }
    }

    private static MapSnapshot BuildMap()
    {
        var rooms = new List<MapRoom>(Rooms);
        for (var i = 0; i < Rooms; i++)
            rooms.Add(new("r" + i, "Room " + i, "A long corridor of worn stone, the " + i + "th of its kind, lit by a guttering lantern.",
                "Area " + i % 40, i % 50, i / 50, 0, false) { KnownExits = ["north", "east", "up"] });
        var directions = new[] { "north", "east", "up" };
        var links = new List<MapLink>(Links);
        for (var i = 0; i < Links; i++)
            links.Add(new("r" + i / 3, "r" + (i / 3 + 1) % Rooms, directions[i % 3], true));
        return new(rooms, links, [], null, MapTrackingState.Unknown, RoomDataSource.Text, 0);
    }
}
