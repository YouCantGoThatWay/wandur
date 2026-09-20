using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia.Headless.XUnit;
using Wandur.Core.Scripting;
using Wandur.Core.Settings;
using Wandur.Desktop;

namespace Wandur.Desktop.Tests;

public sealed class ScriptConnectionTests
{
    [AvaloniaFact]
    public async Task EnabledLibraryWaitsUntilAutoLoginHandsBackControl()
    {
        var factory = new RecordingScriptFactory();
        var vault = new MemoryPasswordVault();
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), "wandur-script-login-" + Guid.NewGuid(), "settings.json"));
        var scripts = new MemoryScriptLibraryStore();
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var profile = new ConnectionProfile { Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, Username = "tester", AutoLogin = true };
        var key = $"{profile.Host}:{profile.Port}:{profile.UseTls}";
        scripts.Upsert(key, Assert.Single(scripts.Load(key)) with { Enabled = true });
        await using var controller = new WorkspaceController(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), store, vault, new MemoryRoomMapStore(), factory, scripts);
        await controller.SaveWorldAsync(profile, "secret", true);
        profile = Assert.Single(controller.Settings.Profiles);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await controller.StartAsync(profile);
        using var server = await listener.AcceptTcpClientAsync(timeout.Token);
        using var reader = new StreamReader(server.GetStream(), Encoding.UTF8, leaveOpen: true);
        Assert.Null(factory.Runtime);
        await server.GetStream().WriteAsync(Encoding.UTF8.GetBytes("Username: "), timeout.Token);
        Assert.Equal("tester", await reader.ReadLineAsync(timeout.Token));
        Assert.Null(factory.Runtime);
        // A repeated username prompt ends the bounded handshake without sending another credential.
        await server.GetStream().WriteAsync(Encoding.UTF8.GetBytes("Username: "), timeout.Token);
        await ScriptSessionTests.WaitFor(() => controller.ScriptLibrary.Items[0].Runtime.IsRunning);
        Assert.Equal(1, controller.CommandsSent);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutputReceivedWhilePrivateIsNotReplayedWhenPrivacyEndsBeforeFlush(bool negotiatedInOnePacket)
    {
        var factory = new RecordingScriptFactory();
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), "wandur-script-privacy-" + Guid.NewGuid(), "settings.json"));
        await using var controller = new WorkspaceController(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), store, new MemoryPasswordVault(), new MemoryRoomMapStore(), factory, new MemoryScriptLibraryStore());
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await controller.StartAsync(new ConnectionProfile { Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port });
        using var server = await listener.AcceptTcpClientAsync(timeout.Token);
        await controller.ScriptLibrary.Items[0].Runtime.RunAsync();
        if (!negotiatedInOnePacket) controller.SetManualPrivate(true);
        // Hold the UI thread until socket output is buffered, so the timer cannot flush it first.
        var text = Encoding.UTF8.GetBytes("private server text\n");
        server.GetStream().Write(negotiatedInOnePacket ? new byte[] { 255, 251, 1 }.Concat(text).Concat(new byte[] { 255, 252, 1 }).ToArray() : text);
        var fields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var pending = (System.Collections.ICollection)typeof(WorkspaceController).GetField("_pending", fields)!.GetValue(controller)!;
        var gate = typeof(WorkspaceController).GetField("_pendingLock", fields)!.GetValue(controller)!;
        Assert.True(SpinWait.SpinUntil(() => { lock (gate) return pending.Count > 0; }, TimeSpan.FromSeconds(2)));
        controller.SetManualPrivate(false);
        controller.FlushOutput();
        // A public line acts as a barrier proving the previous queued work has been processed.
        server.GetStream().Write(Encoding.UTF8.GetBytes("public server text\n"));
        await ScriptSessionTests.WaitFor(() => factory.Runtime!.Events.Any(e => e.Text == "public server text"));
        Assert.DoesNotContain(factory.Runtime!.Events, e => e.Text.Contains("private server text"));
    }

    [AvaloniaFact]
    public async Task GmcpPrivateIntervalsAreDiscardedBeforePublicEventsReachTheWorker()
    {
        var factory = new RecordingScriptFactory();
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), "wandur-gmcp-privacy-" + Guid.NewGuid(), "settings.json"));
        await using var controller = new WorkspaceController(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), store, new MemoryPasswordVault(), new MemoryRoomMapStore(), factory, new MemoryScriptLibraryStore());
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await controller.StartAsync(new ConnectionProfile { Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port });
        using var server = await listener.AcceptTcpClientAsync(timeout.Token);
        await controller.ScriptLibrary.Items[0].Runtime.RunAsync();
        static byte[] Gmcp(string text) => new byte[] { 255, 250, 201 }.Concat(Encoding.UTF8.GetBytes(text)).Concat(new byte[] { 255, 240 }).ToArray();
        var packet = new byte[] { 255, 251, 201, 255, 251, 1 }.Concat(Gmcp("Char.Secret \"hidden\"")).Concat(new byte[] { 255, 252, 1 }).ToArray();
        server.GetStream().Write(packet);
        var fields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var pending = (System.Collections.ICollection)typeof(WorkspaceController).GetField("_pending", fields)!.GetValue(controller)!;
        var gate = typeof(WorkspaceController).GetField("_pendingLock", fields)!.GetValue(controller)!;
        Assert.True(SpinWait.SpinUntil(() => { lock (gate) return pending.Count > 0; }, TimeSpan.FromSeconds(2)));
        controller.FlushOutput();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        server.GetStream().Write(Gmcp("Room.Info {\"num\":1}"));
        await ScriptSessionTests.WaitFor(() => factory.Runtime!.Events.Any(e => e.Kind == "gmcp" && e.Text.StartsWith("Room.Info")));
        Assert.DoesNotContain(factory.Runtime!.Events, e => e.Text.Contains("hidden"));
    }

    [AvaloniaFact]
    public async Task MsdpVariablesReachScriptsAsEventsAndFeedTheStateCache()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wandur-msdp-script-" + Guid.NewGuid());
        var store = new SettingsStore(Path.Combine(directory, "settings.json"));
        await using var controller = new WorkspaceController(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), store,
            new MemoryPasswordVault(), new MemoryRoomMapStore(), new InlineScriptFactory(), new MemoryScriptLibraryStore());
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await controller.StartAsync(new ConnectionProfile { Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, Name = "MSDP test" });
        using var server = await listener.AcceptTcpClientAsync(timeout.Token);
        var script = controller.ScriptLibrary.Items[0].Runtime;
        script.Source = """
            mud.on(Events.Msdp, event => mud.echo(event.variable + "=" + JSON.stringify(event.value)));
            mud.trigger(/^report$/, () => mud.echo("hull:" + mud.state.get("msdp.SHIPHULL") + " missing:" + mud.state.get("msdp.NOPE")));
            """;
        await script.RunAsync();
        Assert.True(script.IsRunning, script.Error);
        static byte[] Subnegotiation(params byte[] payload) => [255, 250, 69, .. payload, 255, 240];
        await server.GetStream().WriteAsync(new byte[] { 255, 251, 69 }, timeout.Token);
        await server.GetStream().WriteAsync(Subnegotiation([1, .. Encoding.UTF8.GetBytes("SHIPHULL"), 2, .. Encoding.UTF8.GetBytes("1200")]), timeout.Token);
        await server.GetStream().WriteAsync(Subnegotiation([1, .. Encoding.UTF8.GetBytes("AFFECTS"), 2, 5,
            2, .. Encoding.UTF8.GetBytes("haste"), 2, .. Encoding.UTF8.GetBytes("sanctuary"), 6]), timeout.Token);
        await ScriptSessionTests.WaitFor(() => { controller.FlushOutput(); return script.Log.Contains("AFFECTS="); });
        Assert.Contains("SHIPHULL=\"1200\"", script.Log);
        Assert.Contains("AFFECTS=[\"haste\",\"sanctuary\"]", script.Log);
        await server.GetStream().WriteAsync(Encoding.UTF8.GetBytes("report\r\n"), timeout.Token);
        await ScriptSessionTests.WaitFor(() => { controller.FlushOutput(); return script.Log.Contains("hull:"); });
        Assert.Contains("hull:1200 missing:undefined", script.Log);
        // Private intervals never reach the cache or the events.
        controller.SetManualPrivate(true);
        await server.GetStream().WriteAsync(Subnegotiation([1, .. Encoding.UTF8.GetBytes("PASSWORDHINT"), 2, .. Encoding.UTF8.GetBytes("secret")]), timeout.Token);
        // Wait for the receiving thread to stamp the message before privacy ends, as the GMCP case does.
        var fields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var queued = (System.Collections.ICollection)typeof(WorkspaceController).GetField("_pendingDiagnostics", fields)!.GetValue(controller)!;
        var gate = typeof(WorkspaceController).GetField("_pendingLock", fields)!.GetValue(controller)!;
        Assert.True(SpinWait.SpinUntil(() => { lock (gate) return queued.Count > 0; }, TimeSpan.FromSeconds(3)));
        controller.SetManualPrivate(false);
        controller.FlushOutput();
        await server.GetStream().WriteAsync(Subnegotiation([1, .. Encoding.UTF8.GetBytes("SHIPHULL"), 2, .. Encoding.UTF8.GetBytes("900")]), timeout.Token);
        await ScriptSessionTests.WaitFor(() => { controller.FlushOutput(); return script.Log.Contains("SHIPHULL=\"900\""); });
        Assert.DoesNotContain("secret", script.Log);
        await controller.DisconnectAsync();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private static byte[] MsdpFrame(string variable, string value) => [255, 250, 69, 1, .. Encoding.UTF8.GetBytes(variable), 2, .. Encoding.UTF8.GetBytes(value), 255, 240];
    private static byte[] GmcpFrame(string text) => [255, 250, 201, .. Encoding.UTF8.GetBytes(text), 255, 240];

    [AvaloniaFact]
    public async Task MsdpReceivedDuringLoginSeedsAScriptThatStartsAfterward()
    {
        var vault = new MemoryPasswordVault();
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), "wandur-msdp-seed-" + Guid.NewGuid(), "settings.json"));
        var scripts = new MemoryScriptLibraryStore();
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var profile = new ConnectionProfile { Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, Username = "tester", AutoLogin = true };
        var key = $"{profile.Host}:{profile.Port}:{profile.UseTls}";
        scripts.Upsert(key, Assert.Single(scripts.Load(key)) with
        {
            Enabled = true,
            Source = """
                mud.on(Events.Msdp, event => mud.echo("event:" + event.variable));
                mud.echo("health:" + mud.state.get("msdp.HEALTH") + " hp:" + mud.state.get("gmcp.Char.Vitals.hp"));
                """
        });
        await using var controller = new WorkspaceController(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), store, vault, new MemoryRoomMapStore(), new InlineScriptFactory(), scripts);
        await controller.SaveWorldAsync(profile, "secret", true);
        profile = Assert.Single(controller.Settings.Profiles);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await controller.StartAsync(profile);
        using var server = await listener.AcceptTcpClientAsync(timeout.Token);
        using var reader = new StreamReader(server.GetStream(), Encoding.UTF8, leaveOpen: true);
        var script = controller.ScriptLibrary.Items[0].Runtime;
        await server.GetStream().WriteAsync(Encoding.UTF8.GetBytes("Username: "), timeout.Token);
        Assert.Equal("tester", await reader.ReadLineAsync(timeout.Token));
        Assert.False(script.IsRunning);
        // The world answers the REPORT while the login handshake still owns the session.
        await server.GetStream().WriteAsync(new byte[] { 255, 251, 69, 255, 251, 201 }, timeout.Token);
        await server.GetStream().WriteAsync(MsdpFrame("HEALTH", "100"), timeout.Token);
        await server.GetStream().WriteAsync(GmcpFrame("Char.Vitals {\"hp\":42}"), timeout.Token);
        await ScriptSessionTests.WaitFor(() => { controller.FlushOutput(); return controller.ScriptState.TryGetGmcp("Char.Vitals") is not null; });
        Assert.Equal("\"100\"", controller.ScriptState.TryGetMsdp("HEALTH"));
        Assert.False(script.IsRunning);
        // A repeated username prompt ends the handshake; the script starts seeded, so its first line already has
        // the values. The host also replays what it cached during login, and a replay that lands while the worker
        // boots is delivered after the load, so the script may see an event for a value the seed already held.
        await server.GetStream().WriteAsync(Encoding.UTF8.GetBytes("Username: "), timeout.Token);
        await ScriptSessionTests.WaitFor(() => { controller.FlushOutput(); return script.Log.Contains("health:"); });
        Assert.Contains("health:100 hp:42", script.Log);
        Assert.DoesNotContain("event:", script.Log.Split("health:")[0]);
        await controller.DisconnectAsync();
        Assert.True(controller.ScriptState.IsEmpty);
    }

    [AvaloniaFact]
    public async Task ReadingAnUnknownMsdpVariableAsksTheWorldToReportItAndARestartIsSeeded()
    {
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), "wandur-msdp-report-" + Guid.NewGuid(), "settings.json"));
        await using var controller = new WorkspaceController(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), store,
            new MemoryPasswordVault(), new MemoryRoomMapStore(), new InlineScriptFactory(), new MemoryScriptLibraryStore());
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await controller.StartAsync(new ConnectionProfile { Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, Name = "Report test" });
        using var server = await listener.AcceptTcpClientAsync(timeout.Token);
        var stream = server.GetStream();
        await stream.WriteAsync(new byte[] { 255, 251, 69 }, timeout.Token);
        await ScriptSessionTests.WaitFor(() => controller.ProtocolEvidence.Msdp == Wandur.Core.Protocol.TelnetOptionState.Enabled);
        var handshake = new Wandur.Core.Protocol.TelnetParser().Feed(new byte[] { 255, 251, 69 }).Reply;
        await stream.ReadExactlyAsync(new byte[handshake.Length], timeout.Token);
        var script = controller.ScriptLibrary.Items[0].Runtime;
        script.Source = """
            mud.on(Events.Msdp, event => mud.echo(event.variable + "=" + event.value));
            mud.echo("level:" + mud.state.get("msdp.LEVELCOMBAT"));
            """;
        await script.RunAsync();
        Assert.True(script.IsRunning, script.Error);
        Assert.Contains("level:undefined", script.Log);
        static byte[] Frame(string content) => [255, 250, 69, .. Encoding.UTF8.GetBytes(content), 255, 240];
        var expected = Frame("\u0001REPORT\u0002LEVELCOMBAT").Concat(Frame("\u0001SEND\u0002LEVELCOMBAT")).ToArray();
        var actual = new byte[expected.Length];
        await stream.ReadExactlyAsync(actual, timeout.Token);
        Assert.Equal(expected, actual);
        await stream.WriteAsync(MsdpFrame("LEVELCOMBAT", "12"), timeout.Token);
        await ScriptSessionTests.WaitFor(() => { controller.FlushOutput(); return script.Log.Contains("LEVELCOMBAT=12"); });
        Assert.Equal("\"12\"", controller.ScriptState.TryGetMsdp("LEVELCOMBAT"));
        // A restart is seeded from the host cache, so the first line already sees the value.
        script.Stop();
        await script.RunAsync();
        Assert.True(script.IsRunning, script.Error);
        Assert.Contains("level:12", script.Log);
        await controller.DisconnectAsync();
    }

    [AvaloniaFact]
    public async Task MsdpReceivedWhilePrivateNeverEntersTheHostCache()
    {
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), "wandur-msdp-private-" + Guid.NewGuid(), "settings.json"));
        await using var controller = new WorkspaceController(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), store,
            new MemoryPasswordVault(), new MemoryRoomMapStore(), new InlineScriptFactory(), new MemoryScriptLibraryStore());
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await controller.StartAsync(new ConnectionProfile { Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, Name = "Private test" });
        using var server = await listener.AcceptTcpClientAsync(timeout.Token);
        var stream = server.GetStream();
        await stream.WriteAsync(new byte[] { 255, 251, 69 }, timeout.Token);
        controller.SetManualPrivate(true);
        await stream.WriteAsync(MsdpFrame("SECRETVAR", "hidden"), timeout.Token);
        // Wait for the receiving thread to stamp the message before privacy ends, as the event tests do.
        var fields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var queued = (System.Collections.ICollection)typeof(WorkspaceController).GetField("_pendingDiagnostics", fields)!.GetValue(controller)!;
        var gate = typeof(WorkspaceController).GetField("_pendingLock", fields)!.GetValue(controller)!;
        Assert.True(SpinWait.SpinUntil(() => { lock (gate) return queued.Count > 0; }, TimeSpan.FromSeconds(3)));
        controller.SetManualPrivate(false);
        controller.FlushOutput();
        await stream.WriteAsync(MsdpFrame("HEALTH", "5"), timeout.Token);
        await ScriptSessionTests.WaitFor(() => { controller.FlushOutput(); return controller.ScriptState.TryGetMsdp("HEALTH") is not null; });
        Assert.Null(controller.ScriptState.TryGetMsdp("SECRETVAR"));
        Assert.DoesNotContain("hidden", controller.ScriptState.SeedJson());
        var script = controller.ScriptLibrary.Items[0].Runtime;
        script.Source = "mud.echo('secret:' + mud.state.get('msdp.SECRETVAR') + ' health:' + mud.state.get('msdp.HEALTH'));";
        await script.RunAsync();
        Assert.True(script.IsRunning, script.Error);
        Assert.Contains("secret:undefined health:5", script.Log);
        await controller.DisconnectAsync();
    }

    [AvaloniaFact]
    public async Task AliasesSendOnceTriggersUseCompletedServerLinesAndPasswordsBypassScripts()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wandur-script-connection-" + Guid.NewGuid());
        var store = new SettingsStore(Path.Combine(directory, "settings.json"));
        await using var controller = new WorkspaceController(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), store, new MemoryPasswordVault(), new MemoryRoomMapStore(), new InlineScriptFactory(), new MemoryScriptLibraryStore());
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await controller.StartAsync(new ConnectionProfile { Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, Name = "Script test" });
        using var server = await listener.AcceptTcpClientAsync(timeout.Token);
        using var reader = new StreamReader(server.GetStream(), Encoding.UTF8, leaveOpen: true);
        controller.ScriptLibrary.Items[0].Runtime.Source = """
            mud.alias(/^lh$/, () => mud.send("look"));
            mud.alias(/^look$/, () => mud.send("recursion-is-a-bug"));
            mud.alias(/^private-secret$/, () => mud.send("password-was-exposed"));
            mud.trigger(/^You see a (\w+)\.$/, m => mud.send("inspect " + m[1]));
            """;
        await controller.ScriptLibrary.Items[0].Runtime.RunAsync();
        Assert.True(controller.ScriptLibrary.Items[0].Runtime.IsRunning, controller.ScriptLibrary.Items[0].Runtime.Error);
        await controller.SendAsync("lh");
        Assert.Equal("look", await reader.ReadLineAsync(timeout.Token));
        Assert.Equal("lh", controller.History.Previous(""));
        Assert.Equal(1, controller.CommandsSent);
        await server.GetStream().WriteAsync(Encoding.UTF8.GetBytes("\u001b[32mYou see a "), timeout.Token);
        await ScriptSessionTests.WaitFor(() => controller.Terminal.PlainText.Contains("You see a"));
        Assert.Equal(0, server.Available);
        await server.GetStream().WriteAsync(Encoding.UTF8.GetBytes("droid.\u001b[0m\r\n"), timeout.Token);
        Assert.Equal("inspect droid", await reader.ReadLineAsync(timeout.Token));
        controller.SetManualPrivate(true);
        await controller.SendAsync("private-secret");
        Assert.Equal("private-secret", await reader.ReadLineAsync(timeout.Token));
        Assert.DoesNotContain("private-secret", controller.Terminal.PlainText);
        Assert.DoesNotContain("private-secret", controller.ScriptLibrary.Items[0].Runtime.Log);
        controller.SetManualPrivate(false);
        controller.ScriptLibrary.Items[0].Runtime.Stop();
        await controller.SendAsync("lh");
        Assert.Equal("lh", await reader.ReadLineAsync(timeout.Token));
        await controller.DisconnectAsync();
        Assert.False(controller.ScriptLibrary.Items[0].Runtime.IsRunning);
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    /// <summary>A generated pack panel of the kind the Legends of the Jedi trial showed empty: a skill level read once, redrawn on every MSDP event.</summary>
    private const string LevelsScript = """
        const p = mud.panel("skills", { title: "Skills", dock: "right" });
        function refresh() {
          const level = mud.state.get("msdp.LEVELCOMBAT");
          p.table("levels", { columns: ["Skill", "Level"], rows: [["Combat", level === undefined ? "" : String(level)]] });
        }
        mud.on(Events.Msdp, refresh);
        refresh();
        """;

    private static string? CombatLevel(WorkspaceController controller)
        => controller.ScriptLibrary.Panels.Panels.FirstOrDefault(panel => panel.Id == "skills")?.Widgets.FirstOrDefault(widget => widget.Id == "levels")?.Properties.Rows[0][1];

    private static byte[] Frame(string content) => [255, 250, 69, .. Encoding.UTF8.GetBytes(content), 255, 240];
    private static byte[] LevelReport => Frame("\u0001REPORT\u0002LEVELCOMBAT").Concat(Frame("\u0001SEND\u0002LEVELCOMBAT")).ToArray();

    private static async Task<byte[]> ReadUntilAsync(Stream stream, byte[] expected, CancellationToken token)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (bytes.Count < 4096 && !bytes.TakeLast(expected.Length).SequenceEqual(expected) && await stream.ReadAsync(one, token) == 1) bytes.Add(one[0]);
        return bytes.ToArray();
    }

    private static async Task<string> ReadRawLineAsync(Stream stream, CancellationToken token)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (await stream.ReadAsync(one, token) == 1 && one[0] != (byte)'\n') bytes.Add(one[0]);
        return Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\r');
    }

    /// <summary>Starts a session whose only script is an enabled pack script, negotiates MSDP and consumes the REPORT the script's read provokes.</summary>
    private static async Task<(WorkspaceController Controller, TcpClient Server, ConnectionProfile Profile)> StartPackSessionAsync(TcpListener listener, string tag, CancellationToken token)
    {
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), tag + Guid.NewGuid(), "settings.json"));
        var scripts = new MemoryScriptLibraryStore();
        var profile = new ConnectionProfile { Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, Name = "Pack test" };
        var key = $"{profile.Host}:{profile.Port}:{profile.UseTls}";
        scripts.Upsert(key, Assert.Single(scripts.Load(key)) with { Enabled = true, Source = LevelsScript, Pack = new ScriptPackInfo("skills", ScriptPackInfo.Generated, 1) });
        var controller = new WorkspaceController(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), store, new MemoryPasswordVault(), new MemoryRoomMapStore(), new InlineScriptFactory(), scripts);
        await controller.StartAsync(profile);
        var server = await listener.AcceptTcpClientAsync(token);
        var stream = server.GetStream();
        var script = controller.ScriptLibrary.Items[0];
        // A pack script runs as soon as the session can, before any login.
        await ScriptSessionTests.WaitFor(() => script.Runtime.IsRunning);
        Assert.True(script.IsPack);
        Assert.Equal("", CombatLevel(controller));
        await stream.WriteAsync(new byte[] { 255, 251, 69 }, token);
        await ScriptSessionTests.WaitFor(() => controller.ProtocolEvidence.Msdp == Wandur.Core.Protocol.TelnetOptionState.Enabled);
        var handshake = new Wandur.Core.Protocol.TelnetParser().Feed(new byte[] { 255, 251, 69 }).Reply;
        await stream.ReadExactlyAsync(new byte[handshake.Length], token);
        // The script read the level at load; once MSDP is on, the next output flush asks the world for it.
        await stream.WriteAsync(Encoding.UTF8.GetBytes("Welcome to the test world.\r\n"), token);
        var expected = LevelReport;
        var actual = new byte[expected.Length];
        await stream.ReadExactlyAsync(actual, token);
        Assert.Equal(expected, actual);
        return (controller, server, profile);
    }

    /// <summary>The Legends of the Jedi trial with a manual login: the world reports skill levels in the packet that
    /// also turns echo back on after the password, which the privacy stamp still covers, and never again since they
    /// do not change. The client asks again once play is public, as it does for the mapped variables.</summary>
    [AvaloniaFact]
    public async Task AValueReportedInThePacketThatEndsThePasswordPromptReachesARunningPackScript()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (controller, server, _) = await StartPackSessionAsync(listener, "wandur-pack-password-", timeout.Token);
        await using var owned = controller;
        using var connection = server;
        var stream = server.GetStream();
        var script = controller.ScriptLibrary.Items[0];
        await stream.WriteAsync((byte[])[255, 251, 1, .. Encoding.UTF8.GetBytes("Password: ")], timeout.Token);
        await ScriptSessionTests.WaitFor(() => { controller.FlushOutput(); return controller.IsPrivate; });
        Assert.True(await controller.SendAsync("secret"));
        // The line follows the client's answer to WILL ECHO.
        Assert.EndsWith("secret", await ReadRawLineAsync(stream, timeout.Token));
        await stream.WriteAsync((byte[])[255, 252, 1, .. Encoding.UTF8.GetBytes("Welcome back.\r\n"), .. MsdpFrame("LEVELCOMBAT", "50")], timeout.Token);
        await ScriptSessionTests.WaitFor(() => { controller.FlushOutput(); return !controller.IsPrivate && controller.Terminal.PlainText.Contains("Welcome back."); });
        // The value in that packet was stamped private; public play asks the world for the script's variables again
        // (after the client's answer to WONT ECHO).
        var received = await ReadUntilAsync(stream, LevelReport, timeout.Token);
        Assert.Equal(LevelReport, received.TakeLast(LevelReport.Length).ToArray());
        await stream.WriteAsync(MsdpFrame("LEVELCOMBAT", "50"), timeout.Token);
        await ScriptSessionTests.WaitFor(() => { controller.FlushOutput(); return CombatLevel(controller) == "50"; });
        Assert.True(script.Runtime.IsRunning, script.Runtime.Error);
        Assert.DoesNotContain("secret", controller.Terminal.PlainText);
        await controller.DisconnectAsync();
    }

    /// <summary>A value the host cache takes while the login handshake owns the session is delivered to a script that
    /// was already running when the handshake ends, as an ordinary event, with no further server traffic.</summary>
    [AvaloniaFact]
    public async Task ValuesCachedWhileTheLoginHandshakeOwnsTheSessionAreReplayedToARunningScriptWhenItEnds()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (controller, server, profile) = await StartPackSessionAsync(listener, "wandur-pack-replay-", timeout.Token);
        await using var owned = controller;
        using var connection = server;
        var stream = server.GetStream();
        var script = controller.ScriptLibrary.Items[0];
        // The handshake takes the session while the script runs (no public path sets it after connect; a reconnect
        // that outpaces the handshake is the closest), and the privacy toggle refreshes the script state.
        var fields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var attempt = typeof(WorkspaceController).GetNestedType("LoginAttempt", System.Reflection.BindingFlags.NonPublic)!;
        typeof(WorkspaceController).GetField("_login", fields)!.SetValue(controller, Activator.CreateInstance(attempt, profile, "secret"));
        controller.SetManualPrivate(false);
        Assert.True(script.Runtime.IsPaused);
        await stream.WriteAsync((byte[])[255, 251, 201, .. MsdpFrame("LEVELCOMBAT", "50"), .. GmcpFrame("Char.Vitals {\"hp\":42}")], timeout.Token);
        await ScriptSessionTests.WaitFor(() => { controller.FlushOutput(); return controller.ScriptState.TryGetGmcp("Char.Vitals") is not null; });
        Assert.Equal("\"50\"", controller.ScriptState.TryGetMsdp("LEVELCOMBAT"));
        Assert.Equal("", CombatLevel(controller));
        // Manual input ends the handshake; the cached values reach the script as ordinary events.
        Assert.True(await controller.SendAsync("look"));
        await ScriptSessionTests.WaitFor(() => { controller.FlushOutput(); return CombatLevel(controller) == "50"; });
        Assert.True(script.Runtime.IsRunning, script.Runtime.Error);
        await controller.DisconnectAsync();
    }
}

internal sealed class InlineScriptFactory : IScriptRuntimeFactory
{
    public IScriptRuntime Create() => new InlineScriptRuntime();
    private sealed class InlineScriptRuntime : IScriptRuntime
    {
        private JavaScriptEngine? _engine = new();
        public bool IsRunning => _engine?.IsRunning == true;
        public Task<ScriptResult> LoadAsync(string source, CancellationToken cancellationToken = default, bool restrictedSend = false) => Task.FromResult(_engine!.Load(source, restrictedSend));
        public Task<ScriptResult> DispatchAsync(ScriptEvent input, CancellationToken cancellationToken = default) => Task.FromResult(_engine!.Dispatch(input));
        public void Stop() => _engine = null;
        public ValueTask DisposeAsync() { Stop(); return ValueTask.CompletedTask; }
    }
}
