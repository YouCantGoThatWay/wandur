using System.Text.Json;
using System.Text.Json.Serialization;
using Wandur.Models;

namespace Wandur.Core.Discovery;

/// <summary>An invalid optional mapping must not discard its directory listing or saved profile.</summary>
public sealed class WorldMappingConverter : JsonConverter<WorldMapping>
{
    public override WorldMapping? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        try
        {
            var mapping = document.RootElement.Deserialize<WorldMapping>(ModelJson.Options);
            return MappingValidation.IsValid(mapping) ? mapping : null;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or FormatException)
        { return null; }
    }

    public override void Write(Utf8JsonWriter writer, WorldMapping value, JsonSerializerOptions options)
    {
        if (MappingValidation.IsValid(value)) JsonSerializer.Serialize(writer, value, ModelJson.Options);
        else writer.WriteNullValue();
    }
}
