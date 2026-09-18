using System.Net;
using System.Net.Sockets;
using System.Text;
using Wandur.Core.Sessions;
using Wandur.Core.Settings;
using Wandur.Core.Terminal;

namespace Wandur.Core.Tests;

public class SessionTests
{
    [Fact]
    public async Task SubscriptionRefreshRequiresNegotiatedPublicConnectionAndSendsOnlyFixedCapabilities()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        await using var session = new TelnetSession(new() { Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port });
        Assert.False(await session.RefreshProtocolSubscriptionsAsync(cancellationToken: timeout.Token));
        await session.ConnectAsync(timeout.Token);
        using var server = await listener.AcceptTcpClientAsync(timeout.Token);
        Assert.False(await session.RefreshProtocolSubscriptionsAsync(cancellationToken: timeout.Token));
        var negotiated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ProtocolStateChanged += _ => negotiated.TrySetResult();
        var stream = server.GetStream();
        await stream.WriteAsync(new byte[] { 255, 251, 201 }, timeout.Token);
        await negotiated.Task.WaitAsync(timeout.Token);
        var handshake = new Wandur.Core.Protocol.TelnetParser().Feed(new byte[] { 255, 251, 201 }).Reply;
        await stream.ReadExactlyAsync(new byte[handshake.Length], timeout.Token);
        session.SetLocalPrivateInput(true);
        Assert.False(await session.RefreshProtocolSubscriptionsAsync(cancellationToken: timeout.Token));
        Assert.Equal(0, server.Available);
        session.SetLocalPrivateInput(false);
        Assert.True(await session.RefreshProtocolSubscriptionsAsync(cancellationToken: timeout.Token));
        var actual = new List<byte>();
        var one = new byte[1];
        while (actual.Count < 2 || actual[^2] != 255 || actual[^1] != 240)
        {
            await stream.ReadExactlyAsync(one, timeout.Token);
            actual.Add(one[0]);
        }
        Assert.Equal(new byte[] { 255, 250, 201 }, actual.Take(3));
        var request = Encoding.UTF8.GetString(actual.Skip(3).Take(actual.Count - 5).ToArray());
        Assert.StartsWith("Core.Supports.Set [", request);
        Assert.Contains("\"Char.Maxstats 1\"", request);
        Assert.DoesNotContain("Credentials", request);
        await stream.WriteAsync(new byte[] { 255, 251, 1 }, timeout.Token);
        await stream.ReadExactlyAsync(new byte[3], timeout.Token);
        Assert.False(await session.RefreshProtocolSubscriptionsAsync(cancellationToken: timeout.Token));
        Assert.Equal(0, server.Available);
    }

    [Fact]
    public async Task NativeMsdpRefreshReportsAndRequestsMappedVariablesWithoutNegotiatingGmcp()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using var session = new TelnetSession(new() { Host = "127.0.0.1", Port = port });
        await session.ConnectAsync(timeout.Token);
        using var server = await listener.AcceptTcpClientAsync(timeout.Token);
        var negotiated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ProtocolStateChanged += _ => negotiated.TrySetResult();
        var stream = server.GetStream();
        await stream.WriteAsync(new byte[] { 255, 251, 69 }, timeout.Token);
        await negotiated.Task.WaitAsync(timeout.Token);
        var handshake = new Wandur.Core.Protocol.TelnetParser().Feed(new byte[] { 255, 251, 69 }).Reply;
        await stream.ReadExactlyAsync(new byte[handshake.Length], timeout.Token);
        var mapping = new Wandur.Models.WorldMapping { WorldId = "test", Endpoint = new("127.0.0.1", port),
            SchemaFingerprint = new('a', 64), GeneratedAt = DateTimeOffset.UtcNow,
            Bindings = [new() { Source = new("MSDP", "MSDP", "/HEALTH"), Target = new("character", "resource", "health", "current") },
                new() { Source = new("MSDP", "MSDP", "/HEALTH_MAX"), Target = new("character", "resource", "health", "maximum") }] };
        Assert.True(await session.RefreshProtocolSubscriptionsAsync(mapping, timeout.Token));
        static byte[] Frame(string content) => [255, 250, 69, .. Encoding.UTF8.GetBytes(content), 255, 240];
        var expected = Frame("\u0001REPORT\u0002HEALTH\u0002HEALTH_MAX").Concat(Frame("\u0001SEND\u0002HEALTH\u0002HEALTH_MAX")).ToArray();
        var actual = new byte[expected.Length];
        await stream.ReadExactlyAsync(actual, timeout.Token);
        Assert.Equal(expected, actual);
        Assert.Equal(0, server.Available);
        Assert.False(await session.RefreshProtocolSubscriptionsAsync(mapping with { Endpoint = new("other.example", port) }, timeout.Token));
        Assert.Equal(0, server.Available);
    }

    [Theory]
    [InlineData("private text\n")]
    [InlineData("private café 🧙\n")]
    public async Task CompletePrivateTextDoesNotMarkTheNextPublicReadPrivate(string privateText)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var session = new TelnetSession(new() { Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port });
        var received = System.Threading.Channels.Channel.CreateUnbounded<ReceivedSessionText>();
        session.TextReceived += text => received.Writer.TryWrite(text);
        await session.ConnectAsync(timeout.Token);
        using var server = await listener.AcceptTcpClientAsync(timeout.Token);
        var stream = server.GetStream();
        await stream.WriteAsync(new byte[] { 255, 251, 1 }.Concat(Encoding.UTF8.GetBytes(privateText)).Concat(new byte[] { 255, 252, 1 }).ToArray(), timeout.Token);
        var privateResult = await received.Reader.ReadAsync(timeout.Token);
        Assert.True(privateResult.MayContainPrivateText);
        Assert.Equal(privateText, privateResult.Text);
        await stream.WriteAsync(Encoding.UTF8.GetBytes("public line\n"), timeout.Token);
        var publicResult = await received.Reader.ReadAsync(timeout.Token);
        Assert.False(publicResult.MayContainPrivateText);
        Assert.Equal("public line\n", publicResult.Text);
    }

    [Fact]
    public async Task TextMetadataPreservesPrivacyAcrossCoalescedNegotiationAndSplitUnicode()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var session = new TelnetSession(new() { Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port });
        var firstText = new TaskCompletionSource<ReceivedSessionText>(TaskCreationOptions.RunContinuationsAsynchronously);
        var privateEnd = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unicodeText = new TaskCompletionSource<ReceivedSessionText>(TaskCreationOptions.RunContinuationsAsynchronously);
        var publicText = new TaskCompletionSource<ReceivedSessionText>(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new StringBuilder();
        session.Output += text => { lock (output) output.Append(text); };
        session.TextReceived += received =>
        {
            if (received.Text.Contains("secret", StringComparison.Ordinal)) firstText.TrySetResult(received);
            if (received.Text.Contains("🧙", StringComparison.Ordinal)) unicodeText.TrySetResult(received);
            if (received.Text.Contains("public", StringComparison.Ordinal)) publicText.TrySetResult(received);
        };
        session.PrivateInputChanged += isPrivate => { if (!isPrivate) privateEnd.TrySetResult(); };
        await session.ConnectAsync(timeout.Token);
        using var server = await listener.AcceptTcpClientAsync(timeout.Token);
        var stream = server.GetStream();
        await stream.WriteAsync(new byte[] { 255, 251, 1 }.Concat(Encoding.UTF8.GetBytes("secret")).Concat(new byte[] { 255, 252, 1 }).ToArray(), timeout.Token);
        Assert.True((await firstText.Task.WaitAsync(timeout.Token)).MayContainPrivateText);

        // Split a four-byte character across the privacy boundary. Reading negotiation
        // replies synchronizes packets without relying on TCP packet boundaries or sleeps.
        var replies = new byte[6];
        await stream.ReadExactlyAsync(replies, timeout.Token);
        await stream.WriteAsync(new byte[] { 255, 251, 1, 0xF0 }, timeout.Token);
        await stream.ReadExactlyAsync(new byte[3], timeout.Token);
        await stream.WriteAsync(new byte[] { 255, 252, 1 }, timeout.Token);
        await stream.ReadExactlyAsync(new byte[3], timeout.Token);
        await privateEnd.Task.WaitAsync(timeout.Token);
        // This public read still cannot produce a complete character. Negotiate
        // suppress-go-ahead solely to acknowledge that these bytes were received.
        await stream.WriteAsync(new byte[] { 0x9F, 255, 251, 3 }, timeout.Token);
        await stream.ReadExactlyAsync(new byte[3], timeout.Token);
        await stream.WriteAsync(new byte[] { 0xA7, 0x99, 10 }, timeout.Token);
        var receivedUnicode = await unicodeText.Task.WaitAsync(timeout.Token);
        Assert.True(receivedUnicode.MayContainPrivateText);
        Assert.Contains("🧙", receivedUnicode.Text);
        await stream.WriteAsync(Encoding.UTF8.GetBytes("public\n"), timeout.Token);
        Assert.False((await publicText.Task.WaitAsync(timeout.Token)).MayContainPrivateText);
        // Both event APIs carry the same visible text; legacy Output remains intact.
        await session.DisposeAsync();
        lock (output) Assert.Equal("secret🧙\npublic\n", output.ToString());
    }

    [Fact]
    public async Task RealTcpConnectionDecodesSplitUtf8FramesCommandsAndNoticesRemoteClose()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using var session = new TelnetSession(new ConnectionProfile { Host = "127.0.0.1", Port = port });
        var output = new StringBuilder();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Output += text => { output.Append(text); if (output.ToString().Contains("café>")) arrived.TrySetResult(); };
        session.StatusChanged += status => { if (!status.Connected) closed.TrySetResult(); };
        await session.ConnectAsync(timeout.Token);
        using var server = await listener.AcceptTcpClientAsync(timeout.Token);
        await server.GetStream().WriteAsync(new byte[] { 99, 97, 102, 0xC3 }, timeout.Token);
        await server.GetStream().WriteAsync(new byte[] { 0xA9, 62 }, timeout.Token);
        await arrived.Task.WaitAsync(timeout.Token);
        Assert.Equal("café>", output.ToString());
        await session.SendCommandAsync("look", timeout.Token);
        var bytes = new byte[6];
        await server.GetStream().ReadExactlyAsync(bytes, timeout.Token);
        Assert.Equal("look\r\n", Encoding.UTF8.GetString(bytes));
        server.Close();
        await closed.Task.WaitAsync(timeout.Token);
        Assert.False(session.IsConnected);
    }

    [Fact]
    public async Task DisposingSessionClosesSocketAndStopsReceiving()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var session = new TelnetSession(new() { Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port });
        await session.ConnectAsync(timeout.Token);
        using var server = await listener.AcceptTcpClientAsync(timeout.Token);
        await session.DisposeAsync();
        Assert.Equal(0, await server.GetStream().ReadAsync(new byte[1], timeout.Token));
        Assert.False(session.IsConnected);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SendCommandAsync("look"));
    }

    [Fact]
    public async Task DemoMovementUsesConnectionsAndFailedMovesDoNotChangeRoom()
    {
        await using var demo = new DemoSession();
        var terminal = new AnsiTerminal();
        demo.Output += terminal.Append;
        await demo.ConnectAsync();
        Assert.True(demo.IsConnected);
        Assert.Contains("The Lantern & the Rain", terminal.PlainText);
        terminal.Clear();
        await demo.SendCommandAsync("north");
        Assert.Contains("The Old Market", terminal.PlainText);
        terminal.Clear();
        await demo.SendCommandAsync("west");
        await demo.SendCommandAsync("look");
        Assert.Contains("can't go", terminal.PlainText);
        Assert.Contains("The Old Market", terminal.PlainText);
        await demo.DisposeAsync();
        Assert.False(demo.IsConnected);
    }
}
