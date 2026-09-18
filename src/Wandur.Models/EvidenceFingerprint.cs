using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Wandur.Models;

public static class EvidenceFingerprint
{
    public static string Compute(ProtocolEvidence evidence)
    {
        var fields = evidence.Fields.GroupBy(f=>f.Source).OrderBy(g=>g.Key.Protocol,StringComparer.Ordinal)
            .ThenBy(g=>g.Key.Package,StringComparer.Ordinal).ThenBy(g=>g.Key.Path,StringComparer.Ordinal)
            .Select(g=>new { source=g.Key, types=g.SelectMany(f=>f.Types).Distinct().Order(StringComparer.Ordinal).ToArray(), advertised=g.Any(f=>f.Advertised) });
        var canonical = JsonSerializer.Serialize(new { schema_version=1, limited=evidence.Limited, fields },ModelJson.Options);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
