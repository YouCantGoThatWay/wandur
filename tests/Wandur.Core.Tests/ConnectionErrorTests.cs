using System.Net.Sockets;
using Wandur.Core.Sessions;
using Wandur.Core.Settings;

namespace Wandur.Core.Tests;

public sealed class ConnectionErrorTests
{
    [Theory]
    [InlineData(SocketError.HostNotFound)]
    [InlineData(SocketError.NoData)]
    public void DnsErrorsIdentifyTheHostAndExplainHowToRecover(SocketError code)
    {
        var message = ConnectionError.Describe(new SocketException((int)code), new ConnectionProfile { Host = "mud.example.org", Port = 5500 });
        Assert.Contains("mud.example.org", message);
        Assert.Contains("hostname", message);
        Assert.Contains("outdated", message);
    }

    [Fact]
    public void OtherErrorsRetainTheirDetails()
    {
        Assert.Equal("Server refused TLS", ConnectionError.Describe(new InvalidOperationException("Server refused TLS"), new ConnectionProfile()));
    }
}
