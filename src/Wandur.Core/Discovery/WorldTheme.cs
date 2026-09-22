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
    /// <summary>Optional window chrome. Missing, malformed or unknown kinds are ignored; the palette still applies.</summary>
    public WorldThemeFrame? Frame { get; init; }
    /// <summary>Optional modular skin. Missing, malformed or unknown versions are ignored; the palette still applies.</summary>
    public WorldThemeSkin? Skin { get; init; }
    public bool IsValid => Version == 1 && Id is { Length: > 0 and <= 80 } &&
        Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') &&
        Name is { Length: > 0 and <= 100 } && !string.IsNullOrWhiteSpace(Name) && !Name.Any(char.IsControl) && Variant is "dark" or "light" &&
        double.IsFinite(CornerRadius) && CornerRadius is >= 0 and <= 16 && Colors is not null &&
        Colors.Values.All(IsColor);
    internal static bool IsColor(string? value) => value is { Length: 7 } && value[0] == '#' && value.Skip(1).All(char.IsAsciiHexDigit);
    internal static bool IsThemeUrl(string? url) => url is { Length: > 0 and <= 2048 } && !url.Any(char.IsControl) &&
        !url.Contains('\\') && !url.StartsWith('/') &&
        (Uri.TryCreate(url, UriKind.Absolute, out var absolute)
            ? absolute.Scheme == "https" && absolute.UserInfo.Length == 0
            : Uri.TryCreate(url, UriKind.Relative, out _) && !url.Split('/').Any(p => p is "." or ".."));
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
    [JsonIgnore] public bool IsValid => WorldTheme.IsThemeUrl(Url) && double.IsFinite(Opacity) && Opacity is >= 0 and <= .35;
}

/// <summary>Optional nine-slice window frame. Only <c>bezel</c> is understood in this spike.</summary>
public sealed record WorldThemeFrame
{
    public string Kind { get; init; } = "";
    public WorldThemeFrameInsets? Inset { get; init; }
    public double? ContentRadius { get; init; }
    public string? Accent { get; init; }
    public string? Plaque { get; init; }
    public WorldThemeFrameAssets? Assets { get; init; }
    /// <summary>Client defaults when <see cref="Inset"/> is omitted for a bezel.</summary>
    public static WorldThemeFrameInsets DefaultBezelInset { get; } = new() { Left = 28, Top = 36, Right = 28, Bottom = 32 };
    [JsonIgnore] public bool IsValid => Kind == "bezel" &&
        (Inset is null || Inset.IsInsetValid) &&
        (ContentRadius is null || (double.IsFinite(ContentRadius.Value) && ContentRadius is >= 0 and <= 16)) &&
        (Accent is null || WorldTheme.IsColor(Accent)) &&
        (Plaque is null || (Plaque.Length <= 40 && !Plaque.Any(char.IsControl))) &&
        Assets?.Border is { IsValid: true };
    [JsonIgnore] public WorldThemeFrameInsets EffectiveInset => Inset ?? DefaultBezelInset;
    public double EffectiveContentRadius(double themeCornerRadius) => ContentRadius ?? themeCornerRadius;
}

public sealed record WorldThemeFrameAssets
{
    public WorldThemeFrameBorder? Border { get; init; }
}

/// <summary>Nine-slice border bitmap. Opacity rules for tiled chrome do not apply; this is opaque window chrome.</summary>
public sealed record WorldThemeFrameBorder
{
    public string Url { get; init; } = "";
    public WorldThemeFrameInsets Slice { get; init; } = new();
    [JsonIgnore] public bool IsValid => WorldTheme.IsThemeUrl(Url) && Slice.IsSliceValid;
}

public sealed record WorldThemeFrameInsets
{
    public double Left { get; init; }
    public double Top { get; init; }
    public double Right { get; init; }
    public double Bottom { get; init; }
    [JsonIgnore] public bool IsInsetValid => AllFinite(8, 96);
    [JsonIgnore] public bool IsSliceValid => AllFinite(0, 256);
    private bool AllFinite(double min, double max) =>
        double.IsFinite(Left) && double.IsFinite(Top) && double.IsFinite(Right) && double.IsFinite(Bottom) &&
        Left >= min && Left <= max && Top >= min && Top <= max && Right >= min && Right <= max && Bottom >= min && Bottom <= max;
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
                Images = ReadImages(root),
                Frame = ReadFrame(root),
                Skin = ReadSkin(root)
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
    private static WorldThemeFrame? ReadFrame(JsonElement root)
    {
        if (!root.TryGetProperty("frame", out var frame) || frame.ValueKind != JsonValueKind.Object) return null;
        try
        {
            WorldThemeFrameInsets? inset = null;
            if (frame.TryGetProperty("inset", out var insetEl) && insetEl.ValueKind == JsonValueKind.Object)
                inset = insetEl.Deserialize<WorldThemeFrameInsets>(WireOptions);
            double? contentRadius = null;
            if (frame.TryGetProperty("content_radius", out var radiusEl) && radiusEl.ValueKind == JsonValueKind.Number)
                contentRadius = radiusEl.GetDouble();
            string? accent = null;
            if (frame.TryGetProperty("accent", out var accentEl) && accentEl.ValueKind == JsonValueKind.String)
                accent = accentEl.GetString();
            string? plaque = null;
            if (frame.TryGetProperty("plaque", out var plaqueEl) && plaqueEl.ValueKind == JsonValueKind.String)
                plaque = plaqueEl.GetString();
            WorldThemeFrameAssets? assets = null;
            if (frame.TryGetProperty("assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Object)
            {
                WorldThemeFrameBorder? border = null;
                if (assetsEl.TryGetProperty("border", out var borderEl) && borderEl.ValueKind == JsonValueKind.Object)
                {
                    var url = borderEl.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String ? urlEl.GetString() ?? "" : "";
                    var slice = borderEl.TryGetProperty("slice", out var sliceEl) && sliceEl.ValueKind == JsonValueKind.Object
                        ? sliceEl.Deserialize<WorldThemeFrameInsets>(WireOptions) ?? new()
                        : new();
                    border = new WorldThemeFrameBorder { Url = url, Slice = slice };
                }
                assets = new WorldThemeFrameAssets { Border = border };
            }
            var parsed = new WorldThemeFrame
            {
                Kind = frame.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String ? kind.GetString() ?? "" : "",
                Inset = inset, ContentRadius = contentRadius, Accent = accent, Plaque = plaque, Assets = assets
            };
            return parsed.IsValid ? parsed : null;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or OverflowException) { return null; }
    }
    private static WorldThemeSkin? ReadSkin(JsonElement root)
    {
        if (!root.TryGetProperty("skin", out var skin) || skin.ValueKind != JsonValueKind.Object) return null;
        try { return WorldThemeSkinJson.Read(skin); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or OverflowException) { return null; }
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
        if (value.Frame is { IsValid: true } frame)
        {
            writer.WritePropertyName("frame"); writer.WriteStartObject();
            writer.WriteString("kind", frame.Kind);
            if (frame.Inset is { } inset)
            {
                writer.WritePropertyName("inset"); JsonSerializer.Serialize(writer, inset, WireOptions);
            }
            if (frame.ContentRadius is { } radius) writer.WriteNumber("content_radius", radius);
            if (frame.Accent is { } accent) writer.WriteString("accent", accent);
            if (frame.Plaque is { } plaque) writer.WriteString("plaque", plaque);
            if (frame.Assets?.Border is { IsValid: true } border)
            {
                writer.WritePropertyName("assets"); writer.WriteStartObject();
                writer.WritePropertyName("border"); writer.WriteStartObject();
                writer.WriteString("url", border.Url);
                writer.WritePropertyName("slice"); JsonSerializer.Serialize(writer, border.Slice, WireOptions);
                writer.WriteEndObject(); writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        if (value.Skin is { HasContent: true } skin)
        {
            writer.WritePropertyName("skin");
            WorldThemeSkinJson.Write(writer, skin);
        }
        writer.WriteEndObject();
    }
}
