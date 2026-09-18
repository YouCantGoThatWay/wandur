using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Wandur.Core.Mapping;

public enum RoomDataSource { Text, Gmcp, Msdp }

public sealed record RoomObservation(
    string? ServerId, string Name, string Description,
    IReadOnlyDictionary<string, string?> Exits, string? Area = null,
    RoomDataSource Source = RoomDataSource.Text)
{
    public bool ExitsProvided { get; init; }
    public string? Environment { get; init; }
    public double? X { get; init; }
    public double? Y { get; init; }
    public double? Z { get; init; }
    public string? Symbol { get; init; }
}

/// <summary>Conservative, bounded decoding of server-supplied room metadata.</summary>
public static class RoomProtocolDecoder
{
    private const int MaximumPayload = 16_384;
    // Mirrors Wandur.Core.Mapping.MapFileFormat.Coordinate; duplicated to avoid a circular
    // project reference, since Wandur.Core depends on Wandur.Protocol, not the other way around.
    private static bool ValidCoordinate(double value) => double.IsFinite(value) && Math.Abs(value) <= 1e9;


    public static RoomObservation? FromGmcp(string message)
    {
        if (message.Length > MaximumPayload) return null;
        int separator = message.IndexOfAny([' ', '\t']);
        if (separator < 0 || !message.AsSpan(0, separator).Equals("Room.Info", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            using var document = JsonDocument.Parse(message[(separator + 1)..], new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string? name = JsonText(root, "name");
            if (string.IsNullOrWhiteSpace(name)) return null;
            var exits = new Dictionary<string, string?>();
            // LOTJ reports a vnum and planet; its O/C exit values are states, not destination IDs.
            var stateExits = root.TryGetProperty("vnum", out _) && JsonText(root, "planet") is not null;
            if (root.TryGetProperty("exits", out var data))
            {
                if (data.ValueKind == JsonValueKind.Object)
                    foreach (var exit in data.EnumerateObject().Take(64))
                        AddExit(exits, exit.Name, stateExits && exit.Value.ValueKind == JsonValueKind.String &&
                            exit.Value.GetString() is "O" or "C" ? null : JsonId(exit.Value));
                else if (data.ValueKind == JsonValueKind.Array)
                    foreach (var exit in data.EnumerateArray().Take(64))
                        if (exit.ValueKind == JsonValueKind.String) AddExit(exits, exit.GetString()!, null);
            }
            string? id = root.TryGetProperty("num", out var num) ? JsonId(num) : null;
            if (id is null && root.TryGetProperty("id", out var alternate)) id = JsonId(alternate);
            if (id is null && root.TryGetProperty("vnum", out var vnum)) id = JsonId(vnum);
            return new(id, name.Trim(), JsonText(root, "desc") ?? JsonText(root, "description") ?? "", exits,
                JsonText(root, "area") ?? JsonText(root, "zone") ?? JsonText(root, "planet"), RoomDataSource.Gmcp)
            {
                Environment = JsonText(root, "environment") ?? JsonText(root, "terrain"),
                Symbol = JsonText(root, "symbol"), ExitsProvided = data.ValueKind is JsonValueKind.Object or JsonValueKind.Array,
                X = JsonCoordinate(root, "x"), Y = JsonCoordinate(root, "y"), Z = JsonCoordinate(root, "z")
            };
        }
        catch (JsonException) { return null; }
    }

    public static RoomObservation? FromMsdp(byte[] payload)
    {
        var fields = ParseMsdp(payload);
        if (fields is null) return null;
        bool nested = fields.TryGetValue("ROOM", out var room) && room is Dictionary<string, object>;
        if (nested) fields = (Dictionary<string, object>)room!;
        string idKey = nested ? "VNUM" : "ROOM_VNUM", nameKey = nested ? "NAME" : "ROOM_NAME";
        // Independent REPORT updates have no transaction identifier. Never combine a new id with a cached old name.
        if (!fields.ContainsKey(idKey) || !fields.TryGetValue(nameKey, out var value) || value is not string name || string.IsNullOrWhiteSpace(name)) return null;
        var exits = new Dictionary<string, string?>();
        if (fields.TryGetValue(nested ? "EXITS" : "ROOM_EXITS", out var data))
        {
            if (data is Dictionary<string, object> table)
                foreach (var exit in table.Take(64)) AddExit(exits, exit.Key, NormalizeId(exit.Value as string));
            else if (data is List<object> array)
                foreach (var exit in array.OfType<string>().Take(64)) AddExit(exits, exit, null);
        }
        fields.TryGetValue(nested ? "AREA" : "AREA_NAME", out var area);
        return new(NormalizeId(fields[idKey] as string), name.Trim(), "", exits, area as string, RoomDataSource.Msdp)
        {
            Environment = MsdpText(fields, nested ? "ENVIRONMENT" : "ROOM_ENVIRONMENT") ?? MsdpText(fields, nested ? "TERRAIN" : "ROOM_TERRAIN"),
            Symbol = MsdpText(fields, nested ? "SYMBOL" : "ROOM_SYMBOL"),
            ExitsProvided = data is Dictionary<string, object> or List<object>,
            X = MsdpCoordinate(fields, nested, "X"), Y = MsdpCoordinate(fields, nested, "Y"), Z = MsdpCoordinate(fields, nested, "Z")
        };
    }

    private static double? JsonCoordinate(JsonElement root, string axis)
    {
        // Each observation is decoded independently; missing axes never borrow another room's values.
        foreach (var key in new[] { "coord", "coords", "coordinates" })
            if (root.TryGetProperty(key, out var coordinates) && coordinates.ValueKind == JsonValueKind.Object && coordinates.TryGetProperty(axis, out var component))
                return Number(component);
        return root.TryGetProperty(axis, out var direct) ? Number(direct) : null;
    }

    private static double? Number(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && ValidCoordinate(number)) return number;
        return value.ValueKind == JsonValueKind.String ? Number(value.GetString()) : null;
    }

    private static double? Number(string? value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
        ValidCoordinate(number) ? number : null;
    private static string? MsdpText(Dictionary<string, object> fields, string key) => fields.TryGetValue(key, out var value) ? value as string : null;
    private static double? MsdpCoordinate(Dictionary<string, object> fields, bool nested, string axis)
    {
        if (fields.TryGetValue(nested ? "COORDINATES" : "ROOM_COORDINATES", out var data) && data is Dictionary<string, object> coords)
            return Number(MsdpText(coords, axis));
        return Number(MsdpText(fields, nested ? axis : "ROOM_" + axis));
    }

    private static string? JsonText(JsonElement root, string key) =>
        root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? JsonId(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => NormalizeId(value.GetString()),
        JsonValueKind.Number when value.TryGetInt64(out long number) && number > 0 => number.ToString(CultureInfo.InvariantCulture),
        _ => null
    };

    private static string? NormalizeId(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > 128 || value.Any(char.IsControl)) return null;
        if (value.ToLowerInvariant() is "unknown" or "null" or "none" or "nil" or "undefined" or "false" or "true"
            or "nan" or "inf" or "infinity" or "-infinity" or "?" or "-1" or "0") return null;
        if (decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal number))
            return number > 0 && decimal.Truncate(number) == number ? number.ToString("0", CultureInfo.InvariantCulture) : null;
        return value;
    }

    private static void AddExit(Dictionary<string, string?> exits, string direction, string? id)
    {
        direction = direction.Trim().ToLowerInvariant() switch
        {
            "n" => "north", "s" => "south", "e" => "east", "w" => "west", "ne" => "northeast", "nw" => "northwest",
            "se" => "southeast", "sw" => "southwest", "u" => "up", "d" => "down", var other => other
        };
        if (direction.Length is > 0 and <= 64 && !direction.Any(char.IsControl)) exits[direction] = id;
    }

    internal static Dictionary<string, object>? ParseMsdp(byte[] payload)
    {
        if (payload.Length == 0 || payload.Length > MaximumPayload) return null;
        try { return new MsdpReader(payload).Read(); }
        catch (FormatException) { return null; }
    }

    private sealed class MsdpReader(byte[] data)
    {
        private int _offset;
        public Dictionary<string, object> Read() => Table(0, false);
        private Dictionary<string, object> Table(int depth, bool nested)
        {
            if (depth > 8) throw new FormatException();
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            while (_offset < data.Length && data[_offset] != 4)
            {
                Expect(1);
                string key = Text();
                if (key.Length == 0 || key.Length > 128 || result.Count >= 256) throw new FormatException();
                Expect(2);
                object value = Value(depth + 1);
                // Command lists may use repeated VAL delimiters without ARRAY_OPEN.
                if (_offset < data.Length && data[_offset] == 2)
                {
                    var values = new List<object> { value };
                    while (_offset < data.Length && data[_offset] == 2) { _offset++; values.Add(Value(depth + 1)); }
                    value = values;
                }
                if (!result.TryAdd(key, value)) throw new FormatException();
            }
            if (nested) Expect(4);
            else if (_offset != data.Length) throw new FormatException();
            return result;
        }
        private object Value(int depth)
        {
            if (depth > 8) throw new FormatException();
            if (_offset < data.Length && data[_offset] == 3) { _offset++; return Table(depth, true); }
            if (_offset < data.Length && data[_offset] == 5)
            {
                _offset++;
                var array = new List<object>();
                while (_offset < data.Length && data[_offset] != 6)
                {
                    if (array.Count >= 256) throw new FormatException();
                    Expect(2); array.Add(Value(depth + 1));
                }
                Expect(6);
                return array;
            }
            return Text();
        }
        private string Text()
        {
            int start = _offset;
            while (_offset < data.Length && data[_offset] > 6 && data[_offset] != 255) _offset++;
            if (_offset < data.Length && data[_offset] is 0 or 255) throw new FormatException();
            return Encoding.UTF8.GetString(data, start, _offset - start);
        }
        private void Expect(byte marker)
        {
            if (_offset >= data.Length || data[_offset++] != marker) throw new FormatException();
        }
    }
}
