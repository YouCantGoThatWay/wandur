using System.Text;
using Wandur.Core.Protocol;

namespace Wandur.Core.Tests;

public sealed class GmcpLoginTests
{
    [Theory]
    [InlineData("{\"type\":[\"password-credentials\"]}", true)]
    [InlineData("{\"type\":[\"oauth\"]}", false)]
    [InlineData("{\"version\":2,\"type\":[\"password-credentials\"]}", false)]
    public void OffersOnlyEnableUnderstoodPasswordVersion(string json, bool supported)
    {
        var message = Assert.IsType<GmcpLoginMessage>(GmcpLoginProtocol.Decode("Char.Login.Default " + json));
        Assert.Equal(GmcpLoginKind.Offer, message.Kind);
        Assert.Equal(supported, message.PasswordSupported);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("\"TRUE\"", true)]
    [InlineData("1", true)]
    [InlineData("false", false)]
    [InlineData("\"false\"", false)]
    [InlineData("0", false)]
    public void ResultAcceptsDocumentedBooleanEncodings(string value, bool expected)
    {
        var message = Assert.IsType<GmcpLoginMessage>(GmcpLoginProtocol.Decode("Char.Login.Result {\"success\":" + value + "}"));
        Assert.Equal(GmcpLoginKind.Result, message.Kind); Assert.Equal(expected, message.Success);
    }

    [Theory]
    [InlineData("Char.Login.Result {\"success\":\"yes\"}")]
    [InlineData("Char.Login.Default {oops")]
    [InlineData("Char.Login.Default []")]
    [InlineData("Char.Login.Result {}")]
    public void MalformedLoginCannotTriggerCredentialsOrClaimSuccess(string value) => Assert.Null(GmcpLoginProtocol.Decode(value));

    [Fact]
    public void LoginFramesArePrivateEvenWithoutPrivateInput()
    {
        var parser = new TelnetParser(); parser.Feed([255, 251, 201]);
        foreach (var package in new[] { "Char.Login.Credentials", "Char.Login.Token", "Char.Login.Result" })
        {
            byte[] frame = [255, 250, 201, .. Encoding.UTF8.GetBytes(package + " {\"secret\":\"fixture-only\"}"), 255, 240];
            Assert.True(Assert.Single(parser.Feed(frame).DataMessages).MayContainPrivateText);
        }
    }
}
