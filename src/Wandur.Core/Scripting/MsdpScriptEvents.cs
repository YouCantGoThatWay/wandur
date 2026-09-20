using System.Buffers;
using System.Text;
using System.Text.Json;
using Wandur.Core.Mapping;

namespace Wandur.Core.Scripting;

/// <summary>Turns one received MSDP payload into the per-variable script events scripts subscribe to.
/// Decoding reuses the protocol library's reader, so scripts see exactly what the mapper sees.</summary>
public static class MsdpScriptEvents
{
    public const int MaximumVariables = 64;
    public const int MaximumValueCharacters = 8192;
    private static readonly JsonSerializerOptions Values = new() { MaxDepth = 16 };

    /// <summary>Decoded variable updates, or an empty list when the payload is not valid MSDP.
    /// Callers must already have cleared the payload for privacy.</summary>
    public static IReadOnlyList<ScriptEvent> Decode(byte[] payload)
        => DecodeValues(payload).Select(entry => new ScriptEvent("msdp", Message(entry.Variable, entry.Json))).ToArray();

    /// <summary>The variables of one payload with their values serialized as JSON, in wire order.
    /// This is what both the script events and the host state cache are built from.</summary>
    public static IReadOnlyList<(string Variable, string Json)> DecodeValues(byte[] payload)
    {
        var fields = RoomProtocolDecoder.ParseMsdp(payload);
        if (fields is null) return [];
        var values = new List<(string Variable, string Json)>();
        foreach (var (variable, value) in fields)
        {
            if (values.Count >= MaximumVariables) break;
            if (variable.Length is 0 or > 128 || variable.Any(char.IsControl)) continue;
            string json;
            try { json = JsonSerializer.Serialize(value, Values); }
            catch (Exception error) when (error is JsonException or NotSupportedException) { continue; }
            if (json.Length > MaximumValueCharacters) continue;
            values.Add((variable, json));
        }
        return values;
    }

    /// <summary>One script event for a variable whose value is already serialized, as the host cache keeps it.</summary>
    public static ScriptEvent Event(string variable, string json) => new("msdp", Message(variable, json));

    /// <summary>The variable named by an msdp event's text, or null when the text is not one of ours.</summary>
    public static string? VariableOf(string text)
    {
        if (text.Length > 64 * 1024) return null;
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("variable", out var variable) && variable.ValueKind == JsonValueKind.String
                ? variable.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static string Message(string variable, string value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("variable", variable);
            writer.WritePropertyName("value");
            writer.WriteRawValue(value, skipInputValidation: false);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
