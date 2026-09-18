using System.Text.Json;
using Wandur.Core.Protocol;
using Wandur.Models;

namespace Wandur.Discovery.Worker;

/// <summary>Collects bounded field structure. The only retained scalar value is public server identity.</summary>
public sealed class EvidenceCollector
{
    private readonly Dictionary<ProtocolFieldReference,ObservedField> _fields=[];
    private readonly HashSet<string> _completeLists=[];
    private bool _limited;
    private string? _serverId;
    public void Observe(byte option, byte[] payload, bool privateInput = false)
    {
        if (option is not (69 or 201)) return;
        var content=ProtocolDiagnosticFormatter.Format(option,payload,privateInput);
        if (content.Truncated) _limited=true;
        if (content.Malformed || content.Truncated || content.Redacted || content.Body.Length==0) return;
        var protocol=option==69 ? "MSDP" : "GMCP";
        var package=option==69 ? "MSDP" : content.Name;
        if (!MappingValidation.SafeField(new(protocol,package,""))) return;
        try
        {
            using var doc=JsonDocument.Parse(content.Body,new JsonDocumentOptions { MaxDepth=16 });
            if (package=="MSDP" && doc.RootElement.ValueKind==JsonValueKind.Object)
            {
                foreach(var property in doc.RootElement.EnumerateObject())
                {
                    if (property.Name=="REPORTABLE_VARIABLES" && property.Value.ValueKind==JsonValueKind.Array)
                    {
                        var names=property.Value.EnumerateArray().ToArray();
                        if(names.Length>256) _limited=true;
                        else if(names.Any(v=>v.ValueKind==JsonValueKind.String && !string.IsNullOrEmpty(v.GetString()))) _completeLists.Add(protocol+":MSDP");
                        foreach(var name in names.Take(256))
                            if(name.ValueKind==JsonValueKind.String && name.GetString() is { Length:>0 and <=128 } text && text.All(c=>char.IsAsciiLetterOrDigit(c)||c=='_'))
                                Add(new(protocol,package,"/"+text),"unknown",true);
                    }
                    else if(property.Name is "SERVERID" or "SERVER_ID" && property.Value.ValueKind==JsonValueKind.String)
                    {
                        var id=property.Value.GetString();
                        if(MappingValidation.Text(id,128)) _serverId=id;
                    }
                }
            }
            Walk(protocol,package,"",doc.RootElement,0);
        }
        catch(JsonException) { _limited=true; }
    }
    private void Walk(string protocol,string package,string path,JsonElement value,int depth)
    {
        if(depth>5) { _limited=true; return; }
        if(value.ValueKind==JsonValueKind.Object)
        {
            foreach(var p in value.EnumerateObject())
            {
                if(p.Name is "REPORTABLE_VARIABLES" or "REPORTED_VARIABLES" or "CONFIGURABLE_VARIABLES" or "COMMANDS" or "LISTS") continue;
                if(p.Name.Length is 0 or >64 || !p.Name.All(c=>char.IsAsciiLetterOrDigit(c)||c is '_' or '.')) continue;
                var next=path+"/"+p.Name;
                if(!MappingValidation.SafeField(new(protocol,package,next))) continue;
                Walk(protocol,package,next,p.Value,depth+1);
            }
        }
        else Add(new(protocol,package,path),value.ValueKind switch
        {
            JsonValueKind.Number=>"number",JsonValueKind.String=>"string",JsonValueKind.True or JsonValueKind.False=>"boolean",
            JsonValueKind.Array=>"array",_=>"null"
        },false);
    }
    private void Add(ProtocolFieldReference source,string type,bool advertised)
    {
        if(!MappingValidation.SafeField(source)) return;
        if(!_fields.ContainsKey(source) && _fields.Count>=1024) { _limited=true; return; }
        _fields.TryGetValue(source,out var previous);
        var types=(previous?.Types??[]).Append(type).Distinct().ToArray();
        if(types.Length>1) types=types.Where(t=>t!="unknown").ToArray();
        _fields[source]=new(source,types.Order(StringComparer.Ordinal).ToArray(),advertised || previous?.Advertised==true);
    }
    public ProtocolEvidence Snapshot() => new() { Fields=_fields.Values.ToArray(),Limited=_limited,ServerId=_serverId,AdvertisedProtocols=_completeLists.Order(StringComparer.Ordinal).ToArray() };

    public static ProtocolEvidence Merge(ProtocolEvidence previous,ProtocolEvidence current)
    {
        // A complete MSDP list can retire absent names. Ordinary GMCP updates cannot.
        var old=previous.Fields.Where(f=>!current.AdvertisedProtocols.Contains(f.Source.Protocol+":"+f.Source.Package)
            || current.Fields.Any(n=>n.Source==f.Source));
        var fields=old.Concat(current.Fields).GroupBy(f=>f.Source).Select(g=>
        {
            var types=g.SelectMany(f=>f.Types).Distinct().ToArray();
            if(types.Length>1) types=types.Where(t=>t!="unknown").ToArray();
            return new ObservedField(g.Key,types.Order(StringComparer.Ordinal).ToArray(),g.Any(f=>f.Advertised));
        }).ToArray();
        return current with { Fields=fields.Take(1024).ToArray(),Limited=current.Limited || fields.Length>1024,ServerId=current.ServerId??previous.ServerId };
    }
}
