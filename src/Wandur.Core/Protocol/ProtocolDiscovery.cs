using System.Text;
using System.Text.Json;
using Wandur.Core.Mapping;

namespace Wandur.Core.Protocol;

/// <summary>Discovers data subscriptions without interpreting game-specific field meanings.</summary>
internal sealed class ProtocolDiscovery
{
    // Data modules plus the implemented password login flow. Do not promise media or web views.
    public const string GmcpSupports = "Core.Supports.Set [\"Room 1\",\"Char 1\",\"Char.Base 1\",\"Char.Vitals 1\",\"Char.Maxstats 1\",\"Char.Status 1\",\"Char.Login 1\",\"Char.Skills 1\",\"Char.Items 1\",\"Char.Afflictions 1\",\"Char.Defences 1\",\"Group 1\",\"Comm 1\",\"Comm.Channel 1\",\"MSDP 1\"]";
    public const string GmcpDiscovery = "MSDP {\"LIST\":\"REPORTABLE_VARIABLES\"}";
    private const int MaximumReports = 256;
    private const int ReportsPerBatch = 32;
    private readonly HashSet<string> _native = new(StringComparer.Ordinal);
    private readonly HashSet<string> _tunneled = new(StringComparer.Ordinal);

    public void Reset(byte option) { if (option == 69) _native.Clear(); if (option == 201) _tunneled.Clear(); }

    public IEnumerable<byte[]> Receive(byte option, byte[] payload)
    {
        var seen = option == 69 ? _native : _tunneled;
        var requested = new List<string>();
        foreach (var name in ReportableNames(option, payload))
        {
            if (seen.Count >= MaximumReports) break;
            if (ValidName(name) && seen.Add(name)) requested.Add(name);
        }
        foreach (var batch in requested.Chunk(ReportsPerBatch))
            yield return Encoding.UTF8.GetBytes(option == 69
                ? "\u0001REPORT" + string.Concat(batch.Select(name => "\u0002" + name))
                : "MSDP " + JsonSerializer.Serialize(new Dictionary<string, string[]> { ["REPORT"] = batch }));
    }

    private static bool ValidName(string name) => name.Length is > 0 and <= 128 && !char.IsAsciiDigit(name[0]) &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    private static IEnumerable<string> ReportableNames(byte option, byte[] payload)
    {
        if (payload.Length > ProtocolDiagnosticFormatter.MaximumPayloadBytes) return [];
        if (option == 69)
        {
            var fields = RoomProtocolDecoder.ParseMsdp(payload);
            if (fields is null || !fields.TryGetValue("REPORTABLE_VARIABLES", out var value)) return [];
            return value is List<object> array ? array.OfType<string>() : value is string text ? [text] : [];
        }
        var message = Encoding.UTF8.GetString(payload);
        var separator = message.IndexOfAny([' ', '\t', '\r', '\n']);
        if (separator < 0 || message[..separator] != "MSDP") return [];
        try
        {
            using var document = JsonDocument.Parse(message[(separator + 1)..], new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("REPORTABLE_VARIABLES", out var value)) return [];
            return value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray().Take(MaximumReports).Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToArray()
                : value.ValueKind == JsonValueKind.String ? [value.GetString()!] : [];
        }
        catch (JsonException) { return []; }
    }
}
