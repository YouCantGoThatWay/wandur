using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Wandur.Core.Classification;

/// <summary>Reproduces the room-classifier package's preprocessing_spec.json exactly. Any change breaks model parity.</summary>
public static partial class RoomTextPreprocessor
{
    [GeneratedRegex(@"\x1b\[[0-9;]*m")] private static partial Regex Ansi();
    [GeneratedRegex(@"&[a-zA-Z0-9]")] private static partial Regex Smaug();
    [GeneratedRegex(@"@[a-zA-Z0-9]")] private static partial Regex Tba();
    [GeneratedRegex(@"\{[a-zA-Z]")] private static partial Regex Rom();
    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();

    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var value = Ansi().Replace(text, "");
        value = Smaug().Replace(value, "");
        value = Tba().Replace(value, "");
        value = Rom().Replace(value, "");
        value = value.Replace("~", "");
        return Whitespace().Replace(value, " ").Trim();
    }

    public static string BuildText(string name, string description) => Clean(name) + "\n" + Clean(description);

    public static string InferenceKey(string name, string description, string modelVersion) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(modelVersion + "\n" + BuildText(name, description))));
}
