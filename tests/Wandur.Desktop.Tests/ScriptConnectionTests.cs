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
}

internal sealed class InlineScriptFactory : IScriptRuntimeFactory
{
    public IScriptRuntime Create() => new InlineScriptRuntime();
    private sealed class InlineScriptRuntime : IScriptRuntime
    {
        private JavaScriptEngine? _engine = new();
        public bool IsRunning => _engine?.IsRunning == true;
        public Task<ScriptResult> LoadAsync(string source, CancellationToken cancellationToken = default) => Task.FromResult(_engine!.Load(source));
        public Task<ScriptResult> DispatchAsync(ScriptEvent input, CancellationToken cancellationToken = default) => Task.FromResult(_engine!.Dispatch(input));
        public void Stop() => _engine = null;
        public ValueTask DisposeAsync() { Stop(); return ValueTask.CompletedTask; }
    }
}
