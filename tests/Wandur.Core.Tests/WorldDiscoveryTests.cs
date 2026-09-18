using System.Net;
using Wandur.Core.Discovery;
using Wandur.Core.Settings;

namespace Wandur.Core.Tests;

public sealed class WorldDiscoveryTests
{
    [Theory]
    [InlineData(" aardmud.org:4000 ", "aardmud.org", 4000)]
    [InlineData("telnet://mud.example.org:23/", "mud.example.org", 23)]
    [InlineData("mud.example.org 4444", "mud.example.org", 4444)]
    [InlineData("[2001:db8::1]:4000", "2001:db8::1", 4000)]
    [InlineData("2001:db8::1", "2001:db8::1", null)]
    [InlineData("mud.example.org", "mud.example.org", null)]
    public void PastedAddressesSeparateHostAndOptionalPort(string text, string host, int? port)
    {
        Assert.True(WorldAddress.TryParse(text, out var address));
        Assert.Equal(host, address!.Host);
        Assert.Equal(port, address.Port);
    }

    [Theory]
    [InlineData("mud.example.org:0")]
    [InlineData("mud.example.org:65536")]
    [InlineData("mud.example.org:abc")]
    [InlineData("telnet://name:secret@mud.example.org:23")]
    [InlineData("https://mud.example.org:443/path")]
    [InlineData("mud.example.org:")]
    public void InvalidPastesAreNotSilentlyReinterpreted(string text) => Assert.False(WorldAddress.TryParse(text, out _));

    [Fact]
    public async Task DirectoryUsesExactHostAndPortAndCachesHostResults()
    {
        var handler = new DirectoryHandler("""
            <a href='https://www.mudconnect.com/cgi-bin/telnet.cgi?mud=Other&amp;url=telnet://other.mud.example.org:4000'>Other</a>
            <a href='https://www.mudconnect.com/cgi-bin/telnet.cgi?mud=The+Lantern+%26+Rain&amp;url=telnet://mud.example.org:4000'>Connect</a>
            <a href='https://www.mudconnect.com/cgi-bin/telnet.cgi?mud=Another+World&amp;url=telnet://mud.example.org:5000'>Connect</a>
            """);
        using var http = new HttpClient(handler);
        var directory = new MudConnectorDirectory(http);
        Assert.Equal("The Lantern & Rain", (await directory.LookupAsync("mud.example.org", 4000))?.Name);
        Assert.Equal("Another World", (await directory.LookupAsync("mud.example.org", 5000))?.Name);
        Assert.Null(await directory.LookupAsync("mud.example.org", 6000));
        Assert.Equal(1, handler.Requests);
        Assert.Equal("https://www.mudconnect.com/cgi-bin/search.cgi?mode=mobile_biglist", handler.LastRequest);
    }

    [Fact]
    public async Task DirectoryAllowsUniqueHostMatchButNeverInventsOne()
    {
        var handler = new DirectoryHandler("""
            <a href='https://www.mudconnect.com/cgi-bin/telnet.cgi?mud=Aardwolf&url=telnet://aardmud.org:23'>Connect</a>
            """);
        using var http = new HttpClient(handler);
        var directory = new MudConnectorDirectory(http);
        Assert.Equal("Aardwolf", (await directory.LookupAsync("AARDMUD.ORG", 4000))?.Name);
        Assert.Null(await directory.LookupAsync("unknown.example.org", 4000));
        Assert.Equal(1, handler.Requests);
    }

    private sealed class DirectoryHandler(string html) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public string LastRequest { get; private set; } = "";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            LastRequest = request.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html) });
        }
    }
}
