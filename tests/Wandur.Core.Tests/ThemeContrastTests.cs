using System.Text.Json;
using Wandur.Core.Settings;

namespace Wandur.Core.Tests;

/// <summary>
/// Every preset has to stay readable: WCAG 2.x contrast against its own terminal background
/// and ANSI colors that still read as the hue their index promises.
/// </summary>
public sealed class ThemeContrastTests
{
    private const double HueTolerance = 25;
    // One canonical hue per ANSI role. Yellow sits low and green high so the historic
    // palette (amber yellow, spring green) passes alongside the newer presets.
    private static readonly IReadOnlyDictionary<int, double> CanonicalHue = new Dictionary<int, double>
    { [1] = 0, [2] = 140, [3] = 55, [4] = 218, [5] = 297, [6] = 190 };
    public static TheoryData<string> Presets() => [.. UserTheme.PresetNames];

    private static (double R, double G, double B) Channels(string hex)
    {
        Assert.True(UserTheme.IsColor(hex), hex);
        return (Convert.ToInt32(hex[1..3], 16), Convert.ToInt32(hex[3..5], 16), Convert.ToInt32(hex[5..7], 16));
    }
    private static double Luminance(string hex)
    {
        static double Linear(double channel)
        {
            var value = channel / 255d;
            return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        var (r, g, b) = Channels(hex);
        return 0.2126 * Linear(r) + 0.7152 * Linear(g) + 0.0722 * Linear(b);
    }
    internal static double Contrast(string first, string second)
    {
        double a = Luminance(first), b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }
    private static double Hue(string hex)
    {
        var (r, g, b) = Channels(hex);
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), delta = max - min;
        if (delta == 0) return 0;
        var hue = max == r ? 60 * (((g - b) / delta + 6) % 6) : max == g ? 60 * ((b - r) / delta + 2) : 60 * ((r - g) / delta + 4);
        return (hue + 360) % 360;
    }
    private static double HueDistance(double first, double second)
    {
        var distance = Math.Abs(first - second) % 360;
        return Math.Min(distance, 360 - distance);
    }
    private static double Distance(string first, string second)
    {
        var (r, g, b) = Channels(first);
        var (r2, g2, b2) = Channels(second);
        return Math.Sqrt(Math.Pow(r - r2, 2) + Math.Pow(g - g2, 2) + Math.Pow(b - b2, 2));
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void PresetTerminalColorsStayLegibleOnTheirOwnBackground(string name)
    {
        var theme = UserTheme.FromPreset(name);
        var background = theme.Colors["Terminal"];
        Assert.Equal(16, theme.AnsiColors.Count);
        for (var index = 0; index < 16; index++)
        {
            Assert.True(theme.AnsiColors.TryGetValue(index, out var color), $"{name} is missing ANSI {index}");
            // Index 0 is the "black" games mostly paint as a background, so it only has to stay visible.
            var required = index == 0 ? 3 : 4.5;
            Assert.True(Contrast(color, background) >= required,
                $"{name} ANSI {index} ({color}) has {Contrast(color, background):F2}:1 on {background}, needs {required}:1");
        }
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void PresetTerminalColorsKeepTheirHueAndBrightVariantsStayDistinct(string name)
    {
        var ansi = UserTheme.FromPreset(name).AnsiColors;
        foreach (var (index, canonical) in CanonicalHue)
            foreach (var slot in new[] { index, index + 8 })
                Assert.True(HueDistance(Hue(ansi[slot]), canonical) <= HueTolerance,
                    $"{name} ANSI {slot} ({ansi[slot]}) sits at hue {Hue(ansi[slot]):F0}, expected {canonical} +/- {HueTolerance}");
        foreach (var index in Enumerable.Range(0, 8))
            Assert.True(Distance(ansi[index], ansi[index + 8]) >= 16,
                $"{name} ANSI {index} ({ansi[index]}) and {index + 8} ({ansi[index + 8]}) are too close to tell apart");
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void PresetInterfaceColorsMeetTheirContrastFloor(string name)
    {
        var colors = UserTheme.FromPreset(name).Colors;
        var shell = colors["Shell"];
        Assert.True(Contrast(colors["Text"], shell) >= 7, $"{name} text {colors["Text"]} has {Contrast(colors["Text"], shell):F2}:1 on {shell}");
        Assert.True(Contrast(colors["Muted"], shell) >= 4.5, $"{name} muted {colors["Muted"]} has {Contrast(colors["Muted"], shell):F2}:1 on {shell}");
        Assert.True(Contrast(colors["Accent"], shell) >= 3, $"{name} accent {colors["Accent"]} has {Contrast(colors["Accent"], shell):F2}:1 on {shell}");
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void PresetsValidateAndRoundTripWithTheirVariant(string name)
    {
        var theme = UserTheme.FromPreset(name);
        theme.Validate();
        Assert.Equal(UserTheme.ColorKeys.Count, theme.Colors.Count);
        Assert.All(UserTheme.ColorKeys, key => Assert.True(UserTheme.IsColor(theme.Colors[key]), key));
        var loaded = JsonSerializer.Deserialize<UserTheme>(JsonSerializer.Serialize(theme))!;
        loaded.Validate();
        Assert.Equal(theme.Colors, loaded.Colors);
        Assert.Equal(theme.AnsiColors, loaded.AnsiColors);
        Assert.Equal(theme.IsLight, loaded.IsLight);
        Assert.Equal(theme.IsLight ? "light" : "dark", loaded.ToWorldTheme().Variant);
        // A light preset is lighter than its own text; a dark one is not.
        Assert.Equal(theme.IsLight, Luminance(theme.Colors["Shell"]) > Luminance(theme.Colors["Text"]));
    }

    [Fact]
    public void LightAndDarkPresetsAreBothOffered()
    {
        var variants = UserTheme.PresetNames.Select(name => UserTheme.FromPreset(name).IsLight).ToList();
        Assert.True(variants.Count(light => light) >= 4);
        Assert.True(variants.Count(light => !light) >= 6);
        Assert.Equal(UserTheme.PresetNames.Count, UserTheme.PresetNames.Distinct().Count());
    }
}
