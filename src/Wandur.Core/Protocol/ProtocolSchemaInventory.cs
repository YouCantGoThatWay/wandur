using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Wandur.Core.Protocol;

/// <summary>A bounded union of observed paths and wire types, never their values. Session-local, not a complete server schema.</summary>
public sealed class ProtocolSchemaInventory
{
    public const int MaximumFields = 2048;
    private const int MaximumPathLength = 512;
    private readonly Dictionary<(string Protocol, string Package, string Path), SortedSet<string>> _fields = [];
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
    private string? _snapshot, _fingerprint, _fieldsText;
    public int FieldCount => _fields.Count;
    public bool Limited { get; private set; }
    public string Snapshot { get { Refresh(); return _snapshot!; } }
    public string Fingerprint { get { Refresh(); return _fingerprint!; } }
    public string FieldsText { get { Refresh(); return _fieldsText!; } }
    public int Revision { get; private set; }

    public void Observe(byte option, ProtocolDiagnosticContent content)
    {
        if (option is not (69 or 201) || content.Malformed || content.Truncated || content.Redacted || GmcpLoginProtocol.IsPrivate(content.Name)) return;
        var protocol = option == 69 ? "MSDP" : "GMCP";
        var package = option == 69 ? "MSDP" : content.Name;
        if (package.Length is 0 or > 128 || !package.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_')) return;
        if (content.Body.Length == 0) { Add(protocol, package, "", "empty"); return; }
        try
        {
            using var document = JsonDocument.Parse(content.Body, new JsonDocumentOptions { MaxDepth = 32 });
            Walk(protocol, package, "", document.RootElement, 0);
        }
        catch (JsonException) { /* Malformed packets remain in history, not in the inferred schema. */ }
    }

    private void Walk(string protocol, string package, string path, JsonElement value, int depth)
    {
        if (depth > 16 || path.Length > MaximumPathLength) { MarkLimited(); return; }
        var type = value.ValueKind switch
        {
            JsonValueKind.Object => "object", JsonValueKind.Array => "array",
            JsonValueKind.String => "string", JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "boolean", _ => "null"
        };
        if (!Add(protocol, package, path, type)) return;
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
                Walk(protocol, package, path + "/" + Escape(property.Name), property.Value, depth + 1);
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var element in value.EnumerateArray()) Walk(protocol, package, path + "/*", element, depth + 1);
    }

    // JSON Pointer escaping with ~2 for a literal *, keeping array wildcards unambiguous.
    private static string Escape(string value) => value.Replace("~", "~0").Replace("/", "~1").Replace("*", "~2");
    private bool Add(string protocol, string package, string path, string type)
    {
        var key = (protocol, package, path);
        if (!_fields.TryGetValue(key, out var types))
        {
            if (_fields.Count >= MaximumFields) { MarkLimited(); return false; }
            _fields.Add(key, types = new(StringComparer.Ordinal));
        }
        if (types.Add(type)) Changed();
        return true;
    }
    private void MarkLimited() { if (Limited) return; Limited = true; Changed(); }
    private void Changed() { _snapshot = null; Revision++; }
    public void Clear() { _fields.Clear(); Limited = false; Changed(); }

    private void Refresh()
    {
        if (_snapshot is not null) return;
        var fields = _fields.OrderBy(e => e.Key.Protocol, StringComparer.Ordinal)
            .ThenBy(e => e.Key.Package, StringComparer.Ordinal).ThenBy(e => e.Key.Path, StringComparer.Ordinal)
            .Select(e => new { protocol = e.Key.Protocol, package = e.Key.Package, path = e.Key.Path, types = e.Value.ToArray() }).ToArray();
        // Fixed property ordering and ordinal sorting make observations independent of packet/field ordering.
        var canonical = JsonSerializer.Serialize(new { schemaVersion = 1, limited = Limited, fields });
        _fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        _fieldsText = JsonSerializer.Serialize(fields, Pretty);
        _snapshot = JsonSerializer.Serialize(new { schemaVersion = 1, limited = Limited, fingerprint = _fingerprint, fields }, Pretty);
    }
}
