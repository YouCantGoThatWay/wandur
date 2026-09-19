using System.Text;
using Wandur.Core.Scripting;

namespace Wandur.Core.Tests;

public sealed class ProtocolStateCacheTests
{
    private static byte[] Msdp(string name, string value) => [1, .. Encoding.UTF8.GetBytes(name), 2, .. Encoding.UTF8.GetBytes(value)];

    [Fact]
    public void KeepsTheLatestValuePerVariableAndPackageAndSeedsBothBuckets()
    {
        var cache = new ProtocolStateCache();
        Assert.True(cache.IsEmpty);
        Assert.Null(cache.SeedJson());
        Assert.Equal(2, cache.RecordMsdp([.. Msdp("HEALTH", "90"), .. Msdp("MONEYINV", "12")]));
        Assert.Equal(1, cache.RecordMsdp(Msdp("HEALTH", "100")));
        Assert.True(cache.RecordGmcp("Char.Vitals { \"hp\": 42 , \"mana\": 7 }"));
        Assert.True(cache.RecordGmcp("Char.Vitals {\"hp\":43}"));
        Assert.True(cache.RecordGmcp("Room.Info"));
        Assert.Equal("\"100\"", cache.TryGetMsdp("HEALTH"));
        Assert.Equal("{\"hp\":43}", cache.TryGetGmcp("Char.Vitals"));
        Assert.Equal("null", cache.TryGetGmcp("Room.Info"));
        Assert.Null(cache.TryGetMsdp("NOPE"));
        Assert.Equal("""{"gmcp":{"Char.Vitals":{"hp":43},"Room.Info":null},"msdp":{"HEALTH":"100","MONEYINV":"12"}}""", cache.SeedJson());
        cache.Clear();
        Assert.True(cache.IsEmpty);
        Assert.Null(cache.SeedJson());
    }

    [Theory]
    [InlineData("Char.Vitals {not json")]
    [InlineData("9Bad {\"hp\":1}")]
    [InlineData("Char Vitals {\"hp\":1}")]
    [InlineData("")]
    public void MalformedGmcpMessagesAreIgnored(string message)
    {
        var cache = new ProtocolStateCache();
        Assert.False(cache.RecordGmcp(message));
        Assert.True(cache.IsEmpty);
        Assert.Equal(0, cache.RecordMsdp([9, 9, 9]));
        Assert.False(cache.RecordMsdp("", "\"x\""));
        Assert.False(cache.RecordMsdp("BAD\u0001NAME", "\"x\""));
    }

    [Fact]
    public void EntryValueAndBucketBoundsDropTheUpdateAndKeepThePreviousValue()
    {
        var cache = new ProtocolStateCache();
        for (var i = 0; i < ProtocolStateCache.MaximumEntries; i++) Assert.True(cache.RecordMsdp("V" + i, "\"a\""));
        Assert.False(cache.RecordMsdp("ONE_TOO_MANY", "\"a\""));
        Assert.True(cache.RecordMsdp("V0", "\"updated\""));
        Assert.Equal(ProtocolStateCache.MaximumEntries, cache.MsdpCount);
        Assert.Equal("\"updated\"", cache.TryGetMsdp("V0"));

        var large = "\"" + new string('x', ProtocolStateCache.MaximumValueCharacters - 2) + "\"";
        Assert.True(cache.RecordGmcp("Big.One " + large));
        Assert.False(cache.RecordGmcp("Big.One \"" + new string('y', ProtocolStateCache.MaximumValueCharacters) + "\""));
        Assert.Equal(large, cache.TryGetGmcp("Big.One"));
        // Eight full values fill the bucket exactly; the ninth is dropped and an update within the budget still lands.
        for (var i = 2; i <= ProtocolStateCache.MaximumBucketCharacters / ProtocolStateCache.MaximumValueCharacters; i++)
            Assert.True(cache.RecordGmcp("Big.N" + i + " " + large));
        Assert.False(cache.RecordGmcp("Big.Overflow " + large));
        Assert.Null(cache.TryGetGmcp("Big.Overflow"));
        Assert.True(cache.RecordGmcp("Big.One {\"small\":1}"));
        Assert.True(cache.RecordGmcp("Big.Overflow {\"fits\":true}"));
    }
}
