using System.Text.Json;

namespace Wandur.Core.Channels;

/// <summary>A world that speaks GMCP names its own channels, so no pattern has to guess at them.</summary>
public sealed record CommChannelText(string Channel, string Talker, string Text);

/// <summary>Decodes the GMCP <c>Comm.Channel.Text</c> package into a channel, a speaker and a line.</summary>
public static class CommChannelProtocol
{
    public const string Package = "Comm.Channel.Text";

    public static CommChannelText? Decode(string message)
    {
        if (message is not { Length: > 0 and <= 16_384 }) return null;
        var space = message.IndexOfAny([' ', '\t', '\n', '\r']);
        if (space < 0 || !message.AsSpan(0, space).Equals(Package, StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            using var document = JsonDocument.Parse(message[(space + 1)..]);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            var channel = Text(document.RootElement, "channel");
            var text = Text(document.RootElement, "text");
            if (channel.Length == 0 || text.Length == 0) return null;
            return new(channel, Text(document.RootElement, "talker"), text);
        }
        catch (JsonException) { return null; }
    }

    private static string Text(JsonElement root, string name)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            var value = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? "" : "";
            return value.Length > 2048 ? value[..2048] : value;
        }
        return "";
    }
}
