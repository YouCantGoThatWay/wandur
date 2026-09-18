using L = Wandur.Core.Localization.Strings;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Wandur.Core.Protocol;
using Wandur.Core.Mapping;
using Wandur.Core.Settings;

namespace Wandur.Core.Sessions;

public sealed class TelnetSession(ConnectionProfile profile) : IMudSession
{
    private readonly TelnetParser _parser = new();
    private readonly object _parserLock = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly TcpClient _client = new() { NoDelay = true };
    private CancellationTokenSource? _lifetime;
    private Stream? _stream;
    private Task? _receiveTask;
    private int _started;
    private int _disposed;
    private volatile bool _connected;
    public event Action<string>? Output;
    public event Action<ReceivedSessionText>? TextReceived;
    public event Action<SessionStatus>? StatusChanged;
    public event Action<bool>? PrivateInputChanged;
    public event Action<string>? GmcpReceived;
    public event Action<GmcpLoginMessage>? GmcpLoginReceived;
    public event Action<ReceivedSessionText>? GmcpMessageReceived;
    public event Action<TelnetDataMessage>? ProtocolMessageReceived;
    public event Action<RoomObservation>? RoomReceived;
    public event Action<TelnetProtocolState>? ProtocolStateChanged;
    /// <summary>Raised before room metadata even when ECHO enters and leaves within one read.</summary>
    public event Action? PrivateIntervalReceived;
    private volatile bool _remoteEcho;
    public bool RemoteEcho => _remoteEcho;
    public TelnetProtocolState ProtocolState { get; private set; } = new();
    public bool IsConnected => _connected;
    private volatile bool _localPrivate;
    private long _privacyEpoch;
    public void SetLocalPrivateInput(bool enabled)
    {
        lock (_parserLock)
        {
            if (_localPrivate != enabled) Interlocked.Increment(ref _privacyEpoch);
            _localPrivate = enabled;
            _parser.SetLocalPrivateInput(enabled);
        }
    }
    private Encoding TextEncoding => profile.Encoding == "latin1" ? Encoding.Latin1 : Encoding.UTF8;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        profile.Validate();
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException(L.CreateANewSessionToReconnect);
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await _client.ConnectAsync(profile.Host, profile.Port, timeout.Token).ConfigureAwait(false);
            _stream = _client.GetStream();
            if (profile.UseTls)
            {
                var tls = new SslStream(_stream, leaveInnerStreamOpen: false);
                _stream = tls;
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = profile.Host }, timeout.Token).ConfigureAwait(false);
            }
            _connected = true;
            StatusChanged?.Invoke(new(true, profile.UseTls ? L.ConnectedTLS : L.ConnectedTelnet));
            _receiveTask = ReceiveAsync(_lifetime.Token);
        }
        catch
        {
            _connected = false; _stream?.Dispose(); _client.Dispose();
            throw;
        }
    }

    private async Task ReceiveAsync(CancellationToken token)
    {
        var bytes = new byte[8192];
        var characters = new char[TextEncoding.GetMaxCharCount(bytes.Length)];
        var decoder = TextEncoding.GetDecoder();
        var decoderMayContainPrivateText = false;
        string status = L.ServerClosedTheConnection;
        try
        {
            while (!token.IsCancellationRequested)
            {
                int count = await _stream!.ReadAsync(bytes, token).ConfigureAwait(false);
                if (count == 0) break;
                bool wasPrivate = _parser.ServerEcho;
                TelnetPacket packet;
                lock (_parserLock) packet = _parser.Feed(bytes.AsSpan(0, count));
                _remoteEcho = _parser.ServerEcho;
                if (packet.MayContainPrivateText)
                {
                    Interlocked.Increment(ref _privacyEpoch);
                    PrivateIntervalReceived?.Invoke();
                }
                if (ProtocolState != _parser.ProtocolState)
                {
                    ProtocolState = _parser.ProtocolState;
                    ProtocolStateChanged?.Invoke(ProtocolState);
                }
                if (packet.Reply.Length > 0) await WriteAsync(packet.Reply, token).ConfigureAwait(false);
                if (wasPrivate != _parser.ServerEcho) PrivateInputChanged?.Invoke(_parser.ServerEcho);
                // Servers may negotiate both protocols. Keep updates in wire order so an older
                // room from one protocol cannot overwrite the newest room from the other.
                foreach (var message in packet.DataMessages)
                {
                    ProtocolMessageReceived?.Invoke(message with { MayContainPrivateText = message.MayContainPrivateText || packet.MayContainPrivateText });
                    RoomObservation? room;
                    if (message.Option == 201)
                    {
                        var gmcp = Encoding.UTF8.GetString(message.Payload);
                        if (GmcpLoginProtocol.Decode(gmcp) is { } login) GmcpLoginReceived?.Invoke(login);
                        GmcpReceived?.Invoke(gmcp);
                        GmcpMessageReceived?.Invoke(new(gmcp, message.MayContainPrivateText || packet.MayContainPrivateText));
                        room = RoomProtocolDecoder.FromGmcp(gmcp);
                    }
                    else room = RoomProtocolDecoder.FromMsdp(message.Payload);
                    if (room is not null) RoomReceived?.Invoke(room);
                }
                int length = decoder.GetChars(packet.Text, 0, packet.Text.Length, characters, 0, false);
                // Negotiation-only reads add no bytes to the decoder's pending character.
                if (packet.Text.Length > 0) decoderMayContainPrivateText |= packet.MayContainPrivateText;
                if (length > 0)
                {
                    var text = new string(characters, 0, length);
                    TextReceived?.Invoke(new(text, decoderMayContainPrivateText));
                    Output?.Invoke(text);
                    // Retain provenance only for an incomplete character at this packet's
                    // end. Complete private text must not suppress the next public read.
                    // Reads that emit no characters retain provenance until completion.
                    decoderMayContainPrivateText = packet.MayContainPrivateText &&
                        TextEncoding.CodePage == Encoding.UTF8.CodePage && HasIncompleteUtf8Suffix(packet.Text);
                }
            }
        }
        catch (OperationCanceledException) { status = L.Disconnected; }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        { status = token.IsCancellationRequested ? L.Disconnected : L.Format(L.ConnectionLost, ex.Message); }
        finally
        {
            _connected = false;
            _stream?.Dispose(); _client.Dispose();
            PrivateInputChanged?.Invoke(false);
            StatusChanged?.Invoke(new(false, status));
        }
    }

    private static bool HasIncompleteUtf8Suffix(ReadOnlySpan<byte> bytes)
    {
        var lead = bytes.Length - 1;
        while (lead >= 0 && bytes[lead] is >= 0x80 and <= 0xBF) lead--;
        if (lead < 0) return false;
        var expected = bytes[lead] switch
        {
            >= 0xC2 and <= 0xDF => 2,
            >= 0xE0 and <= 0xEF => 3,
            >= 0xF0 and <= 0xF4 => 4,
            _ => 1
        };
        return bytes.Length - lead < expected;
    }

    public async Task SendCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        if (!_connected) throw new InvalidOperationException(L.ConnectToAWorldFirst);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime!.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await WriteAsync(TelnetParser.EncodeCommand(command, TextEncoding), timeout.Token).ConfigureAwait(false);
    }

    public async Task SendLoginCredentialsAsync(string? account, string? password, CancellationToken cancellationToken = default)
    {
        if (!_connected || ProtocolState.Gmcp != TelnetOptionState.Enabled) throw new InvalidOperationException(L.ConnectToAWorldFirst);
        // This path never publishes outgoing credentials to diagnostics, scripts or command history.
        var body = string.IsNullOrEmpty(account) || string.IsNullOrEmpty(password) ? "{}" :
            JsonSerializer.Serialize(new { account, password });
        var payload = Encoding.UTF8.GetBytes("Char.Login.Credentials " + body);
        if (payload.Length > ProtocolDiagnosticFormatter.MaximumPayloadBytes) throw new ArgumentException(L.CommandIsTooLong);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime!.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await WriteAsync(TelnetParser.Subnegotiation(201, payload), timeout.Token).ConfigureAwait(false);
    }

    /// <summary>Request fresh observations using fixed capabilities and validated mapped MSDP names.</summary>
    public async Task<bool> RefreshProtocolSubscriptionsAsync(Wandur.Models.WorldMapping? mapping = null, CancellationToken cancellationToken = default)
    {
        if (!_connected || _localPrivate || RemoteEcho ||
            ProtocolState is not { Gmcp: TelnetOptionState.Enabled } and not { Msdp: TelnetOptionState.Enabled }) return false;
        if (mapping is not null && (!Wandur.Models.MappingValidation.IsValid(mapping) ||
            !mapping.Endpoint.Matches(new(profile.Host, profile.Port, profile.UseTls)))) return false;
        var epoch = Interlocked.Read(ref _privacyEpoch);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime!.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var requests = new List<(byte Option, byte[] Bytes)>();
        if (ProtocolState.Gmcp == TelnetOptionState.Enabled)
            requests.Add((201, TelnetParser.Subnegotiation(201, Encoding.UTF8.GetBytes(ProtocolDiscovery.GmcpSupports))));
        foreach (var option in new byte[] { 69, 201 })
        {
            if (mapping is null) continue;
            var protocol = option == 69 ? "MSDP" : "GMCP";
            var names = mapping.Bindings.Where(b => b.Source.Protocol == protocol && b.Source.Package == "MSDP")
                .Select(b => b.Source.Path.Split('/').ElementAtOrDefault(1) ?? "")
                .Where(name => name.Length is > 0 and <= 128 && !char.IsAsciiDigit(name[0]) &&
                    name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
                .Distinct(StringComparer.Ordinal).Take(256);
            foreach (var batch in names.Chunk(32))
            foreach (var command in new[] { "REPORT", "SEND" })
            {
                var payload = option == 69 ? "\u0001" + command + string.Concat(batch.Select(name => "\u0002" + name)) :
                    "MSDP " + JsonSerializer.Serialize(new Dictionary<string, string[]> { [command] = batch });
                requests.Add((option, TelnetParser.Subnegotiation(option, Encoding.UTF8.GetBytes(payload))));
            }
        }
        await _sendLock.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            foreach (var request in requests)
            {
                if (!_connected || _localPrivate || RemoteEcho || epoch != Interlocked.Read(ref _privacyEpoch)) return false;
                if ((request.Option == 69 ? ProtocolState.Msdp : ProtocolState.Gmcp) != TelnetOptionState.Enabled) continue;
                await _stream!.WriteAsync(request.Bytes, timeout.Token).ConfigureAwait(false);
            }
            return true;
        }
        finally { _sendLock.Release(); }
    }

    private async Task WriteAsync(byte[] bytes, CancellationToken token)
    {
        await _sendLock.WaitAsync(token).ConfigureAwait(false);
        try { await _stream!.WriteAsync(bytes, token).ConfigureAwait(false); }
        finally { _sendLock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _connected = false;
        if (_lifetime is not null) await _lifetime.CancelAsync().ConfigureAwait(false);
        _client.Dispose();
        if (_receiveTask is not null) await _receiveTask.ConfigureAwait(false);
        _stream?.Dispose();
        _lifetime?.Dispose();
    }
}
