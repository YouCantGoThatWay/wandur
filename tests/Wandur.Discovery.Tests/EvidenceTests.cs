using System.Text;
using Wandur.Discovery.Worker;
using Wandur.Models;
namespace Wandur.Discovery.Tests;
public class EvidenceTests
{
    [Fact]
    public void JediPreloginListProducesSafeSourcesWithoutRetainingValues()
    {
        var collector = new EvidenceCollector();
        collector.Observe(201,Encoding.UTF8.GetBytes("MSDP {\"REPORTABLE_VARIABLES\":[\"\",\"SERVERID\",\"HEALTH\",\"HEALTHMAX\",\"PASSWORD\",\"COMMCHANNEL\"]}"));
        collector.Observe(201,Encoding.UTF8.GetBytes("MSDP {\"SERVERID\":\"Legends of the Jedi\",\"HEALTH\":\"45\"}"));
        collector.Observe(201,Encoding.UTF8.GetBytes("Char.Login.Result {\"token\":\"never-store-this\"}"));
        var result=collector.Snapshot();
        Assert.Equal("Legends of the Jedi",result.ServerId);
        Assert.Contains(result.Fields,f=>f.Source==new ProtocolFieldReference("GMCP","MSDP","/HEALTH") && f.Types.Contains("string"));
        Assert.Contains(result.Fields,f=>f.Source.Path=="/HEALTHMAX" && f.Advertised);
        Assert.DoesNotContain(result.Fields,f=>f.Source.Path.Contains("PASSWORD") || f.Source.Path.Contains("COMMCHANNEL") || f.Source.Package.Contains("Login"));
        Assert.DoesNotContain("never-store-this",System.Text.Json.JsonSerializer.Serialize(result));
    }
    [Fact]
    public void FingerprintIgnoresOrderAndChangingValuesButDetectsNewFields()
    {
        var a=new EvidenceCollector(); var b=new EvidenceCollector();
        a.Observe(201,Encoding.UTF8.GetBytes("Char.Vitals {\"hp\":45,\"maxhp\":100}"));
        b.Observe(201,Encoding.UTF8.GetBytes("Char.Vitals {\"maxhp\":100,\"hp\":30}"));
        Assert.Equal(EvidenceFingerprint.Compute(a.Snapshot()),EvidenceFingerprint.Compute(b.Snapshot()));
        b.Observe(201,Encoding.UTF8.GetBytes("Char.Vitals {\"mp\":20}"));
        Assert.NotEqual(EvidenceFingerprint.Compute(a.Snapshot()),EvidenceFingerprint.Compute(b.Snapshot()));
    }
}
