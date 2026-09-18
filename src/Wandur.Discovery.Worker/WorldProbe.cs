using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using Wandur.Core.Protocol;
using Wandur.Models;

namespace Wandur.Discovery.Worker;

public sealed record ProbeResult(string Status, ProtocolEvidence Evidence);
public interface IWorldProbe { Task<ProbeResult> ProbeAsync(WorldEndpoint endpoint,CancellationToken token); }

public sealed class WorldProbe(TimeSpan? duration=null, bool allowPrivateNetwork=false) : IWorldProbe
{
    public async Task<ProbeResult> ProbeAsync(WorldEndpoint endpoint,CancellationToken token)
    {
        if(!MappingValidation.Endpoint(endpoint)) return new("invalid_endpoint",new());
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(duration??TimeSpan.FromSeconds(15));
        var collector=new EvidenceCollector();
        var connected=false;
        try
        {
            var addresses=await Dns.GetHostAddressesAsync(endpoint.NormalizedHost,deadline.Token);
            if(addresses.Length==0 || !allowPrivateNetwork && addresses.Any(a=>!IsPublic(a))) return new("blocked_endpoint",new());
            using var client=new TcpClient();
            // Connect to the exact validated addresses, not a second DNS lookup.
            await client.ConnectAsync(addresses,endpoint.Port,deadline.Token);
            connected=true;
            await using Stream stream=endpoint.UseTls ? new SslStream(client.GetStream(),false) : client.GetStream();
            if(stream is SslStream tls) await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost=endpoint.NormalizedHost },deadline.Token);
            var parser=new TelnetParser(); var buffer=new byte[8192]; var total=0; var messages=0;
            while(!deadline.IsCancellationRequested)
            {
                var count=await stream.ReadAsync(buffer,deadline.Token);
                if(count==0) break;
                total+=count;
                if(total>1_048_576) return new("limited",collector.Snapshot() with { Limited=true });
                var packet=parser.Feed(buffer.AsSpan(0,count));
                if(packet.Reply.Length>0) await stream.WriteAsync(packet.Reply,deadline.Token);
                foreach(var data in packet.DataMessages)
                {
                    if(++messages>1024) return new("limited",collector.Snapshot() with { Limited=true });
                    collector.Observe(data.Option,data.Payload,data.MayContainPrivateText);
                }
            }
        }
        catch(OperationCanceledException) when(!token.IsCancellationRequested) { }
        catch(Exception ex) when(ex is IOException or SocketException or System.Security.Authentication.AuthenticationException) { return new("connection_failed",collector.Snapshot()); }
        token.ThrowIfCancellationRequested();
        var evidence=collector.Snapshot();
        return new(evidence.Limited ? "limited" : evidence.Fields.Length>0 ? "observed" : connected ? "no_data" : "timeout",evidence);
    }
    public static bool IsPublic(IPAddress address)
    {
        if(address.IsIPv4MappedToIPv6) address=address.MapToIPv4();
        if(IPAddress.IsLoopback(address)) return false;
        var b=address.GetAddressBytes();
        if(b.Length==16) return (b[0]&0xe0)==0x20 && !(b[0]==0x20&&b[1]==0x01&&b[2]==0x0d&&b[3]==0xb8);
        return b[0] is >0 and <224 && b[0]!=10 && b[0]!=127 && !(b[0]==169&&b[1]==254)
            && !(b[0]==172&&b[1] is >=16 and <=31) && !(b[0]==192&&b[1]==168)
            && !(b[0]==100&&b[1] is >=64 and <=127) && !(b[0]==198&&b[1] is 18 or 19)
            && !(b[0]==192&&b[1]==0) && !(b[0]==198&&b[1]==51&&b[2]==100) && !(b[0]==203&&b[1]==0&&b[2]==113);
    }
}
