using System.Text;
using System.Text.Json;
using Wandur.Core.Mapping;

namespace Wandur.Core.Protocol;

public sealed record ProtocolDiagnosticContent(string Name, string Body, bool Malformed, bool Truncated)
{
    public bool Redacted { get; init; }
}

/// <summary>Formats received protocol data for inspection independently of supported mapper fields.</summary>
public static class ProtocolDiagnosticFormatter
{
    public const int MaximumPayloadBytes = 16384;
    public const int MaximumBodyCharacters = 32768;
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, MaxDepth = 32 };

    public static ProtocolDiagnosticContent Format(byte option, byte[] payload, bool privateInput = false, IReadOnlyList<string>? secrets = null)
    {
        var truncated = payload.Length > MaximumPayloadBytes;
        if (truncated) payload = payload[..MaximumPayloadBytes];
        string name, body;
        var malformed = false;
        if (option == 69)
        {
            var fields = RoomProtocolDecoder.ParseMsdp(payload);
            name = fields is null ? "MSDP" : string.Join(", ", fields.Keys.Take(3));
            body = fields is null ? Convert.ToHexString(payload) : JsonSerializer.Serialize(fields, Pretty);
            malformed = fields is null;
        }
        else
        {
            var text = Encoding.UTF8.GetString(payload);
            var separator = text.IndexOfAny([' ', '\t', '\r', '\n']);
            name = separator < 0 ? text : text[..separator];
            var data = separator < 0 ? "" : text[(separator + 1)..].Trim();
            if (data.Length == 0) body = "";
            else
            {
                try
                {
                    using var document = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 32 });
                    body = JsonSerializer.Serialize(document.RootElement, Pretty);
                }
                catch (JsonException) { body = JsonSerializer.Serialize(data); malformed = true; }
            }
        }
        var safe = ProtocolDiagnosticRedactor.Apply(option, name, body, malformed, truncated, privateInput, secrets);
        name = safe.Name; body = safe.Body;
        truncated |= name.Length > 128;
        name = new string(name.Take(128).Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        truncated |= body.Length > MaximumBodyCharacters;
        if (body.Length > MaximumBodyCharacters) body = body[..MaximumBodyCharacters];
        return new(name, body, malformed, truncated) { Redacted = safe.Redacted };
    }
}
