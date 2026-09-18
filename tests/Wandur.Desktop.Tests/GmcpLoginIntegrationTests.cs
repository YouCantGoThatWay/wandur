using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Wandur.Core.Settings;
using Wandur.Desktop;

namespace Wandur.Desktop.Tests;

public sealed class GmcpLoginIntegrationTests
{
    private static byte[] Message(string text) => [255, 250, 201, .. Encoding.UTF8.GetBytes(text), 255, 240];
    private static WorkspaceController Controller() => new(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(),
        new SettingsStore(Path.Combine(Path.GetTempPath(), "wandur-gmcp-login-" + Guid.NewGuid(), "settings.json")),
        new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());

    private static async Task<string> ReadCredentials(NetworkStream stream, CancellationToken token)
    {
        var bytes = new List<byte>(); var buffer = new byte[4096];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, token); Assert.True(count > 0);
            bytes.AddRange(buffer.Take(count));
            var text = Encoding.UTF8.GetString(bytes.ToArray());
            var start = text.IndexOf("Char.Login.Credentials ", StringComparison.Ordinal);
            if (start < 0) continue;
            start += "Char.Login.Credentials ".Length;
            var end = text.IndexOf('\ufffd', start);
            if (end >= 0) return text[start..end];
        }
    }

    private static async Task Pump(WorkspaceController controller)
    {
        for (var i = 0; i < 10; i++) { await Task.Delay(15); Dispatcher.UIThread.RunJobs(); controller.FlushOutput(); }
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CredentialsAreSentOnceAndResultsNeverLeakOrRetry(bool success)
    {
        await using var controller = Controller();
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var profile = new ConnectionProfile { Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, Username = "fixture-player", AutoLogin = true };
        const string password = "fixture-only-\"quoted\"-秘密";
        await controller.SaveWorldAsync(profile, password, true); profile = Assert.Single(controller.Profiles);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await controller.StartAsync(profile);
        using var socket = await listener.AcceptTcpClientAsync(timeout.Token); var stream = socket.GetStream();
        await stream.WriteAsync(new byte[] { 255, 251, 201 }.Concat(Message("Char.Login.Default {\"type\":[\"password-credentials\"]}")).ToArray(), timeout.Token);
        using var credentials = JsonDocument.Parse(await ReadCredentials(stream, timeout.Token));
        Assert.Equal(profile.Username, credentials.RootElement.GetProperty("account").GetString());
        Assert.Equal(password, credentials.RootElement.GetProperty("password").GetString());
        await stream.WriteAsync(Message("Char.Vitals {\"hp\":42}"), timeout.Token);
        await Pump(controller);
        Assert.Contains(controller.Diagnostics.Entries, e => e.Content?.Name == "Char.Vitals" && e.Content.Body.Contains("42"));
        await stream.WriteAsync(Message("Char.Login.Result " + JsonSerializer.Serialize(new { success, message = "fixture-result-private: " + password })), timeout.Token);
        await Pump(controller);
        await stream.WriteAsync(Message("Char.Login.Default {\"type\":[\"password-credentials\"]}")
            .Concat(Message("Char.Login.Token {\"token\":\"fixture-token-private\"}"))
            .Concat(Encoding.UTF8.GetBytes("Name: ")).ToArray(), timeout.Token);
        await Pump(controller);
        Assert.Equal(0, socket.Available);
        Assert.Equal("draft", controller.History.Previous("draft"));
        Assert.DoesNotContain(password, controller.Terminal.PlainText);
        Assert.Contains(controller.Diagnostics.Entries, e => e.Content?.Name == "Char.Login.Default" && e.Content.Body.Contains("password-credentials"));
        Assert.Contains(controller.Diagnostics.Entries, e => e.Content?.Name == "Char.Login.Result" && e.Content.Body.Contains("fixture-result-private"));
        Assert.DoesNotContain(controller.Diagnostics.Entries, e => e.Content?.Body.Contains("fixture-token-private") == true);
        var result = Assert.Single(controller.Diagnostics.Entries, e => e.Content?.Name == "Char.Login.Result");
        using var response = JsonDocument.Parse(result.Content!.Body);
        Assert.Equal("fixture-result-private: [redacted]", response.RootElement.GetProperty("message").GetString());
        Assert.DoesNotContain("Char.Login", controller.Diagnostics.SchemaDetail);
        if (!success) Assert.NotNull(controller.Notice);
    }

    [AvaloniaTheory]
    [InlineData(false, "password-credentials")]
    [InlineData(true, "oauth")]
    public async Task DisabledOrUnsupportedFlowDeclinesAndKeepsTextFallback(bool autoLogin, string method)
    {
        await using var controller = Controller();
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var profile = new ConnectionProfile { Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, Username = "fixture-player", AutoLogin = autoLogin };
        await controller.SaveWorldAsync(profile, "fixture-password", true); profile = Assert.Single(controller.Profiles);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await controller.StartAsync(profile);
        using var socket = await listener.AcceptTcpClientAsync(timeout.Token); var stream = socket.GetStream();
        await stream.WriteAsync(new byte[] { 255, 251, 201 }.Concat(Message("Char.Login.Default {\"type\":[\"" + method + "\"]}")).ToArray(), timeout.Token);
        Assert.Equal("{}", await ReadCredentials(stream, timeout.Token));
        await stream.WriteAsync(Encoding.UTF8.GetBytes("Name: "), timeout.Token);
        if (autoLogin)
        {
            using var reader = new StreamReader(stream, leaveOpen: true);
            Assert.Equal(profile.Username, await reader.ReadLineAsync(timeout.Token));
            await stream.WriteAsync(Encoding.UTF8.GetBytes("\r\nPassword: "), timeout.Token);
            Assert.Equal("fixture-password", await reader.ReadLineAsync(timeout.Token));
        }
        else { await Pump(controller); Assert.Equal(0, socket.Available); }
    }

    [AvaloniaFact]
    public async Task LateOfferCannotResendCredentialsAfterTextLoginStarts()
    {
        await using var controller = Controller();
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var profile = new ConnectionProfile { Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, Username = "fixture-player", AutoLogin = true };
        await controller.SaveWorldAsync(profile, "fixture-password", true); profile = Assert.Single(controller.Profiles);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await controller.StartAsync(profile);
        using var socket = await listener.AcceptTcpClientAsync(timeout.Token); var stream = socket.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes("Name: "), timeout.Token);
        using var reader = new StreamReader(stream, leaveOpen: true);
        Assert.Equal(profile.Username, await reader.ReadLineAsync(timeout.Token));
        await stream.WriteAsync(new byte[] { 255, 251, 201 }.Concat(Message("Char.Login.Default {\"type\":[\"password-credentials\"]}")).ToArray(), timeout.Token);
        Assert.Equal("{}", await ReadCredentials(stream, timeout.Token));
        await stream.WriteAsync(Encoding.UTF8.GetBytes("\r\nPassword: "), timeout.Token);
        Assert.Equal("fixture-password", await reader.ReadLineAsync(timeout.Token));
    }
}
