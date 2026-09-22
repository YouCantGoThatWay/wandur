using System.Text;
using Wandur.Core.Protocol;

namespace Wandur.Core.Tests;

public class TelnetParserTests
{
    [Fact]
    public void NegotiationsSplitAtEveryByteDoNotLeakIntoText()
    {
        var parser = new TelnetParser();
        var text = new List<byte>();
        var replies = new List<byte>();
        foreach (byte b in new byte[] { 72, 255, 251, 3, 105, 255, 251, 86, 33 })
        {
            var packet = parser.Feed([b]);
            text.AddRange(packet.Text);
            replies.AddRange(packet.Reply);
        }
        Assert.Equal("Hi!", Encoding.UTF8.GetString(text.ToArray()));
        Assert.Equal(new byte[] { 255, 253, 3, 255, 254, 86 }, replies);
    }

    [Fact]
    public void EscapedIacBecomesOneLiteralByte()
    {
        var parser = new TelnetParser();
        Assert.Equal(new byte[] { 65, 255, 66 }, parser.Feed([65, 255, 255, 66]).Text);
    }

    [Fact]
    public void ServerEchoNegotiationTracksPasswordEntry()
    {
        var parser = new TelnetParser();
        Assert.Equal(new byte[] { 255, 253, 1 }, parser.Feed([255, 251, 1]).Reply);
        Assert.True(parser.ServerEcho);
        parser.Feed([255, 252, 1]);
        Assert.False(parser.ServerEcho);
    }

    [Fact]
    public void CoalescedEchoNegotiationsPreservePrivateTextMetadata()
    {
        var parser = new TelnetParser();
        var packet = parser.Feed([255, 251, 1, 115, 101, 99, 114, 101, 116, 255, 252, 1]);
        Assert.False(parser.ServerEcho);
        Assert.Equal("secret", Encoding.UTF8.GetString(packet.Text));
        Assert.True(packet.MayContainPrivateText);
        Assert.False(parser.Feed(Encoding.UTF8.GetBytes("public")).MayContainPrivateText);
    }

    [Fact]
    public void EchoPrivacyMetadataIncludesInitialStateAndSplitNegotiation()
    {
        var parser = new TelnetParser();
        parser.Feed([255, 251]);
        Assert.True(parser.Feed([1, 115]).MayContainPrivateText);
        Assert.True(parser.Feed([101, 255, 252]).MayContainPrivateText);
        Assert.True(parser.Feed([1, 99]).MayContainPrivateText);
        Assert.False(parser.Feed([120]).MayContainPrivateText);
    }

    [Fact]
    public void TerminalTypeRequestIsAnsweredAfterNegotiation()
    {
        var parser = new TelnetParser();
        Assert.Equal(new byte[] { 255, 251, 24 }, parser.Feed([255, 253, 24]).Reply);
        var reply = parser.Feed([255, 250, 24, 1, 255, 240]).Reply;
        Assert.Equal(new byte[] { 255, 250, 24, 0 }.Concat(Encoding.ASCII.GetBytes(ClientIdentity.TerminalType)).Concat(new byte[] { 255, 240 }), reply);
    }

    [Fact]
    public void GmcpHelloNamesTheClientForWorldAdmins()
    {
        var parser = new TelnetParser();
        var reply = Encoding.UTF8.GetString(parser.Feed([255, 251, 201]).Reply);
        Assert.Contains(ClientIdentity.GmcpHelloBody, reply);
        Assert.Contains("Wandur Mud Client (WMC)", reply);
        Assert.Contains("www.wandur.net", reply);
    }

    [Fact]
    public void GmcpIsEmittedSeparatelyFromVisibleText()
    {
        var parser = new TelnetParser();
        parser.Feed([255, 251, 201]);
        var input = new byte[] { 255, 250, 201 }.Concat(Encoding.UTF8.GetBytes("Room.Info {\"num\":42}")).Concat(new byte[] { 255, 240, 62 }).ToArray();
        var packet = parser.Feed(input);
        Assert.Equal(">", Encoding.UTF8.GetString(packet.Text));
        Assert.Equal("Room.Info {\"num\":42}", Assert.Single(packet.Gmcp));
    }

    [Fact]
    public void RepeatedOffersDoNotCreateNegotiationLoops()
    {
        var parser = new TelnetParser();
        parser.Feed([255, 251, 3]);
        Assert.Empty(parser.Feed([255, 251, 3]).Reply);
    }

    [Fact]
    public void OversizedSubnegotiationIsDiscardedUntilItsEnd()
    {
        var parser = new TelnetParser();
        parser.Feed([255, 250, 201]);
        Assert.Empty(parser.Feed(Enumerable.Repeat((byte)65, 100_000).ToArray()).Text);
        var packet = parser.Feed([255, 240, 79, 75]);
        Assert.Empty(packet.Gmcp);
        Assert.Equal("OK", Encoding.UTF8.GetString(packet.Text));
    }

    [Fact]
    public void OutgoingCommandsHaveOneCrlfAndEscapeIac()
    {
        Assert.Equal(new byte[] { 65, 255, 255, 13, 10 }, TelnetParser.EncodeCommand("Aÿ", Encoding.Latin1));
        Assert.Throws<ArgumentException>(() => TelnetParser.EncodeCommand("look\nquit", Encoding.UTF8));
    }
}
