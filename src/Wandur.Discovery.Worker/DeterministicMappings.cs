using Wandur.Models;
namespace Wandur.Discovery.Worker;

/// <summary>Conservative aliases for common MSDP and Char.Vitals fields. Unrecognized names stay unmapped.</summary>
public static class DeterministicMappings
{
    public const string Revision="aliases-1";
    public static FieldBinding[] Create(ProtocolEvidence evidence)
    {
        var result=new Dictionary<MappingTarget,FieldBinding>();
        foreach(var field in evidence.Fields.OrderBy(f=>f.Source.Protocol=="MSDP"?0:1).ThenBy(f=>f.Source.Path,StringComparer.Ordinal))
        {
            if(field.Types.Any(t=>t is "array" or "object") || !MappingValidation.SafeField(field.Source)) continue;
            var source=field.Source;
            var name=source.Path.TrimStart('/').Replace("_","").ToUpperInvariant();
            if(source.Package!="MSDP")
            {
                if(source.Package!="Char.Vitals") continue;
                name=name switch { "HP"=>"HEALTH","MAXHP"=>"HEALTHMAX","MP"=>"MANA","MAXMP"=>"MANAMAX","MV"=>"MOVEMENT","MAXMV"=>"MOVEMENTMAX",_=>name };
            }
            string entity="character",category="resource",key="",member="current",label="",conversion="number";
            switch(name)
            {
                case "CHARACTERNAME": category="identity"; key="name"; label="Name"; conversion="text"; member="value"; break;
                case "RACE": case "CLASS": case "CLAN": category="identity"; key=name=="CLAN"?"faction":name.ToLowerInvariant(); label=key; conversion="text"; member="value"; break;
                case "HEALTH": case "HEALTHMAX": key="health"; label="HP"; member=name.EndsWith("MAX")?"maximum":"current"; break;
                case "MANA": case "MANAMAX": key="mana"; label="Mana"; member=name.EndsWith("MAX")?"maximum":"current"; break;
                case "MOVEMENT": case "MOVEMENTMAX": key="movement"; label="Movement"; member=name.EndsWith("MAX")?"maximum":"current"; break;
                case "CURRENTAMMO": case "MAXAMMO": key="ammunition"; label="Ammunition"; member=name=="MAXAMMO"?"maximum":"current"; break;
                case "OPPONENTNAME": entity="opponent";category="identity";key="name";label="Opponent";conversion="text";member="value";break;
                case "OPPONENTHEALTH": case "OPPONENTHEALTHMAX": entity="opponent";key="health";label="HP";member=name.EndsWith("MAX")?"maximum":"current";break;
                case "TOPLEVEL": case "LEVEL": category="progression";key="level";label="Level";break;
                case "EXPERIENCE": case "EXPERIENCEMAX": category="progression";key="experience";label="Experience";member=name.EndsWith("MAX")?"maximum":"current";break;
                case "MONEYTOTAL": case "MONEYINV": case "MONEYBANK": category="currency";key="money";label="Money";member=name=="MONEYTOTAL"?"total":name=="MONEYBANK"?"bank":"carried";break;
                case "ROOMVNUM": case "ROOMNAME": case "PLANET": category="location";key=name=="ROOMVNUM"?"id":name=="ROOMNAME"?"name":"area";label=key;conversion="text";member="value";break;
                default:
                    if(name.StartsWith("SHIP"))
                    {
                        var metric=name[4..]; member=metric.StartsWith("MAX")?"maximum":"current";metric=metric.Replace("MAX","");
                        if(metric is not ("HULL" or "ENERGY" or "SHIELD" or "SPEED")) continue;
                        entity="vehicle";key=metric.ToLowerInvariant();label=key;
                    }
                    else if(name.StartsWith("LEVEL") && name.Length>5) { category="progression";key=name[5..].ToLowerInvariant();label=key; }
                    else if(new[]{"STR","INT","WIS","DEX","CON","CHA"}.Any(s=>name==s||name==s+"PERM"))
                    {
                        category="attribute";member=name.EndsWith("PERM")?"base":"current";
                        key=name[..3] switch { "STR"=>"strength","INT"=>"intelligence","WIS"=>"wisdom","DEX"=>"dexterity","CON"=>"constitution",_=>"charisma" };label=key;
                    }
                    else continue;
                    break;
            }
            var binding=new FieldBinding { Source=source,Target=new(entity,category,key,member),Label=label,Conversion=conversion };
            if(MappingValidation.Binding(binding)) result.TryAdd(binding.Target,binding);
        }
        return result.Values.Take(MappingValidation.MaximumBindings).ToArray();
    }
}
