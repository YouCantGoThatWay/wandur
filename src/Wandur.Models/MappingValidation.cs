using System.Text.RegularExpressions;

namespace Wandur.Models;

public static class MappingValidation
{
    public const int MaximumBindings = 256;
    public static bool Slug(string? value) => value is { Length: > 0 and <= 64 }
        && Regex.IsMatch(value, "^[a-z][a-z0-9_-]*$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    public static bool Text(string? value, int max) => value is not null && value.Length <= max && !value.Any(char.IsControl);
    public static bool SafeField(ProtocolFieldReference? source) => source is not null && source.Protocol is "MSDP" or "GMCP"
        && source.Package is { Length: > 0 and <= 128 } && source.Package.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.')
        && (source.Protocol != "MSDP" || source.Package == "MSDP") && Pointer(source.Path)
        && !Sensitive(source.Package) && !Sensitive(source.Path);
    public static bool Sensitive(string value) => new[] { "password", "passwd", "secret", "token", "credential", "login", "auth", "chat", "channel" }
        .Any(word => value.Contains(word, StringComparison.OrdinalIgnoreCase));
    public static bool Pointer(string? value)
    {
        if (!Text(value,512) || value!.Length > 0 && value[0] != '/') return false;
        for (var i=0; i<value.Length; i++)
            if (value[i]=='~' && (++i==value.Length || value[i] is not ('0' or '1'))) return false;
        return true;
    }
    public static bool Endpoint(WorldEndpoint? endpoint) => endpoint is not null && Text(endpoint.Host,253)
        && Uri.CheckHostName(endpoint.NormalizedHost) != UriHostNameType.Unknown && endpoint.Port is > 0 and <= 65535;
    public static bool Binding(FieldBinding? binding)
    {
        if (binding is null || !SafeField(binding.Source) || binding.Target is not { } target || !Slug(target.Key)
            || target.Entity is not ("character" or "opponent" or "vehicle" or "world") || !Text(binding.Label,80) || string.IsNullOrWhiteSpace(binding.Label)
            || !double.IsFinite(binding.Scale) || binding.Scale is <= 0 or > 1_000_000) return false;
        return target.Category switch
        {
            "identity" or "location" => target.Member == "value" && binding.Conversion == "text" && binding.Scale == 1,
            "resource" or "progression" => target.Member is "current" or "maximum" && binding.Conversion == "number",
            "attribute" => target.Member is "current" or "base" && binding.Conversion == "number",
            "currency" => target.Member is "carried" or "bank" or "total" && binding.Conversion == "number",
            "metric" => target.Member == "value" && binding.Conversion is "number" or "text" or "boolean" && (binding.Conversion=="number" || binding.Scale==1),
            _ => false
        };
    }
    public static bool IsValid(WorldMapping? mapping, ProtocolEvidence? evidence = null)
    {
        if (mapping is null || mapping.SchemaVersion != 1 || !Text(mapping.WorldId,256) || string.IsNullOrWhiteSpace(mapping.WorldId)
            || !Endpoint(mapping.Endpoint) || mapping.SchemaFingerprint is not { Length: 64 }
            || !mapping.SchemaFingerprint.All(c=>char.IsAsciiDigit(c) || c is >= 'a' and <= 'f') || mapping.Revision < 1 || mapping.GeneratedAt == default
            || mapping.Provenance is not ("deterministic" or "azure") || mapping.Bindings is null || mapping.Bindings.Length > MaximumBindings
            || mapping.Bindings.Any(b=>!Binding(b)) || mapping.Bindings.Select(b=>b.Target).Distinct().Count()!=mapping.Bindings.Length) return false;
        return evidence is null || evidence.Fields is not null && evidence.Fields.All(f=>f is not null && SafeField(f.Source) && f.Types is not null) && mapping.Bindings.All(b=>evidence.Fields.Any(f=>f.Source == b.Source));
    }
}
