using System.Text.Json;

namespace Wandur.Core.Protocol;

public enum GmcpLoginKind { Offer, Result }
public sealed record GmcpLoginMessage(GmcpLoginKind Kind, bool PasswordSupported = false, bool Success = false);

/// <summary>Version 1 password login metadata. Never forwards credentials, tokens, URLs or server messages.</summary>
public static class GmcpLoginProtocol
{
    public static bool IsPrivate(string message)
    {
        var separator = message.IndexOfAny([' ', '\t', '\r', '\n']);
        var package = separator < 0 ? message : message[..separator];
        return package.StartsWith("Char.Login.", StringComparison.OrdinalIgnoreCase);
    }

    public static GmcpLoginMessage? Decode(string message)
    {
        if (message.Length > ProtocolDiagnosticFormatter.MaximumPayloadBytes) return null;
        var separator = message.IndexOfAny([' ', '\t', '\r', '\n']);
        if (separator < 0) return null;
        var package = message[..separator];
        if (!package.Equals("Char.Login.Default", StringComparison.OrdinalIgnoreCase) &&
            !package.Equals("Char.Login.Result", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            using var document = JsonDocument.Parse(message[(separator + 1)..], new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (package.Equals("Char.Login.Default", StringComparison.OrdinalIgnoreCase))
            {
                if (!root.TryGetProperty("type", out var types) || types.ValueKind != JsonValueKind.Array) return null;
                var versionOne = !root.TryGetProperty("version", out var version) ||
                    (version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out var number) && number == 1) ||
                    (version.ValueKind == JsonValueKind.String && version.GetString() == "1");
                return new(GmcpLoginKind.Offer, versionOne && types.EnumerateArray().Any(t =>
                    t.ValueKind == JsonValueKind.String && t.GetString() == "password-credentials"));
            }
            if (!root.TryGetProperty("success", out var success)) return null;
            bool? value = success.ValueKind switch
            {
                JsonValueKind.True => true, JsonValueKind.False => false,
                JsonValueKind.String when bool.TryParse(success.GetString(), out var result) => result,
                JsonValueKind.Number when success.TryGetInt32(out var result) && result is 0 or 1 => result == 1,
                _ => null
            };
            return value.HasValue ? new(GmcpLoginKind.Result, Success: value.Value) : null;
        }
        catch (JsonException) { return null; }
    }
}
