using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Wandur.Core.Classification;
using Wandur.Core.Mapping;
using Wandur.Core.Settings;
using Wandur.Desktop;

namespace Wandur.Desktop.Tests;

public sealed class RoomInferenceSchedulingTests
{
    private sealed class FakeClassifier : IRoomEnvironmentClassifier
    {
        private readonly ManualResetEventSlim _gate = new(true);
        public readonly List<string> Seen = [];
        public string? ThrowsFor { get; init; }
        public string ModelVersion => "t"; public double DefaultThreshold => 0.8;
        public int Count { get { lock (Seen) return Seen.Count; } }
        public string[] Names { get { lock (Seen) return [.. Seen]; } }
        public static FakeClassifier Blocked() { var classifier = new FakeClassifier(); classifier._gate.Reset(); return classifier; }
        public void Release() => _gate.Set();
        public RoomEnvironmentPrediction? Classify(string name, string description, double threshold)
        {
            lock (Seen) Seen.Add(name);
            _gate.Wait(TimeSpan.FromSeconds(5));
            if (name == ThrowsFor) throw new ArgumentException("bad room");
            return name.Contains("Pine") ? new("forest", 0.95, "t") : null;
        }
    }

    /// <summary>Runs posted inference results to quiescence: no worker running, and none restarted by a rescan.</summary>
    private static async Task Pump(WorkspaceController controller)
    {
        var stable = 0;
        for (var i = 0; i < 80 && stable < 3; i++)
        {
            if (controller.InferenceWorkerForTests is { IsCompleted: false } worker)
            {
                await Task.WhenAny(worker, Task.Delay(50));
                stable = 0;
            }
            Dispatcher.UIThread.RunJobs();
            stable = controller.InferenceWorkerForTests is null or { IsCompleted: true } ? stable + 1 : 0;
        }
    }

    /// <summary>Writes a minimal, hash-verified room-classifier package directly under a models root, bypassing the installer.</summary>
    private static void WriteInstalledPackage(string modelsRoot, string version = "9.9.9")
    {
        var dir = Path.Combine(modelsRoot, version);
        Directory.CreateDirectory(dir);
        var files = new Dictionary<string, string>
        {
            ["encoder.onnx"] = "not-a-real-model",
            ["tokenizer.json"] = JsonSerializer.Serialize(new { model = new { type = "WordPiece", vocab = new Dictionary<string, int> { ["[PAD]"] = 0, ["[UNK]"] = 1 } } }),
            ["head.json"] = JsonSerializer.Serialize(new { classes = new[] { "cave" }, coef = new[] { new[] { 0.1 } }, intercept = new[] { 0.1 } }),
            ["preprocessing_spec.json"] = JsonSerializer.Serialize(new { max_word_pieces = 256, threshold = 0.8 }),
            ["taxonomy.json"] = JsonSerializer.Serialize(new { version = "1.0.0" }),
        };
        foreach (var (name, content) in files) File.WriteAllText(Path.Combine(dir, name), content);
        var manifest = new { version, files = files.ToDictionary(f => f.Key, f => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(f.Value)))) };
        File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(manifest));
    }

    private static WorkspaceController Controller(IRoomEnvironmentClassifier classifier)
    {
        var directory = Path.Combine(Path.GetTempPath(), "wandur-infer-" + Guid.NewGuid());
        return new WorkspaceController(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(),
            new SettingsStore(Path.Combine(directory, "settings.json")), new MemoryPasswordVault(), new MemoryRoomMapStore(),
            new RecordingScriptFactory(), new MemoryScriptLibraryStore(), classification: RoomClassificationService.ForTesting(classifier));
    }

    [AvaloniaFact]
    public async Task ObservedRoomsWithoutTerrainAreClassifiedAndAppliedOnTheUiThread()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wandur-infer-" + Guid.NewGuid());
        var classifier = new FakeClassifier();
        await using var controller = new WorkspaceController(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(),
            new SettingsStore(Path.Combine(directory, "settings.json")), new MemoryPasswordVault(), new MemoryRoomMapStore(),
            new RecordingScriptFactory(), new MemoryScriptLibraryStore(), classification: RoomClassificationService.ForTesting(classifier));
        controller.Map.Observe(new("1", "Pine Trail", "Tall pines crowd the narrow trail.", new Dictionary<string, string?>(), Source: RoomDataSource.Gmcp));
        controller.Map.Observe(new("2", "Harbor", "Ships creak at their moorings.", new Dictionary<string, string?>(), Source: RoomDataSource.Gmcp) { Environment = "water" }, "east");
        controller.Map.Observe(new("3", "Dunes", "Endless dunes roll away.", new Dictionary<string, string?>(), Source: RoomDataSource.Gmcp), "east");
        controller.ScheduleInference();
        await Pump(controller);
        var rooms = controller.Map.Snapshot.Rooms.ToDictionary(r => r.Id);
        Assert.Equal("forest", rooms["s:1"].InferredEnvironment); Assert.Equal(0.95, rooms["s:1"].InferredConfidence);
        Assert.Null(rooms["s:2"].InferredEnvironment); Assert.DoesNotContain("Harbor", classifier.Seen); // server terrain never classified
        Assert.Null(rooms["s:3"].InferredEnvironment); Assert.NotNull(rooms["s:3"].InferredKey); // abstained, recorded
        Assert.All(rooms.Values, r => Assert.False(r.IsManuallyEdited));
        var seen = classifier.Seen.Count;
        controller.ScheduleInference(); await Pump(controller);
        Assert.Equal(seen, classifier.Seen.Count); // nothing stale, nothing re-run
    }

    [AvaloniaFact]
    public async Task DisabledSettingSchedulesNothing()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wandur-infer-" + Guid.NewGuid());
        var classifier = new FakeClassifier();
        await using var controller = new WorkspaceController(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(),
            new SettingsStore(Path.Combine(directory, "settings.json")), new MemoryPasswordVault(), new MemoryRoomMapStore(),
            new RecordingScriptFactory(), new MemoryScriptLibraryStore(), classification: RoomClassificationService.ForTesting(classifier));
        controller.SaveSettings(controller.Settings with { ClassifyRoomsLocally = false });
        controller.Map.Observe(new("1", "Pine Trail", "Tall pines crowd the narrow trail.", new Dictionary<string, string?>(), Source: RoomDataSource.Gmcp));
        controller.ScheduleInference(); await Pump(controller);
        Assert.Empty(classifier.Seen);
        Assert.Null(controller.Map.Snapshot.Rooms.Single().InferredEnvironment);
    }

    [AvaloniaFact]
    public async Task AFailingRoomNeitherFaultsTheWorkerNorBlocksLaterAttempts()
    {
        var classifier = new FakeClassifier { ThrowsFor = "Harbor" };
        await using var controller = Controller(classifier);
        controller.Map.Observe(new("1", "Pine Trail", "Tall pines crowd the narrow trail.", new Dictionary<string, string?>(), Source: RoomDataSource.Gmcp));
        controller.Map.Observe(new("2", "Harbor", "Ships creak at their moorings.", new Dictionary<string, string?>(), Source: RoomDataSource.Gmcp), "east");
        controller.Map.Observe(new("3", "Dunes", "Endless dunes roll away.", new Dictionary<string, string?>(), Source: RoomDataSource.Gmcp), "east");
        controller.ScheduleInference();
        await Pump(controller);
        Assert.True(controller.InferenceWorkerForTests is null or { IsFaulted: false });
        var rooms = controller.Map.Snapshot.Rooms.ToDictionary(r => r.Id);
        Assert.Equal("forest", rooms["s:1"].InferredEnvironment);
        Assert.NotNull(rooms["s:3"].InferredKey);
        Assert.Null(rooms["s:2"].InferredKey); // the throwing room stayed stale
        var before = classifier.Names.Count(name => name == "Harbor");
        controller.ScheduleInference();
        await Pump(controller);
        Assert.True(classifier.Names.Count(name => name == "Harbor") > before); // re-queued, not stuck in the queued set
    }

    [AvaloniaFact]
    public async Task ClassifierCreationFailureNeverFaultsTheWorker()
    {
        var data = Path.Combine(Path.GetTempPath(), "wandur-infer-classifier-fail-" + Guid.NewGuid());
        WriteInstalledPackage(Path.Combine(data, "models", "room-classifier"));
        using var service = new RoomClassificationService(data, new HttpClient(), classifierFactory: _ => throw new DllNotFoundException("boom"));
        Assert.Equal(RoomClassificationState.Ready, service.Status.State);
        await using var controller = new WorkspaceController(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(),
            new SettingsStore(Path.Combine(data, "settings.json")), new MemoryPasswordVault(), new MemoryRoomMapStore(),
            new RecordingScriptFactory(), new MemoryScriptLibraryStore(), classification: service);
        controller.Map.Observe(new("1", "Pine Trail", "Tall pines crowd the narrow trail.", new Dictionary<string, string?>(), Source: RoomDataSource.Gmcp));
        controller.ScheduleInference();
        await Pump(controller);
        Assert.True(controller.InferenceWorkerForTests is null or { IsCompleted: true, IsFaulted: false }); // never faults
        Assert.Null(controller.Map.Snapshot.Rooms.Single().InferredEnvironment);
        Assert.Null(controller.Map.Snapshot.Rooms.Single().InferredKey);
        Assert.Equal(RoomClassificationState.Failed, service.Status.State);
    }

    [AvaloniaFact]
    public async Task SchedulingAfterCancellationStartsAFreshWorker()
    {
        var classifier = FakeClassifier.Blocked();
        await using var controller = Controller(classifier);
        controller.Map.Observe(new("1", "Pine Trail", "Tall pines crowd the narrow trail.", new Dictionary<string, string?>(), Source: RoomDataSource.Gmcp));
        controller.ScheduleInference();
        await ScriptSessionTests.WaitFor(() => classifier.Count > 0); // the worker is inside Classify
        controller.CancelInferenceForTests();
        controller.ScheduleInference();
        classifier.Release();
        await Pump(controller);
        Assert.Equal("forest", controller.Map.Snapshot.Rooms.Single().InferredEnvironment);
    }

    [AvaloniaFact]
    public async Task TheCurrentRoomJumpsAheadOfRoomsQueuedEarlier()
    {
        var classifier = FakeClassifier.Blocked();
        await using var controller = Controller(classifier);
        controller.Map.Observe(new("1", "Room One", "A room.", new Dictionary<string, string?>(), Source: RoomDataSource.Gmcp));
        controller.Map.Observe(new("2", "Room Two", "A room.", new Dictionary<string, string?>(), Source: RoomDataSource.Gmcp), "east");
        controller.Map.Observe(new("3", "Room Three", "A room.", new Dictionary<string, string?>(), Source: RoomDataSource.Gmcp), "east");
        controller.ScheduleInference();
        await ScriptSessionTests.WaitFor(() => classifier.Count > 0); // room three (current) taken first, worker parked
        controller.Map.Observe(new("4", "Room Four", "A room.", new Dictionary<string, string?>(), Source: RoomDataSource.Gmcp), "east");
        controller.ScheduleInference();
        classifier.Release();
        await Pump(controller);
        var names = classifier.Names;
        Assert.Equal("Room Three", names[0]);
        Assert.Equal("Room Four", names[1]); // the new current room overtook rooms one and two
        Assert.All(controller.Map.Snapshot.Rooms, room => Assert.NotNull(room.InferredKey));
    }

    [AvaloniaFact]
    public async Task ThePerRoomFastPathConsidersOnlyThatRoom()
    {
        var classifier = new FakeClassifier();
        await using var controller = Controller(classifier);
        controller.Map.Observe(new("1", "Pine Trail", "Tall pines crowd the narrow trail.", new Dictionary<string, string?>(), Source: RoomDataSource.Gmcp));
        controller.Map.Observe(new("2", "Harbor", "Ships creak at their moorings.", new Dictionary<string, string?>(), Source: RoomDataSource.Gmcp) { Environment = "water" }, "east");
        controller.ScheduleInference("s:2");
        await Pump(controller);
        Assert.Empty(classifier.Names); // server terrain queues nothing, and no full scan ran either
        Assert.Null(controller.InferenceWorkerForTests);
        controller.ScheduleInference("s:1");
        await Pump(controller);
        Assert.Equal(["Pine Trail"], classifier.Names);
        Assert.Equal("forest", controller.Map.Snapshot.Rooms.Single(room => room.Id == "s:1").InferredEnvironment);
    }

    [AvaloniaFact]
    public async Task TurningClassificationOffCancelsWorkInFlight()
    {
        var classifier = FakeClassifier.Blocked();
        await using var controller = Controller(classifier);
        controller.Map.Observe(new("1", "Pine Trail", "Tall pines crowd the narrow trail.", new Dictionary<string, string?>(), Source: RoomDataSource.Gmcp));
        controller.ScheduleInference();
        await ScriptSessionTests.WaitFor(() => classifier.Count > 0); // the worker is inside Classify
        controller.SaveSettings(controller.Settings with { ClassifyRoomsLocally = false });
        classifier.Release();
        await Pump(controller);
        var room = controller.Map.Snapshot.Rooms.Single();
        Assert.Null(room.InferredEnvironment);
        Assert.Null(room.InferredKey); // the in-flight result was dropped, not applied
    }

    [AvaloniaFact]
    public async Task InferenceNeverInterruptsAMapWalk()
    {
        var classifier = FakeClassifier.Blocked();
        await using var world = await InferenceWorld.Open(classifier);
        // Connecting schedules the rooms loaded from the map store, so the worker is already inside Classify.
        await ScriptSessionTests.WaitFor(() => classifier.Count > 0);
        var walk = world.Controller.StartMapWalkAsync(world.Route);
        Assert.Equal("north", await world.Command());
        Assert.True(world.Controller.IsMapWalking);
        classifier.Release();
        await Pump(world.Controller);
        Assert.True(world.Controller.IsMapWalking); // a revision bump mid-walk would have stopped it
        Assert.All(world.Controller.Map.Snapshot.Rooms, room => Assert.Null(room.InferredKey));
        await world.Room("2");
        Assert.Equal("east", await world.Command());
        await world.Room("3");
        await walk;
        Assert.False(world.Controller.IsMapWalking);
        await Pump(world.Controller);
        // No further observation and no schedule call: ending the walk alone has to catch the map up.
        Assert.All(world.Controller.Map.Snapshot.Rooms, room => Assert.NotNull(room.InferredKey));
    }

    private sealed class InferenceWorld(TcpListener listener, TcpClient socket, WorkspaceController controller, MapRoute route) : IAsyncDisposable
    {
        public WorkspaceController Controller { get; } = controller;
        public MapRoute Route { get; } = route;

        public static async Task<InferenceWorld> Open(IRoomEnvironmentClassifier classifier)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var profile = new ConnectionProfile { Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port };
            var maps = new MemoryRoomMapStore();
            MapRoom[] rooms = [new("s:1", "Room 1", "A room.", null, 0, 0, 0, false, "1"), new("s:2", "Room 2", "A room.", null, 0, 1, 0, false, "2"), new("s:3", "Room 3", "A room.", null, 1, 1, 0, false, "3")];
            MapLink[] links = [new("s:1", "s:2", "north", true), new("s:2", "s:3", "east", true)];
            maps.Save(profile.Host, profile.Port, new(rooms, links, [], null, MapTrackingState.Unknown, RoomDataSource.Gmcp, 0));
            var controller = new WorkspaceController(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(),
                new SettingsStore(Path.Combine(Path.GetTempPath(), "wandur-infer-walk-" + Guid.NewGuid(), "settings.json")),
                new MemoryPasswordVault(), maps, new RecordingScriptFactory(), new MemoryScriptLibraryStore(),
                classification: RoomClassificationService.ForTesting(classifier));
            await controller.StartAsync(profile);
            var socket = await listener.AcceptTcpClientAsync();
            var world = new InferenceWorld(listener, socket, controller, new(links, 2));
            await world.Output(new byte[] { 255, 251, 201 }.Concat(RoomBytes("1")).ToArray());
            await ScriptSessionTests.WaitFor(() => controller.Map.Snapshot.State == MapTrackingState.Confirmed);
            // Discard negotiation responses before checking movement commands.
            while (socket.Available > 0) await socket.GetStream().ReadExactlyAsync(new byte[socket.Available]);
            return world;
        }

        private static byte[] RoomBytes(string id) => new byte[] { 255, 250, 201 }
            .Concat(Encoding.UTF8.GetBytes($"Room.Info {{\"num\":\"{id}\",\"name\":\"Room {id}\"}}")).Concat(new byte[] { 255, 240 }).ToArray();

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
                if (b[0] == 10) return Encoding.UTF8.GetString([.. bytes]).TrimEnd('\r');
                bytes.Add(b[0]);
            }
        }

        public async ValueTask DisposeAsync() { await Controller.DisposeAsync(); socket.Dispose(); listener.Dispose(); }
    }
}
