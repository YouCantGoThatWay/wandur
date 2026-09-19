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
    {
        var fields = RoomProtocolDecoder.ParseMsdp(payload);
        if (fields is null) return [];
        var events = new List<ScriptEvent>();
        foreach (var (variable, value) in fields)
        {
            if (events.Count >= MaximumVariables) break;
            if (variable.Length is 0 or > 128 || variable.Any(char.IsControl)) continue;
            string json;
            try { json = JsonSerializer.Serialize(value, Values); }
            catch (Exception error) when (error is JsonException or NotSupportedException) { continue; }
            if (json.Length > MaximumValueCharacters) continue;
            events.Add(new("msdp", Message(variable, json)));
        }
        return events;
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
