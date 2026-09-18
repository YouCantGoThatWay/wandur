using Wandur.Core.Discovery;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Core.Settings;

/// <summary>A named, data-only personal palette persisted with client settings.</summary>
public sealed record UserTheme
{
    public string Id { get; init; } = "custom-" + Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    public bool IsLight { get; init; }
    public Dictionary<string, string> Colors { get; init; } = [];
    public Dictionary<int, string> AnsiColors { get; init; } = [];
    public static IReadOnlyList<string> ColorKeys { get; } = ["Shell", "Panel", "Terminal", "Text", "Muted", "Accent", "AccentSecondary", "Border", "TerminalText", "Chrome", "Selection", "Button", "ButtonText", "PrimaryText", "MapBackground", "MapGrid", "EditorBackground", "EditorText"];
    public static IReadOnlyList<string> PresetNames { get; } = ["Ember", "Moonlight", "Forest", "Paper"];
    public static bool IsColor(string? value) => value is { Length: 7 } && value[0] == '#' && value.Skip(1).All(char.IsAsciiHexDigit);
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 100 || Name.Any(char.IsControl) ||
            Id is not { Length: > 7 and <= 80 } || !Id.StartsWith("custom-", StringComparison.Ordinal) || !Id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') ||
            AnsiColors is null || AnsiColors.Any(c => c.Key is < 0 or > 15 || !IsColor(c.Value)) ||
            Colors is null || Colors.Count != ColorKeys.Count || ColorKeys.Any(k => !Colors.TryGetValue(k, out var value) || !IsColor(value)))
            throw new ArgumentException(L.InvalidCustomTheme);
    }
    public WorldTheme ToWorldTheme() => new()
    {
        Id = Id, Name = Name, Variant = IsLight ? "light" : "dark", CornerRadius = 8,
        Colors = new() { Shell = Colors["Shell"], Panel = Colors["Panel"], Terminal = Colors["Terminal"], Text = Colors["Text"], Muted = Colors["Muted"], Accent = Colors["Accent"], AccentSecondary = Colors["AccentSecondary"], Border = Colors["Border"], TerminalText = Colors["TerminalText"] }
    };
    public static UserTheme FromPreset(string name)
    {
        var (shell, panel, terminal, text, muted, accent, border) = name switch
        {
            "Moonlight" => ("#12141C", "#1C1F2B", "#171A24", "#E3E6F1", "#969CAF", "#B9B6F2", "#292D3C"),
            "Forest" => ("#111816", "#1D2823", "#17201C", "#DFE8E1", "#92A397", "#A4C8AD", "#2A3730"),
            "Paper" => ("#EEEFEF", "#F6F7F7", "#FFFFFF", "#282D35", "#686F7B", "#CEC3AA", "#DEE1E6"),
            _ => ("#141519", "#212328", "#1A1C21", "#E3E4E8", "#979BA6", "#DBBFA0", "#2B2E35")
        };
        return new() { Name = name, IsLight = name == "Paper", Colors = new()
        {
            ["Shell"] = shell, ["Panel"] = panel, ["Terminal"] = terminal, ["Text"] = text, ["Muted"] = muted,
            ["Accent"] = accent, ["AccentSecondary"] = accent, ["Border"] = border, ["TerminalText"] = text,
            ["Chrome"] = panel, ["Selection"] = border, ["Button"] = panel, ["ButtonText"] = text, ["PrimaryText"] = "#242424",
            ["MapBackground"] = "#10191F", ["MapGrid"] = "#1D2B34",
            ["EditorBackground"] = name == "Paper" ? "#FFFFFF" : "#161B22", ["EditorText"] = name == "Paper" ? "#24292F" : "#E6EDF3"
        } };
    }
}
