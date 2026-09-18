using System.Text;
using System.Text.Json;

namespace Wandur.Core.Protocol;

/// <summary>Redacts diagnostic copies only. The transport and automation privacy decisions stay independent.</summary>
internal static class ProtocolDiagnosticRedactor
{
    private const string Hidden = "[redacted]";
    private static bool Sensitive(string name) => new string(name.Where(char.IsAsciiLetterOrDigit).ToArray()).ToLowerInvariant() is
        "password" or "passwd" or "pwd" or "pass" or "passphrase" or "passcode" or "secret" or "clientsecret" or
        "token" or "accesstoken" or "refreshtoken" or "idtoken" or "authtoken" or "sessiontoken" or "sessionid" or
        "apikey" or "authorization" or "credentials" or "credential" or "privatekey" or "cookie" or "setcookie";

    public static (string Name, string Body, bool Redacted) Apply(byte option, string name, string body,
        bool malformed, bool truncated, bool privateInput, IReadOnlyList<string>? secrets)
    {
        var redacted = false;
        var known = secrets?.Where(s => !string.IsNullOrEmpty(s)).Take(8).OrderByDescending(s => s.Length).ToArray() ?? [];
        string Scrub(string text)
        {
            foreach (var secret in known)
                if (text.Contains(secret, StringComparison.Ordinal)) { text = text.Replace(secret, Hidden, StringComparison.Ordinal); redacted = true; }
            return text;
        }
        var privatePackage = option == 201 && ((GmcpLoginProtocol.IsPrivate(name) &&
            !name.Equals("Char.Login.Default", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("Char.Login.Result", StringComparison.OrdinalIgnoreCase)) || name.Split('.').Any(Sensitive));
        name = Scrub(name);
        if (privatePackage || (privateInput && (malformed || truncated))) return (name, Hidden, true);
        if (body.Length == 0) return (name, body, redacted);
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                void Write(JsonElement value)
                {
                    switch (value.ValueKind)
                    {
                        case JsonValueKind.Object:
                            writer.WriteStartObject();
                            foreach (var property in value.EnumerateObject())
                            {
                                writer.WritePropertyName(Scrub(property.Name));
                                if (Sensitive(property.Name)) { writer.WriteStringValue(Hidden); redacted = true; }
                                else Write(property.Value);
                            }
                            writer.WriteEndObject(); break;
                        case JsonValueKind.Array:
                            writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) Write(item); writer.WriteEndArray(); break;
                        case JsonValueKind.String: writer.WriteStringValue(Scrub(value.GetString()!)); break;
                        default: value.WriteTo(writer); break;
                    }
                }
                Write(document.RootElement);
            }
            body = Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (JsonException) { body = Scrub(body); }
        return (name, body, redacted);
    }
}
