using System.Text;
using Wandur.Core.Protocol;

namespace Wandur.Core.Tests;

public sealed class ProtocolDiscoveryTests
{
    private static byte[] Frame(byte option, string body) => [255, 250, option, .. Encoding.UTF8.GetBytes(body), 255, 240];

    [Fact]
    public void NativeReportsAcceptCustomFieldsAndRejectInvalidNames()
    {
        var parser = new TelnetParser(); parser.Feed([255, 251, 69]);
        var reply = parser.Feed(Frame(69, "\u0001REPORTABLE_VARIABLES\u0002\u0005\u0002ROOM\u0002HEALTH\u0002FORCE_POOL\u0002ROOM_VNUM\u0002HEALTH\u0002bad name\u00029BAD\u0006")).Reply;
        Assert.Equal(Frame(69, "\u0001REPORT\u0002ROOM\u0002HEALTH\u0002FORCE_POOL\u0002ROOM_VNUM"), reply);
        Assert.Equal(Frame(69, "\u0001REPORT\u0002EXPERIENCE"), parser.Feed(Frame(69, "\u0001REPORTABLE_VARIABLES\u0002EXPERIENCE")).Reply);
    }

    [Fact]
    public void TunnelDiscoveryRetainsUnknownPackagesAndResetsAfterRenegotiation()
    {
        var parser = new TelnetParser(); parser.Feed([255, 251, 201]);
        var reports = Frame(201, "MSDP {\"REPORTABLE_VARIABLES\":[\"HEALTH\",\"FORCE_POOL\",\"HEALTH\",\"bad name\"]}");
        Assert.Equal(Frame(201, "MSDP {\"REPORT\":[\"HEALTH\",\"FORCE_POOL\"]}"), parser.Feed(reports).Reply);
        Assert.Empty(parser.Feed(reports).Reply);
        var custom = parser.Feed(Frame(201, "Jedi.Custom {\"force\":42}"));
        Assert.Single(custom.DataMessages); Assert.Single(custom.Gmcp); Assert.Empty(custom.Reply);
        Assert.Empty(parser.Feed(Frame(201, "MSDP {bad json")).Reply);
        parser.Feed([255, 252, 201]); parser.Feed([255, 251, 201]);
        Assert.NotEmpty(parser.Feed(reports).Reply);
    }

    [Fact]
    public void SubscriptionsAreBatchedBoundedAndIndependentBetweenTransports()
    {
        var parser = new TelnetParser(); parser.Feed([255, 251, 69, 255, 251, 201]);
        var names = Enumerable.Range(0, 256).Select(i => "FIELD_" + i).ToArray();
        var native = parser.Feed(Frame(69, "\u0001REPORTABLE_VARIABLES\u0002\u0005" + string.Concat(names.Select(n => "\u0002" + n)) + "\u0006"));
        Assert.Equal(8, native.Reply.Count(b => b == 250));
        Assert.Contains("FIELD_255", Encoding.UTF8.GetString(native.Reply));
        Assert.Empty(parser.Feed(Frame(69, "\u0001REPORTABLE_VARIABLES\u0002ANOTHER_FIELD")).Reply);
        Assert.NotEmpty(parser.Feed(Frame(201, "MSDP {\"REPORTABLE_VARIABLES\":\"FIELD_0\"}")).Reply);
    }
}
