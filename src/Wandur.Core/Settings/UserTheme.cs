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

    // Every preset carries a full sixteen color terminal palette. The colors keep their
    // canonical hue and stay legible on the preset's own terminal background; the exact
    // thresholds live in the contrast tests, which run over every entry below.
    private sealed record Preset(
        string Name, bool IsLight, string Shell, string Panel, string Terminal, string Text, string Muted, string Accent, string Border,
        string MapBackground, string MapGrid, string EditorBackground, string EditorText, IReadOnlyList<string> Ansi);

    // The historic dark terminal palette. Indices 0, 1, 8 and 9 are lightened from the
    // legacy defaults so black, red, bright black and bright red clear the contrast floor.
    private static readonly string[] DarkAnsi =
    [
        "#6B6B80", "#DE6363", "#0DBC79", "#E5C07B", "#61AFEF", "#C678DD", "#56B6C2", "#D4D4D4",
        "#8A8A8A", "#F47B7B", "#23D18B", "#F5F543", "#82AAFF", "#D670D6", "#29B8DB", "#FFFFFF"
    ];
    private static readonly Preset[] Presets =
    [
        // The client's own look: pale hull plating around a dark transcript, measured off the design it
        // was drawn from. Light chrome with dark content panes is the one combination no other preset has.
        new("Hull", true, "#C9CBC8", "#D1D3D1", "#0E181F", "#22282B", "#4A5053", "#115C73", "#8F9294",
            "#0F181E", "#26333C", "#F4F5F6", "#22282B", DarkAnsi),
        new("Ember", false, "#141519", "#212328", "#1A1C21", "#E3E4E8", "#979BA6", "#DBBFA0", "#2B2E35",
            "#10191F", "#1D2B34", "#161B22", "#E6EDF3", DarkAnsi),
        new("Moonlight", false, "#12141C", "#1C1F2B", "#171A24", "#E3E6F1", "#969CAF", "#B9B6F2", "#292D3C",
            "#10191F", "#1D2B34", "#161B22", "#E6EDF3", DarkAnsi),
        new("Forest", false, "#111816", "#1D2823", "#17201C", "#DFE8E1", "#92A397", "#A4C8AD", "#2A3730",
            "#10191F", "#1D2B34", "#161B22", "#E6EDF3", DarkAnsi),
        new("Midnight", false, "#0D1220", "#161D30", "#111728", "#DCE3F2", "#93A0BE", "#7FB2FF", "#232C45",
            "#0B1020", "#1E2742", "#101628", "#DCE3F2",
            ["#606980", "#DD5A63", "#2BD458", "#D4BA2B", "#4988DA", "#CB51DB", "#2BC3D4", "#C9CBCF",
             "#818692", "#F68890", "#54F27E", "#F2DA54", "#76ADF5", "#E77EF5", "#54E2F2", "#FFFFFF"]),
        new("Slate", false, "#16181A", "#212428", "#1B1E21", "#E2E4E7", "#9AA0A6", "#B8C0C8", "#2D3136",
            "#14171A", "#272B30", "#1A1D20", "#E2E4E7",
            ["#5E6E7D", "#CF6E6E", "#40BF6A", "#BFB540", "#668BCC", "#C766CC", "#40AABF", "#C9CCCF",
             "#848C94", "#ED9898", "#63E38E", "#E3D963", "#8EB0EB", "#E68EEB", "#63CEE3", "#FFFFFF"]),
        new("Rose", false, "#1A1114", "#26191E", "#1F1418", "#F2E2E6", "#B39AA2", "#E58FA5", "#3A2630",
            "#170F12", "#33222A", "#1E1317", "#F2E2E6",
            ["#806067", "#E0594D", "#26D971", "#D9D926", "#5A81E2", "#DD3CD7", "#26ACD9", "#CFC9CA",
             "#928185", "#F7887E", "#52F496", "#F4F452", "#8CAAF8", "#F66BF1", "#52CCF4", "#FFFFFF"]),
        new("Paper", true, "#EEEFEF", "#F6F7F7", "#FFFFFF", "#282D35", "#5F6674", "#9A7443", "#DEE1E6",
            "#F4F5F6", "#DCE0E6", "#FFFFFF", "#24292F",
            ["#13161B", "#A12121", "#1B833E", "#7B7319", "#2150A1", "#9A21A1", "#1D798C", "#43464C",
             "#5D636F", "#9A0404", "#02591F", "#504902", "#043FA4", "#85048B", "#035464", "#1F2228"]),
        new("Parchment", true, "#F3EADA", "#F9F2E6", "#FBF5EA", "#3A3128", "#6B5E4C", "#8A5E2B", "#E0D3BD",
            "#F6EFE1", "#DFD2BC", "#FBF5EA", "#3A3128",
            ["#1B1813", "#A5261D", "#15793D", "#716F14", "#1D46A5", "#A51DA2", "#19748F", "#4C4843",
             "#6F685D", "#900D04", "#024F21", "#464502", "#0434A4", "#81037F", "#034D63", "#28241F"]),
        new("Daylight", true, "#F2F4F7", "#FAFBFC", "#FFFFFF", "#1F2933", "#5A6672", "#1F6FEB", "#DCE1E8",
            "#F7F9FB", "#D8DEE6", "#FFFFFF", "#1F2933",
            ["#13171B", "#A31F28", "#188134", "#817118", "#1F58A3", "#931FA3", "#197A85", "#43474C",
             "#5D656F", "#9A040E", "#025518", "#5A4C02", "#04479F", "#840495", "#02555E", "#1F2328"]),
        new("Linen", true, "#EDEAE4", "#F5F3EE", "#F8F6F2", "#2F2E2B", "#62605A", "#2F6E62", "#DFDBD2",
            "#F2EFE9", "#DAD5CB", "#F8F6F2", "#2F2E2B",
            ["#1B1913", "#9D2525", "#1D7C3D", "#746C1B", "#25519D", "#97259D", "#1F7384", "#4C4A43",
             "#6F6B5D", "#920808", "#04521E", "#4E4804", "#0840A0", "#7D0783", "#054D5C", "#28261F"])
    ];
    public static IReadOnlyList<string> PresetNames { get; } = Array.AsReadOnly(Presets.Select(p => p.Name).ToArray());

    /// <summary>WCAG 2.x relative luminance of an #RRGGBB color.</summary>
    private static double Luminance(string hex)
    {
        static double Linear(int channel)
        {
            var value = channel / 255d;
            return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(Convert.ToInt32(hex[1..3], 16))
            + 0.7152 * Linear(Convert.ToInt32(hex[3..5], 16))
            + 0.0722 * Linear(Convert.ToInt32(hex[5..7], 16));
    }

    /// <summary>True when black reads better than white on this background. 0.1791 is the
    /// luminance where the WCAG contrast of black and white against it is equal.</summary>
    public static bool IsLightBackground(string hex) => IsColor(hex) && Luminance(hex) > 0.1791;

    /// <summary>
    /// A contrast checked sixteen color terminal palette for a background the personal theme did not
    /// choose itself, such as a world theme's terminal or a custom background color. A preset's own
    /// colors are only legible on a background of its own lightness. Ember and Linen are the two
    /// presets whose palettes clear the contrast floor on every other preset background of their
    /// lightness, which the contrast tests enforce.
    /// </summary>
    public static IReadOnlyDictionary<int, string> PaletteForBackground(string background)
        => IsLightBackground(background) ? LightTerminalPalette : DarkTerminalPalette;
    private static readonly IReadOnlyDictionary<int, string> DarkTerminalPalette = FromPreset("Ember").AnsiColors;
    private static readonly IReadOnlyDictionary<int, string> LightTerminalPalette = FromPreset("Linen").AnsiColors;
    /// <summary>Localized display name for a preset. Other identifiers are returned unchanged.</summary>
    public static string DisplayName(string? name) => name switch
    {
        "Hull" => L.ThemeHull, "Ember" => L.ThemeEmber, "Moonlight" => L.ThemeMoonlight, "Forest" => L.ThemeForest, "Paper" => L.ThemePaper,
        "Midnight" => L.ThemeMidnight, "Slate" => L.ThemeSlate, "Rose" => L.ThemeRose,
        "Parchment" => L.ThemeParchment, "Daylight" => L.ThemeDaylight, "Linen" => L.ThemeLinen,
        _ => name ?? ""
    };
    /// <summary>
    /// What is typed and read on the transcript. For most presets that is the interface text, because the
    /// transcript is the same lightness as the chrome. A preset that pairs light chrome with a dark transcript
    /// (Hull) needs light text there, or everything typed into it is dark on dark.
    /// </summary>
    private static string TerminalTextFor(Preset preset) =>
        IsLightBackground(preset.Terminal) == preset.IsLight ? preset.Text
        : preset.IsLight ? "#CBD6E2" : "#1F2933";

    public static UserTheme FromPreset(string name)
    {
        var preset = Presets.FirstOrDefault(p => p.Name == name) ?? Presets[0];
        return new() { Name = name, IsLight = preset.IsLight, Colors = new()
        {
            ["Shell"] = preset.Shell, ["Panel"] = preset.Panel, ["Terminal"] = preset.Terminal, ["Text"] = preset.Text, ["Muted"] = preset.Muted,
            ["Accent"] = preset.Accent, ["AccentSecondary"] = preset.Accent, ["Border"] = preset.Border, ["TerminalText"] = TerminalTextFor(preset),
            ["Chrome"] = preset.Panel, ["Selection"] = preset.Border, ["Button"] = preset.Panel, ["ButtonText"] = preset.Text, ["PrimaryText"] = "#242424",
            ["MapBackground"] = preset.MapBackground, ["MapGrid"] = preset.MapGrid,
            ["EditorBackground"] = preset.EditorBackground, ["EditorText"] = preset.EditorText
        }, AnsiColors = preset.Ansi.Select((color, index) => (color, index)).ToDictionary(c => c.index, c => c.color) };
    }
}
