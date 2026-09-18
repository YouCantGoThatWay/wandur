using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wandur.Core.Discovery;

/// <summary>Optional, data-only world appearance. Unsupported themes never invalidate a listing.</summary>
[JsonConverter(typeof(WorldThemeConverter))]
public sealed record WorldTheme
{
    public int Version { get; init; } = 1;
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Variant { get; init; } = "dark";
    public double CornerRadius { get; init; } = 4;
    public WorldThemeColors Colors { get; init; } = new();
    public string Surface { get; init; } = "standard";
    public WorldThemeImages Images { get; init; } = new();
    public bool IsValid => Version == 1 && Id is { Length: > 0 and <= 80 } &&
        Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') &&
        Name is { Length: > 0 and <= 100 } && !string.IsNullOrWhiteSpace(Name) && !Name.Any(char.IsControl) && Variant is "dark" or "light" &&
        double.IsFinite(CornerRadius) && CornerRadius is >= 0 and <= 16 && Colors is not null &&
        Colors.Values.All(IsColor);
    private static bool IsColor(string? value) => value is { Length: 7 } && value[0] == '#' && value.Skip(1).All(char.IsAsciiHexDigit);
}

public sealed record WorldThemeImages
{
    public WorldThemeImage? Chrome { get; init; }
    public WorldThemeImage? Shell { get; init; }
}

/// <summary>Optional tiled PNG material, composited over the palette rather than the transcript.</summary>
public sealed record WorldThemeImage
{
    public string Url { get; init; } = "";
    public double Opacity { get; init; } = .12;
    [JsonIgnore] public bool IsValid => Url is { Length: > 0 and <= 2048 } && !Url.Any(char.IsControl) &&
        !Url.Contains('\\') && !Url.StartsWith('/') && double.IsFinite(Opacity) && Opacity is >= 0 and <= .35 &&
        (Uri.TryCreate(Url, UriKind.Absolute, out var absolute)
            ? absolute.Scheme == "https" && absolute.UserInfo.Length == 0
            : Uri.TryCreate(Url, UriKind.Relative, out _) && !Url.Split('/').Any(p => p is "." or ".."));
}

public sealed record WorldThemeColors
{
    public string Shell { get; init; } = "";
    public string Panel { get; init; } = "";
    public string Terminal { get; init; } = "";
    public string Text { get; init; } = "";
    public string Muted { get; init; } = "";
    public string Accent { get; init; } = "";
    public string AccentSecondary { get; init; } = "";
    public string Border { get; init; } = "";
    public string TerminalText { get; init; } = "";
    [JsonIgnore] public IEnumerable<string> Values => [Shell, Panel, Terminal, Text, Muted, Accent, AccentSecondary, Border, TerminalText];
}

public sealed class WorldThemeConverter : JsonConverter<WorldTheme>
{
    private static readonly JsonSerializerOptions WireOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    public override WorldTheme? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        try
        {
            var theme = new WorldTheme
            {
                Version = root.GetProperty("version").GetInt32(), Id = root.GetProperty("id").GetString() ?? "",
                Name = root.GetProperty("name").GetString() ?? "", Variant = root.GetProperty("variant").GetString() ?? "",
                CornerRadius = root.GetProperty("corner_radius").GetDouble(),
                Colors = root.GetProperty("colors").Deserialize<WorldThemeColors>(WireOptions) ?? new(),
                Surface = root.TryGetProperty("surface", out var surface) && surface.ValueKind == JsonValueKind.String && surface.GetString() == "metallic" ? "metallic" : "standard",
                Images = ReadImages(root)
            };
            return theme.IsValid ? theme : null;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException) { return null; }
    }
    private static WorldThemeImages ReadImages(JsonElement root)
    {
        if (!root.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Object) return new();
        WorldThemeImage? Read(string key)
        {
            if (!images.TryGetProperty(key, out var value)) return null;
            try { var image = value.Deserialize<WorldThemeImage>(WireOptions); return image is { IsValid: true } ? image : null; }
            catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { return null; }
        }
        return new() { Chrome = Read("chrome"), Shell = Read("shell") };
    }
    public override void Write(Utf8JsonWriter writer, WorldTheme value, JsonSerializerOptions options)
    {
        if (!value.IsValid) { writer.WriteNullValue(); return; }
        writer.WriteStartObject();
        writer.WriteNumber("version", value.Version); writer.WriteString("id", value.Id); writer.WriteString("name", value.Name);
        writer.WriteString("variant", value.Variant); writer.WriteNumber("corner_radius", value.CornerRadius);
        writer.WritePropertyName("colors"); JsonSerializer.Serialize(writer, value.Colors, WireOptions);
        if (value.Surface == "metallic") writer.WriteString("surface", value.Surface);
        if (value.Images is { } images && (images.Chrome is { IsValid: true } || images.Shell is { IsValid: true }))
        {
            writer.WritePropertyName("images"); writer.WriteStartObject();
            if (images.Chrome is { IsValid: true } chrome) { writer.WritePropertyName("chrome"); JsonSerializer.Serialize(writer, chrome, WireOptions); }
            if (images.Shell is { IsValid: true } shell) { writer.WritePropertyName("shell"); JsonSerializer.Serialize(writer, shell, WireOptions); }
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }
}
