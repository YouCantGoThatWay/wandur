using System.Net;
using System.Net.Sockets;
using System.Text;
using Wandur.Discovery.Worker;
using Wandur.Models;
namespace Wandur.Discovery.Tests;
public class MappingGenerationTests
{
    [Fact]
    public void JediAliasesBecomeGenericCharacterOpponentAndVehicleResources()
    {
        var evidence=new ProtocolEvidence { Fields=new[] { "HEALTH","HEALTHMAX","LEVELENGINEERING","SHIPHULL","SHIPMAXHULL","MONEYBANK","OPPONENTNAME","ROOMVNUM","MYSTERY" }
            .Select(s=>new ObservedField(new("MSDP","MSDP","/"+s),["unknown"],true)).ToArray() };
        var bindings=DeterministicMappings.Create(evidence);
        Assert.Contains(bindings,b=>b.Source.Path=="/SHIPMAXHULL" && b.Target==new MappingTarget("vehicle","resource","hull","maximum"));
        Assert.Contains(bindings,b=>b.Source.Path=="/LEVELENGINEERING" && b.Target==new MappingTarget("character","progression","engineering","current"));
        Assert.Contains(bindings,b=>b.Source.Path=="/MONEYBANK" && b.Target==new MappingTarget("character","currency","money","bank"));
        Assert.DoesNotContain(bindings,b=>b.Source.Path=="/MYSTERY");
        Assert.True(MappingValidation.IsValid(ContractTests.Example() with { Bindings=bindings },evidence));
    }
    [Theory]
    [InlineData("127.0.0.1",false)] [InlineData("::1",false)] [InlineData("::ffff:10.1.2.3",false)]
    [InlineData("169.254.169.254",false)] [InlineData("192.168.1.2",false)] [InlineData("8.8.8.8",true)]
    [InlineData("2606:4700:4700::1111",true)]
    public void PublicProbeRejectsLocalAndMetadataAddresses(string ip,bool expected) => Assert.Equal(expected,WorldProbe.IsPublic(IPAddress.Parse(ip)));

    [Fact]
    public async Task AnonymousProbeNegotiatesAndDiscoversAtUsernamePromptWithoutSendingCredentials()
    {
        using var listener=new TcpListener(IPAddress.Loopback,0); listener.Start();
        var port=((IPEndPoint)listener.LocalEndpoint).Port;
        var task=new WorldProbe(TimeSpan.FromSeconds(2),allowPrivateNetwork:true).ProbeAsync(new("127.0.0.1",port),default);
        using var token=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var socket=await listener.AcceptTcpClientAsync(token.Token); var stream=socket.GetStream();
        await stream.WriteAsync(new byte[]{255,251,201},token.Token);
        var bytes=new byte[4096]; var initial=await stream.ReadAsync(bytes,token.Token);
        Assert.Contains("Core.Hello",Encoding.UTF8.GetString(bytes,0,initial));
        var data=Encoding.UTF8.GetBytes("MSDP {\"REPORTABLE_VARIABLES\":[\"SERVERID\",\"HEALTH\",\"HEALTHMAX\"]}");
        await stream.WriteAsync(new byte[]{255,250,201}.Concat(data).Concat(new byte[]{255,240}).Concat(Encoding.ASCII.GetBytes("Enter username: ")).ToArray(),token.Token);
        var report=await stream.ReadAsync(bytes,token.Token);
        Assert.Contains("REPORT",Encoding.UTF8.GetString(bytes,0,report));
        Assert.DoesNotContain("Credentials",Encoding.UTF8.GetString(bytes,0,report));
        var result=await task;
        Assert.Equal("observed",result.Status);
        Assert.Contains(result.Evidence.Fields,f=>f.Source.Path=="/HEALTHMAX");
    }
}
