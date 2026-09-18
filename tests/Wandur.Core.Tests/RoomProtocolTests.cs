using System.Text;
using Wandur.Core.Protocol;
using Wandur.Core.Mapping;
using System.Net;
using System.Net.Sockets;
using Wandur.Core.Sessions;

namespace Wandur.Core.Tests;

public class RoomProtocolTests
{
    [Fact]
    public void LotjVnumIdentifiesRoomAndExitFlagsDoNotBecomeRoomIds()
    {
        var room = Assert.IsType<RoomObservation>(RoomProtocolDecoder.FromGmcp("""
            Room.Info {"name":"The Cockpit | Academy Transport Shuttle ","vnum":561,"exits":{"south":"O","up":"C"},"planet":"Ring of Kafrene"}
            """));
        Assert.Equal("561", room.ServerId);
        Assert.Equal("Ring of Kafrene", room.Area);
        Assert.Equal("The Cockpit | Academy Transport Shuttle", room.Name);
        Assert.True(room.ExitsProvided);
        Assert.Null(room.Exits["south"]); Assert.Null(room.Exits["up"]);
        var tracker = new RoomMapTracker(); tracker.Observe(room);
        Assert.Equal(MapTrackingState.Confirmed, tracker.Snapshot.State);
        Assert.Equal("s:561", tracker.Snapshot.CurrentRoomId);
        Assert.Empty(tracker.Snapshot.Links);
    }

    [Theory]
    [InlineData("561")]
    [InlineData("\"561\"")]
    public void VnumAliasPreservesDestinationIdsAndPrefersExistingArea(string id)
    {
        var room = Assert.IsType<RoomObservation>(RoomProtocolDecoder.FromGmcp("Room.Info {\"vnum\":" + id + ",\"name\":\"Hall\",\"planet\":\"Planet\",\"area\":\"Ship\",\"exits\":{\"north\":562}}"));
        Assert.Equal("561", room.ServerId); Assert.Equal("562", room.Exits["north"]); Assert.Equal("Ship", room.Area);
    }
    [Fact]
    public void GmcpRoomNormalizesIdentityDescriptionAndExitIds()
    {
        var room = Assert.IsType<RoomObservation>(RoomProtocolDecoder.FromGmcp("room.info {\"num\":42,\"name\":\"Hall\",\"desc\":\"Marble\",\"area\":\"Keep\",\"exits\":{\"n\":43,\"s\":-1}}"));
        Assert.Equal("42", room.ServerId);
        Assert.Equal("Hall", room.Name);
        Assert.Equal("Marble", room.Description);
        Assert.Equal("Keep", room.Area);
        Assert.Equal("43", room.Exits["north"]);
        Assert.Null(room.Exits["south"]);
        Assert.Equal(RoomDataSource.Gmcp, room.Source);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("{}")]
    [InlineData("\"unknown\"")]
    [InlineData("\"false\"")]
    [InlineData("\"NaN\"")]
    [InlineData("\"Infinity\"")]
    [InlineData("\"-2\"")]
    [InlineData("\"1.5\"")]
    [InlineData("\"\"")]
    public void SentinelIdsNeverBecomeAuthoritative(string id)
    {
        var room = Assert.IsType<RoomObservation>(RoomProtocolDecoder.FromGmcp("Room.Info {\"id\":" + id + ",\"name\":\"Hall\"}"));
        Assert.Null(room.ServerId);
    }

    [Fact]
    public void GmcpAlternateNamesAndOpaqueIdsAreAccepted()
    {
        var room = Assert.IsType<RoomObservation>(RoomProtocolDecoder.FromGmcp("Room.Info {\"id\":\"zone:42\",\"name\":\"Hall\",\"description\":\"Stone\",\"exits\":[\"e\",\"up\"]}"));
        Assert.Equal("zone:42", room.ServerId);
        Assert.Equal("Stone", room.Description);
        Assert.True(room.Exits.ContainsKey("east"));
        Assert.Null(room.Exits["up"]);
    }

    [Theory]
    [InlineData("Room.Info {")]
    [InlineData("Char.Vitals {\"name\":\"Hall\"}")]
    [InlineData("Room.Info []")]
    [InlineData("Room.Info {\"num\":42}")]
    public void MalformedOrUnrelatedGmcpIsIgnored(string payload) => Assert.Null(RoomProtocolDecoder.FromGmcp(payload));

    [Fact]
    public void OversizedGmcpIsIgnored() => Assert.Null(RoomProtocolDecoder.FromGmcp("Room.Info {\"name\":\"" + new string('a', 20000) + "\"}"));

    [Fact]
    public void MsdpDecodesCoherentRoomAndNestedExits()
    {
        var payload = Encoding.UTF8.GetBytes("\u0001ROOM_VNUM\u000242\u0001ROOM_NAME\u0002Hall\u0001AREA_NAME\u0002Keep\u0001ROOM_EXITS\u0002\u0003\u0001n\u000243\u0001s\u00020\u0004");
        var room = Assert.IsType<RoomObservation>(RoomProtocolDecoder.FromMsdp(payload));
        Assert.Equal("42", room.ServerId);
        Assert.Equal("Hall", room.Name);
        Assert.Equal("Keep", room.Area);
        Assert.Equal("43", room.Exits["north"]);
        Assert.Null(room.Exits["south"]);
        Assert.Equal(RoomDataSource.Msdp, room.Source);
    }

    [Fact]
    public void MsdpPartialUpdatesNeverReuseAnOldNameForANewId()
    {
        Assert.Null(RoomProtocolDecoder.FromMsdp(Encoding.UTF8.GetBytes("\u0001ROOM_NAME\u0002Old hall")));
        Assert.Null(RoomProtocolDecoder.FromMsdp(Encoding.UTF8.GetBytes("\u0001ROOM_VNUM\u000299")));
        Assert.Null(RoomProtocolDecoder.FromMsdp(Encoding.UTF8.GetBytes("\u0001ROOM_VNUM\u000299\u0001ROOM_EXITS\u0002\u0003\u0001n\u00024")));
    }

    [Fact]
    public void CompoundMsdpRoomProvidesCoherentObservation()
    {
        var room = Assert.IsType<RoomObservation>(RoomProtocolDecoder.FromMsdp(Encoding.UTF8.GetBytes("\u0001ROOM\u0002\u0003\u0001VNUM\u000242\u0001NAME\u0002Hall\u0001AREA\u0002Keep\u0001EXITS\u0002\u0003\u0001n\u000243\u0004\u0004")));
        Assert.Equal("42", room.ServerId);
        Assert.Equal("43", room.Exits["north"]);
        Assert.Equal("Keep", room.Area);
    }

    [Fact]
    public void MsdpRequestsAllAdvertisedReportsAndSuppressesDuplicates()
    {
        var parser = new TelnetParser();
        parser.Feed([255, 251, 69]);
        var input = Frame(69, "\u0001REPORTABLE_VARIABLES\u0002\u0005\u0002ROOM_VNUM\u0002ROOM_NAME\u0002HEALTH\u0006");
        Assert.Equal(Frame(69, "\u0001REPORT\u0002ROOM_VNUM\u0002ROOM_NAME\u0002HEALTH"), parser.Feed(input).Reply);
        Assert.Empty(parser.Feed(input).Reply);
        parser.Feed([255, 252, 69]);
        Assert.Empty(parser.Feed(input).Msdp);
        parser.Feed([255, 251, 69]);
        Assert.NotEmpty(parser.Feed(input).Reply);
    }

    [Fact]
    public void MsdpByteSplitsAndUnnegotiatedFramesNeverLeakIntoText()
    {
        var parser = new TelnetParser();
        var payload = "\u0001ROOM_VNUM\u000242\u0001ROOM_NAME\u0002Hall";
        Assert.Empty(parser.Feed(Frame(69, payload)).Msdp);
        parser.Feed([255, 251, 69]);
        var messages = new List<byte[]>();
        foreach (byte b in Frame(69, payload))
        {
            var result = parser.Feed([b]);
            Assert.Empty(result.Text);
            messages.AddRange(result.Msdp);
        }
        Assert.Equal(Encoding.UTF8.GetBytes(payload), Assert.Single(messages));
    }

    [Fact]
    public void MsdpBoundsMalformedTablesAndDeepNesting()
    {
        Assert.Null(RoomProtocolDecoder.FromMsdp(Encoding.UTF8.GetBytes("\u0001ROOM\u0002" + string.Concat(Enumerable.Repeat("\u0003\u0001ROOM\u0002", 30)) + "x")));
        Assert.Null(RoomProtocolDecoder.FromMsdp(new byte[20000]));
        Assert.Null(RoomProtocolDecoder.FromMsdp(Encoding.UTF8.GetBytes("\u0001ROOM_VNUM\u000242\u0001ROOM_NAME\u0002Hall\u0004")));
        Assert.Null(RoomProtocolDecoder.FromMsdp(Encoding.UTF8.GetBytes("\u0001ROOM_VNUM\u000242\u0001ROOM_NAME\u0002Hall\u0001ROOM_NAME\u0002Other")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TelnetSessionEmitsNormalizedRoomEvents(bool gmcp)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var session = new TelnetSession(new() { Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port });
        var arrived = new TaskCompletionSource<RoomObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.RoomReceived += room => arrived.TrySetResult(room);
        await session.ConnectAsync(timeout.Token);
        using var server = await listener.AcceptTcpClientAsync(timeout.Token);
        byte option = gmcp ? (byte)201 : (byte)69;
        await server.GetStream().WriteAsync(new byte[] { 255, 251, option }, timeout.Token);
        await server.GetStream().WriteAsync(Frame(option, gmcp ? "Room.Info {\"num\":42,\"name\":\"Hall\"}" : "\u0001ROOM_VNUM\u000242\u0001ROOM_NAME\u0002Hall"), timeout.Token);
        var room = await arrived.Task.WaitAsync(timeout.Token);
        Assert.Equal("42", room.ServerId);
        Assert.Equal("Hall", room.Name);
        Assert.Equal(gmcp ? RoomDataSource.Gmcp : RoomDataSource.Msdp, room.Source);
    }

    private static byte[] Frame(byte option, string payload) => new byte[] { 255, 250, option }.Concat(Encoding.UTF8.GetBytes(payload)).Concat(new byte[] { 255, 240 }).ToArray();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MixedProtocolRoomEventsPreserveWireOrder(bool msdpFirst)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var session = new TelnetSession(new() { Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port });
        var arrived = new TaskCompletionSource<string?[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ids = new List<string?>();
        session.RoomReceived += room => { ids.Add(room.ServerId); if (ids.Count == 2) arrived.TrySetResult(ids.ToArray()); };
        await session.ConnectAsync(timeout.Token);
        using var server = await listener.AcceptTcpClientAsync(timeout.Token);
        var msdp = Frame(69, "\u0001ROOM_VNUM\u000241\u0001ROOM_NAME\u0002Hall");
        var gmcp = Frame(201, "Room.Info {\"num\":42,\"name\":\"Garden\"}");
        var input = new byte[] { 255, 251, 69, 255, 251, 201 }.Concat(msdpFirst ? msdp : gmcp).Concat(msdpFirst ? gmcp : msdp).ToArray();
        await server.GetStream().WriteAsync(input, timeout.Token);
        Assert.Equal(msdpFirst ? new[] { "41", "42" } : new[] { "42", "41" }, await arrived.Task.WaitAsync(timeout.Token));
    }

    [Fact]
    public void GmcpNegotiationRequestsRoomUpdatesOnce()
    {
        var parser = new TelnetParser();
        var reply = Encoding.UTF8.GetString(parser.Feed([255, 251, 201]).Reply);
        foreach (var module in new[] { "Room 1", "Char 1", "Char.Base 1", "Char.Vitals 1", "Char.Maxstats 1", "Char.Status 1", "Char.Items 1", "Char.Skills 1", "Group 1", "Comm.Channel 1", "MSDP 1" })
            Assert.Contains("\"" + module + "\"", reply);
        Assert.Contains("MSDP {\"LIST\":\"REPORTABLE_VARIABLES\"}", reply);
        Assert.DoesNotContain("Client.Media", reply);
        Assert.Contains("\"Char.Login 1\"", reply);
        Assert.Empty(parser.Feed([255, 251, 201]).Reply);
    }

    [Fact]
    public void MsdpNegotiationDiscoversAvailableReports()
    {
        var parser = new TelnetParser();
        var reply = parser.Feed([255, 251, 69]).Reply;
        Assert.Equal(new byte[] { 255, 253, 69 }, reply.Take(3));
        Assert.Contains("\x01LIST\x02REPORTABLE_VARIABLES", Encoding.UTF8.GetString(reply));
        Assert.Empty(parser.Feed([255, 251, 69]).Reply);
    }
}
