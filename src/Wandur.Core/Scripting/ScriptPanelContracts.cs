using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Wandur.Core.Scripting;

/// <summary>Widget properties a script may declare. Unset members keep their defaults; the renderer
/// reads only the members its widget kind uses.</summary>
public sealed record ScriptWidgetProperties
{
    public string? Label { get; init; }
    public string? Title { get; init; }
    public string? Text { get; init; }
    public string? Placeholder { get; init; }
    public string? Value { get; init; }
    public double Number { get; init; }
    public double Maximum { get; init; } = 100;
    public double? Warn { get; init; }
    public bool On { get; init; }
    public IReadOnlyList<string> Items { get; init; } = [];
    public IReadOnlyList<string> Columns { get; init; } = [];
    public IReadOnlyList<IReadOnlyList<string>> Rows { get; init; } = [];
    public IReadOnlyList<string> Children { get; init; } = [];
}

/// <summary>One validated panel instruction produced by a script worker. Panels are data; the client renders them.</summary>
public sealed record ScriptPanelAction(string Panel, string Action)
{
    public string Title { get; init; } = "";
    public string Dock { get; init; } = DockRight;
    public string Widget { get; init; } = "";
    public string Kind { get; init; } = "";
    public ScriptWidgetProperties Properties { get; init; } = new();

    public const string DockLeft = "left";
    public const string DockRight = "right";
    public const int MaximumStringCharacters = 4096;
    public const int MaximumCollectionItems = 500;
    public const int MaximumColumns = 32;
    public const int MaximumChildren = 64;
    public const int MaximumWidgets = 64;
    public const int MaximumPanels = 8;
    public const int MaximumActionsPerEvent = 32;

    public static IReadOnlyList<string> Actions { get; } = ["create", "widget", "remove", "show", "hide", "close"];
    public static IReadOnlyList<string> EventNames { get; } = ["click", "change", "submit", "select"];

    private static readonly Regex Identifier = new(@"^[A-Za-z0-9][A-Za-z0-9_.\-]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50));

    /// <summary>Re-validates a worker instruction before anything is rendered. Malformed data is a script error.</summary>
    public static ScriptPanelAction Parse(string json)
    {
        if (json.Length > JavaScriptEngine.MaximumPanelActionCharacters) throw new FormatException("A panel action exceeds 65536 characters.");
        JsonDocument document;
        try { document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 }); }
        catch (JsonException error) { throw new FormatException("A panel action is not valid JSON.", error); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException("A panel action must be an object.");
            var panel = Name(root, "panel");
            var action = Text(root, "action", 16);
            if (!Actions.Contains(action, StringComparer.Ordinal)) throw new FormatException("Unknown panel action.");
            var result = new ScriptPanelAction(panel, action);
            if (action == "create")
            {
                var dock = Text(root, "dock", 16);
                if (dock is not (DockLeft or DockRight)) throw new FormatException("A panel docks to left or right.");
                result = result with { Title = Text(root, "title", MaximumStringCharacters), Dock = dock };
            }
            else if (action is "widget" or "remove")
            {
                result = result with { Widget = Name(root, "widget") };
                if (action == "widget")
                {
                    var kind = Text(root, "kind", 16);
                    if (!JavaScriptEngine.PanelWidgetKinds.Contains(kind, StringComparer.Ordinal)) throw new FormatException("Unknown widget kind.");
                    result = result with { Kind = kind, Properties = ReadProperties(root, kind) };
                }
            }
            return result;
        }
    }

    private static ScriptWidgetProperties ReadProperties(JsonElement root, string kind)
    {
        if (!root.TryGetProperty("props", out var properties)) return new();
        if (properties.ValueKind != JsonValueKind.Object) throw new FormatException("Widget properties must be an object.");
        var result = new ScriptWidgetProperties();
        switch (kind)
        {
            case "gauge":
                result = result with { Label = Optional(properties, "label"), Number = Number(properties, "value"), Maximum = Number(properties, "max") };
                if (properties.TryGetProperty("warn", out _)) result = result with { Warn = Number(properties, "warn") };
                break;
            case "label" or "text":
                result = result with { Text = Optional(properties, "text") ?? "" };
                break;
            case "list":
                result = result with { Title = Optional(properties, "title"), Items = Strings(properties, "items", MaximumCollectionItems) };
                break;
            case "table":
                result = result with { Title = Optional(properties, "title"), Columns = Strings(properties, "columns", MaximumColumns), Rows = Rows(properties) };
                break;
            case "button":
                result = result with { Label = Optional(properties, "label") ?? "" };
                break;
            case "toggle":
                result = result with { Label = Optional(properties, "label") ?? "", On = Flag(properties, "value") };
                break;
            case "input":
                result = result with { Placeholder = Optional(properties, "placeholder"), Value = Optional(properties, "value") };
                break;
            case "group":
                result = result with { Title = Optional(properties, "title"), Children = Strings(properties, "children", MaximumChildren) };
                break;
        }
        return result;
    }

    private static IReadOnlyList<IReadOnlyList<string>> Rows(JsonElement properties)
    {
        if (!properties.TryGetProperty("rows", out var rows)) return [];
        if (rows.ValueKind != JsonValueKind.Array) throw new FormatException("rows must be an array.");
        if (rows.GetArrayLength() > MaximumCollectionItems) throw new FormatException("rows exceeds 500 items.");
        var result = new List<IReadOnlyList<string>>();
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array) throw new FormatException("Each table row must be an array.");
            if (row.GetArrayLength() > MaximumColumns) throw new FormatException("A table row exceeds 32 cells.");
            result.Add(row.EnumerateArray().Select(cell => Str(cell, "a cell")).ToArray());
        }
        return result;
    }

    private static IReadOnlyList<string> Strings(JsonElement properties, string name, int limit)
    {
        if (!properties.TryGetProperty(name, out var value)) return [];
        if (value.ValueKind != JsonValueKind.Array) throw new FormatException(name + " must be an array.");
        if (value.GetArrayLength() > limit) throw new FormatException(name + " exceeds its item limit.");
        return value.EnumerateArray().Select(entry => Str(entry, name)).ToArray();
    }

    private static string Str(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.String) throw new FormatException(name + " must contain strings.");
        var text = value.GetString()!;
        if (text.Length > MaximumStringCharacters) throw new FormatException(name + " exceeds 4096 characters.");
        return text;
    }

    private static string Name(JsonElement root, string member)
    {
        var value = Text(root, member, 64);
        if (!Identifier.IsMatch(value)) throw new FormatException("A panel or widget id is not valid.");
        return value;
    }

    private static string Text(JsonElement root, string member, int limit)
    {
        if (!root.TryGetProperty(member, out var value) || value.ValueKind != JsonValueKind.String) throw new FormatException(member + " must be a string.");
        var text = value.GetString()!;
        if (text.Length > limit) throw new FormatException(member + " is too long.");
        return text;
    }

    private static string? Optional(JsonElement properties, string member)
        => properties.TryGetProperty(member, out var value) ? Str(value, member) : null;

    private static double Number(JsonElement properties, string member)
    {
        if (!properties.TryGetProperty(member, out var value)) return member == "max" ? 100 : 0;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
            throw new FormatException(member + " must be a finite number.");
        return number;
    }

    private static bool Flag(JsonElement properties, string member)
        => properties.TryGetProperty(member, out var value) && value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new FormatException(member + " must be true or false.")
        };

    /// <summary>The callback message the client sends back to the worker.</summary>
    public static string EventJson(string panel, string widget, string name, bool? flag = null, string? text = null)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("panel", panel);
            writer.WriteString("widget", widget);
            writer.WriteString("event", name);
            if (flag is { } value) writer.WriteBoolean("value", value);
            else if (text is not null) writer.WriteString("value", text);
            else writer.WriteNull("value");
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>The gauge caption, formatted in the user's culture.</summary>
    public static string Measure(double value, double maximum) =>
        value.ToString("0.##", Localization.UiLanguage.Culture) + " / " + maximum.ToString("0.##", Localization.UiLanguage.Culture);
}
