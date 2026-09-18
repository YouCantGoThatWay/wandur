using L = Wandur.Core.Localization.Strings;
using System.Text;

namespace Wandur.Core.Protocol;

public enum TelnetOptionState { Unknown, Enabled, Disabled }
public sealed record TelnetProtocolState(TelnetOptionState Gmcp = TelnetOptionState.Unknown, TelnetOptionState Msdp = TelnetOptionState.Unknown);

public sealed record TelnetDataMessage(byte Option, byte[] Payload)
{
    public bool MayContainPrivateText { get; init; }
}

public sealed record TelnetPacket(byte[] Text, byte[] Reply, IReadOnlyList<string> Gmcp)
{
    public bool MayContainPrivateText { get; init; }
    public IReadOnlyList<byte[]> Msdp { get; init; } = [];
    public IReadOnlyList<TelnetDataMessage> DataMessages { get; init; } = [];
}

public sealed class TelnetParser
{
    private enum State { Text, Iac, Option, Sub, SubIac }
    private State _state;
    private byte _verb;
    private readonly List<byte> _sub = [];
    private readonly HashSet<byte> _remote = [];
    private readonly HashSet<byte> _local = [];
    private readonly ProtocolDiscovery _discovery = new();
    private bool _overflow;
    private bool _subPrivate;
    private bool _localPrivate;
    public bool ServerEcho { get; private set; }
    public TelnetProtocolState ProtocolState { get; private set; } = new();

    /// <summary>Latch privacy on an unfinished message, even if no bytes arrive during the interval.</summary>
    public void SetLocalPrivateInput(bool enabled)
    {
        _localPrivate = enabled;
        if (enabled && _state is State.Sub or State.SubIac) _subPrivate = true;
    }

    public TelnetPacket Feed(ReadOnlySpan<byte> bytes)
    {
        var text = new List<byte>();
        var replies = new List<byte>();
        var gmcp = new List<string>();
        var msdp = new List<byte[]>();
        var dataMessages = new List<TelnetDataMessage>();
        // A single read can enter and leave server echo mode. Preserve any private
        // interval instead of inferring privacy from only the final echo state.
        var mayContainPrivateText = ServerEcho || _localPrivate;
        foreach (byte b in bytes)
        {
            switch (_state)
            {
                case State.Text:
                    if (b == 255) _state = State.Iac;
                    else text.Add(b);
                    break;
                case State.Iac:
                    if (b == 255) { text.Add(b); _state = State.Text; }
                    else if (b is >= 251 and <= 254) { _verb = b; _state = State.Option; }
                    else if (b == 250) { _sub.Clear(); _overflow = false; _subPrivate = ServerEcho || _localPrivate; _state = State.Sub; }
                    else _state = State.Text;
                    break;
                case State.Option:
                    Negotiate(b, replies);
                    mayContainPrivateText |= ServerEcho;
                    _state = State.Text;
                    break;
                case State.Sub:
                    if (b == 255) _state = State.SubIac;
                    else AddSub(b);
                    break;
                case State.SubIac:
                    if (b == 240)
                    {
                        if (!_overflow && _sub.Count > 0)
                        {
                            if (_sub[0] == 24 && _local.Contains(24) && _sub.Count > 1 && _sub[1] == 1)
                                replies.AddRange(Subnegotiation(24, new byte[] { 0 }.Concat(Encoding.ASCII.GetBytes("WANDUR")).ToArray()));
                            if (_sub[0] == 201 && _remote.Contains(201))
                            {
                                var payload = _sub.Skip(1).ToArray();
                                gmcp.Add(Encoding.UTF8.GetString(payload));
                                dataMessages.Add(new(201, payload) { MayContainPrivateText = _subPrivate || GmcpLoginProtocol.IsPrivate(gmcp[^1]) });
                                foreach (var request in _discovery.Receive(201, payload)) replies.AddRange(Subnegotiation(201, request));
                            }
                            if (_sub[0] == 69 && _remote.Contains(69))
                            {
                                var payload = _sub.Skip(1).ToArray();
                                msdp.Add(payload);
                                dataMessages.Add(new(69, payload) { MayContainPrivateText = _subPrivate });
                                foreach (var request in _discovery.Receive(69, payload)) replies.AddRange(Subnegotiation(69, request));
                            }
                        }
                        _sub.Clear();
                        _state = State.Text;
                    }
                    else { if (b == 255) AddSub(b); _state = State.Sub; }
                    break;
            }
        }
        return new(text.ToArray(), replies.ToArray(), gmcp)
        {
            Msdp = msdp,
            DataMessages = dataMessages,
            MayContainPrivateText = mayContainPrivateText
        };
    }

    private void AddSub(byte b)
    {
        if (_sub.Count < 16_384 && !_overflow) _sub.Add(b);
        else { _overflow = true; _sub.Clear(); }
    }

    private void Negotiate(byte option, List<byte> reply)
    {
        if (_verb is 251 or 252)
        {
            var state = _verb == 251 ? TelnetOptionState.Enabled : TelnetOptionState.Disabled;
            if (option == 201) ProtocolState = ProtocolState with { Gmcp = state };
            if (option == 69) ProtocolState = ProtocolState with { Msdp = state };
        }
        switch (_verb)
        {
            case 251: // WILL: accept server echo, suppress-go-ahead, MSDP, and GMCP.
                if (option is 1 or 3 or 69 or 201)
                {
                    if (_remote.Add(option))
                    {
                        reply.AddRange([255, 253, option]);
                        if (option == 201)
                        {
                            reply.AddRange(Subnegotiation(201, Encoding.UTF8.GetBytes("Core.Hello {\"client\":\"Wandur\",\"version\":\"0.1.0\"}")));
                            reply.AddRange(Subnegotiation(201, Encoding.UTF8.GetBytes(ProtocolDiscovery.GmcpSupports)));
                            reply.AddRange(Subnegotiation(201, Encoding.UTF8.GetBytes(ProtocolDiscovery.GmcpDiscovery)));
                        }
                        if (option == 69)
                            reply.AddRange(Subnegotiation(69, Encoding.UTF8.GetBytes("\u0001LIST\u0002REPORTABLE_VARIABLES")));
                    }
                    if (option == 1) ServerEcho = true;
                }
                else reply.AddRange([255, 254, option]);
                break;
            case 252: // WONT
                if (_remote.Remove(option)) reply.AddRange([255, 254, option]);
                if (option == 1) ServerEcho = false;
                _discovery.Reset(option);
                break;
            case 253: // DO: a conservative fixed terminal width until resize support.
                if (option is 3 or 24 or 31)
                {
                    if (_local.Add(option))
                    {
                        reply.AddRange([255, 251, option]);
                        if (option == 31) reply.AddRange(Subnegotiation(31, [0, 100, 0, 40]));
                    }
                }
                else reply.AddRange([255, 252, option]);
                break;
            case 254:
                if (_local.Remove(option)) reply.AddRange([255, 252, option]);
                break;
        }
    }

    internal static byte[] Subnegotiation(byte option, byte[] payload)
    {
        var result = new List<byte> { 255, 250, option };
        foreach (var b in payload) { result.Add(b); if (b == 255) result.Add(b); }
        result.AddRange([255, 240]);
        return result.ToArray();
    }

    public static byte[] EncodeCommand(string command, Encoding encoding)
    {
        if (command.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new ArgumentException(L.SendOneCommandAtATime, nameof(command));
        if (command.Length > 8192) throw new ArgumentException(L.CommandIsTooLong, nameof(command));
        var result = new List<byte>();
        foreach (var b in encoding.GetBytes(command)) { result.Add(b); if (b == 255) result.Add(b); }
        result.AddRange([13, 10]);
        return result.ToArray();
    }
}
