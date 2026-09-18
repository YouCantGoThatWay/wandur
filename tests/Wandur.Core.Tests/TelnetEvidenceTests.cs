using Wandur.Core.Protocol;

namespace Wandur.Core.Tests;

public sealed class TelnetEvidenceTests
{
    [Fact]
    public void NoNegotiationRemainsUnknownAndRemoteRefusalIsDistinctFromEnabled()
    {
        var parser = new TelnetParser();
        Assert.Equal(TelnetOptionState.Unknown, parser.ProtocolState.Gmcp);
        Assert.Equal(TelnetOptionState.Unknown, parser.ProtocolState.Msdp);
        parser.Feed([255, 251]);
        Assert.Equal(TelnetOptionState.Unknown, parser.ProtocolState.Gmcp);
        parser.Feed([201]);
        Assert.Equal(TelnetOptionState.Enabled, parser.ProtocolState.Gmcp);
        Assert.Equal(TelnetOptionState.Unknown, parser.ProtocolState.Msdp);
        parser.Feed([255, 252, 69]);
        Assert.Equal(TelnetOptionState.Disabled, parser.ProtocolState.Msdp);
        parser.Feed([255, 252, 201]);
        Assert.Equal(TelnetOptionState.Disabled, parser.ProtocolState.Gmcp);
    }
}
