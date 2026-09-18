using System.Text;
using Wandur.Core.Protocol;

namespace Wandur.Core.Tests;

public sealed class ProtocolDiagnosticsTests
{
    [Theory]
    [InlineData(201)]
    [InlineData(69)]
    public void PartialMessagesRememberLocalPrivacyBetweenReads(byte option)
    {
        var parser = new TelnetParser();
        parser.Feed([255, 251, option]);
        parser.Feed([255, 250, option, 1]);
        parser.SetLocalPrivateInput(true);
        parser.SetLocalPrivateInput(false);
        var packet = parser.Feed([2, 255, 240]);
        Assert.True(Assert.Single(packet.DataMessages).MayContainPrivateText);
        Assert.False(parser.Feed([255, 250, option, 1, 2, 255, 240]).DataMessages[0].MayContainPrivateText);
    }

    [Fact]
    public void FormatsAllGmcpPackagesIncludingNonRoomData()
    {
        var message = ProtocolDiagnosticFormatter.Format(201, Encoding.UTF8.GetBytes("Char.Vitals {\"hp\":42,\"maxhp\":100}"));
        Assert.Equal("Char.Vitals", message.Name);
        Assert.Contains("\n", message.Body);
        Assert.Contains("\"hp\": 42", message.Body);
        Assert.False(message.Malformed);
    }

    [Fact]
    public void FormatsMsdpTablesAndArraysWithoutRequiringRoomFields()
    {
        var message = ProtocolDiagnosticFormatter.Format(69, Encoding.UTF8.GetBytes("\u0001HEALTH\u0002" + "42\u0001ROOM\u0002\u0003\u0001NAME\u0002Hall\u0001EXITS\u0002\u0005\u0002north\u0002south\u0006\u0004"));
        Assert.Contains("HEALTH", message.Name);
        Assert.Contains("\"NAME\": \"Hall\"", message.Body);
        Assert.Contains("\"north\"", message.Body);
        Assert.False(message.Malformed);
    }

    [Fact]
    public void MalformedAndOversizedPayloadsRemainInspectableAndBounded()
    {
        var malformed = ProtocolDiagnosticFormatter.Format(69, [1, 255]);
        Assert.True(malformed.Malformed);
        Assert.Contains("01FF", malformed.Body);
        var oversized = ProtocolDiagnosticFormatter.Format(201, Encoding.UTF8.GetBytes("Room.Info " + new string('x', 100000)));
        Assert.True(oversized.Truncated);
        Assert.True(oversized.Body.Length <= 32768);
    }
    [Fact]
    public void SensitiveFieldsAreRedactedWithoutHidingTheResponse()
    {
        var message = ProtocolDiagnosticFormatter.Format(201, Encoding.UTF8.GetBytes(
            "Auth.Info {\"success\":false,\"message\":\"Try another account\",\"password\":\"fixture-password\",\"nested\":[{\"access_token\":\"fixture-token\",\"remaining\":2}]}"));
        Assert.Contains("Try another account", message.Body);
        Assert.Contains("remaining", message.Body);
        Assert.DoesNotContain("fixture-password", message.Body);
        Assert.DoesNotContain("fixture-token", message.Body);
    }

    [Fact]
    public void MsdpPasswordsAreRedactedAlongsideVisibleVitals()
    {
        var message = ProtocolDiagnosticFormatter.Format(69, Encoding.UTF8.GetBytes("\u0001HEALTH\u000285\u0001PASSWORD\u0002fixture-password"));
        Assert.Contains("85", message.Body);
        Assert.DoesNotContain("fixture-password", message.Body);
    }

    [Theory]
    [InlineData("Char.Login.Token")]
    [InlineData("Char.Login.Credentials")]
    [InlineData("Char.Login.URL")]
    public void CredentialBearingPackagesNeverExposeUnrecognizedFields(string package)
    {
        var message = ProtocolDiagnosticFormatter.Format(201, Encoding.UTF8.GetBytes(package + " {\"unrecognized\":\"fixture-secret\"}"));
        Assert.Equal(package, message.Name);
        Assert.DoesNotContain("fixture-secret", message.Body);
    }

    [Fact]
    public void KnownCredentialsAreRemovedFromFreeTextReplies()
    {
        const string secret = "fixture-only-\"quoted\"-秘密";
        var response = "Char.Login.Result " + System.Text.Json.JsonSerializer.Serialize(new { success = false, message = "Rejected: " + secret });
        var message = ProtocolDiagnosticFormatter.Format(201, Encoding.UTF8.GetBytes(response), true, [secret]);
        Assert.True(message.Redacted);
        using var document = System.Text.Json.JsonDocument.Parse(message.Body);
        Assert.Equal("Rejected: [redacted]", document.RootElement.GetProperty("message").GetString());
        Assert.False(document.RootElement.GetProperty("success").GetBoolean());
    }

    [Theory]
    [InlineData(201, "Char.Login.Result {broken fixture-secret")]
    [InlineData(69, "\u0001BROKEN\u0002\u0003fixture-secret")]
    public void MalformedPrivatePacketsKeepTheirIdentityButNotUnparseableSecrets(byte option, string body)
    {
        var message = ProtocolDiagnosticFormatter.Format(option, Encoding.UTF8.GetBytes(body), true);
        Assert.True(message.Redacted); Assert.True(message.Malformed);
        Assert.Equal("[redacted]", message.Body);
        Assert.NotEmpty(message.Name);
    }

    [Fact]
    public void LoginDataStaysVisibleEvenWithoutKnownCredentials()
    {
        var message = ProtocolDiagnosticFormatter.Format(201, Encoding.UTF8.GetBytes("Char.Vitals {\"hp\":42,\"maxhp\":100}"), true);
        Assert.False(message.Redacted);
        Assert.Contains("42", message.Body);
        Assert.Contains("100", message.Body);
    }

}
