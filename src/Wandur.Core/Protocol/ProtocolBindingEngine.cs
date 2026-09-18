using System.Globalization;
using System.Text.Json;
using Wandur.Models;

namespace Wandur.Core.Protocol;

/// <summary>Applies only validated fields from an endpoint-bound mapping. State is local to one connection lifetime.</summary>
public sealed class ProtocolBindingEngine
{
    public const double MaximumMagnitude = 1e15;
    public const int MaximumTextLength = 512;
    private FieldBinding[] _bindings;
    private WorldMapping _mapping;
    public GameState State { get; private set; } = new();

    public ProtocolBindingEngine(WorldMapping mapping)
    {
        if (!MappingValidation.IsValid(mapping)) throw new ArgumentException("Invalid protocol mapping.", nameof(mapping));
        _mapping = mapping;
        _bindings = [.. mapping.Bindings];
    }

    /// <summary>Preserve observations only for an additive map with identical identity rules.
    /// Newly mapped fields require fresh public packets; diagnostic history is never replayed.</summary>
    public bool UpdateMapping(WorldMapping mapping)
    {
        if (!MappingValidation.IsValid(mapping)) throw new ArgumentException("Invalid protocol mapping.", nameof(mapping));
        var sameEndpoint = _mapping.WorldId == mapping.WorldId && _mapping.Endpoint.Matches(mapping.Endpoint);
        if (sameEndpoint && _bindings.ToHashSet().SetEquals(mapping.Bindings)) { _mapping = mapping; return false; }
        var sameIdentity = _bindings.Where(b => b.Target.Category == "identity").ToHashSet()
            .SetEquals(mapping.Bindings.Where(b => b.Target.Category == "identity"));
        if (!sameEndpoint || !sameIdentity || !_bindings.All(mapping.Bindings.Contains)) Reset();
        _mapping = mapping;
        _bindings = [.. mapping.Bindings];
        return true;
    }

    public void Reset() => State = new();

    // Call only after the session's privacy and epoch gates. This also rejects formatter redactions.
    public void Observe(byte option, ProtocolDiagnosticContent content, DateTimeOffset receivedAt)
    {
        if (option is not (69 or 201) || content.Malformed || content.Truncated || content.Redacted
            || content.Body.Length > ProtocolDiagnosticFormatter.MaximumBodyCharacters || GmcpLoginProtocol.IsPrivate(content.Name)) return;
        var protocol = option == 69 ? "MSDP" : "GMCP";
        var package = option == 69 ? "MSDP" : content.Name;
        try
        {
            using var document = JsonDocument.Parse(content.Body, new JsonDocumentOptions { MaxDepth = 32 });
            var updates = new List<(FieldBinding Binding, JsonElement Value)>();
            foreach (var binding in _bindings)
                if (binding.Source.Protocol == protocol && binding.Source.Package == package && TryResolve(document.RootElement, binding.Source.Path, out var value))
                    updates.Add((binding, value));
            // Identity must be processed before vitals, regardless of map/packet property ordering.
            var changed = updates.Where(u => u.Binding.Target.Category == "identity" && u.Binding.Target.Key is "name" or "id")
                .Where(u => Entity(u.Binding.Target.Entity).Identity.TryGetValue(u.Binding.Target.Key, out var previous)
                    && previous.Value != ReadText(u.Value)).Select(u => u.Binding.Target.Entity).Distinct().ToArray();
            if (changed.Contains("character")) Reset();
            else
                foreach (var entity in changed)
                    State = entity switch { "opponent" => State with { Opponent = new() }, "vehicle" => State with { Vehicle = new() }, "world" => State with { World = new() }, _ => State };
            foreach (var update in updates) Apply(update.Binding, update.Value, receivedAt);
        }
        catch (JsonException) { /* Malformed packets supply no observations. */ }
    }

    private EntityState Entity(string entity) => entity switch
    { "character" => State.Character, "opponent" => State.Opponent, "vehicle" => State.Vehicle, _ => State.World };

    private void Apply(FieldBinding binding, JsonElement value, DateTimeOffset now)
    {
        var target = binding.Target;
        var entity = Entity(target.Entity);
        var key = target.Key;
        var label = string.IsNullOrEmpty(binding.Label) ? key : binding.Label;
        Observation<double>? number = ReadNumber(value, binding.Scale) is { } n ? new(n, now) : null;
        Observation<string>? text = ReadText(value) is { } t ? new(t, now) : null;
        switch (target.Category)
        {
            case "identity":
            case "location":
                var fields = target.Category == "identity" ? entity.Identity : entity.Location;
                if (text is null) fields.Remove(key); else fields[key] = text;
                break;
            case "resource":
            case "progression":
                var resources = target.Category == "resource" ? entity.Resources : entity.Progression;
                var resource = resources.GetValueOrDefault(key) ?? new(label, null, null);
                resources[key] = target.Member == "current" ? resource with { Current = number } : resource with { Maximum = number };
                break;
            case "attribute":
                var attribute = entity.Attributes.GetValueOrDefault(key) ?? new(label, null, null);
                entity.Attributes[key] = target.Member == "current" ? attribute with { Current = number } : attribute with { Base = number };
                break;
            case "currency":
                var currency = entity.Currencies.GetValueOrDefault(key) ?? new(label, null, null, null);
                entity.Currencies[key] = target.Member switch
                { "carried" => currency with { Carried = number }, "bank" => currency with { Bank = number }, _ => currency with { Total = number } };
                break;
            case "metric":
                entity.Metrics[key] = new(label, binding.Conversion == "text" ? text : null,
                    binding.Conversion == "number" ? number : null,
                    binding.Conversion == "boolean" && ReadBoolean(value) is { } b ? new(b, now) : null);
                break;
        }
    }

    private static double? ReadNumber(JsonElement value, double scale)
    {
        double number;
        if (value.ValueKind == JsonValueKind.Number)
        { if (!value.TryGetDouble(out number)) return null; }
        else if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= 64 } text)
        { if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return null; }
        else return null;
        number *= scale;
        return double.IsFinite(number) && Math.Abs(number) <= MaximumMagnitude ? number : null;
    }

    private static string? ReadText(JsonElement value)
    {
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => ReadNumber(value, 1)?.ToString("G", CultureInfo.InvariantCulture),
            _ => null
        };
        return text is { Length: > 0 and <= MaximumTextLength } && !text.Any(char.IsControl) ? text : null;
    }

    private static bool? ReadBoolean(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true, JsonValueKind.False => false,
        JsonValueKind.Number when value.TryGetInt32(out var n) && n is 0 or 1 => n == 1,
        JsonValueKind.String when value.GetString() is { Length: <= 5 } s => s.ToLowerInvariant() switch
        { "true" or "1" => true, "false" or "0" => false, _ => null },
        _ => null
    };

    private static bool TryResolve(JsonElement root, string pointer, out JsonElement value)
    {
        value = root;
        if (pointer.Length == 0) return true;
        foreach (var encoded in pointer[1..].Split('/'))
        {
            var segment = encoded.Replace("~1", "/").Replace("~0", "~");
            if (value.ValueKind == JsonValueKind.Object)
            { if (!value.TryGetProperty(segment, out value)) return false; }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                if (segment.Length == 0 || segment.Length > 1 && segment[0] == '0' || !int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                    || index >= value.GetArrayLength()) return false;
                value = value[index];
            }
            else
            {
                // An explicit null/wrong-shaped ancestor invalidates the leaf, never supplies its value.
                value = default;
                return true;
            }
        }
        return true;
    }
}
