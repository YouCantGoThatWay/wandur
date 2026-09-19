using System.Text.Json;
using Wandur.Core.Settings;
using Wandur.Core.Terminal;

namespace Wandur.Core.Tests;

public sealed class AnsiPaletteTests
{
    [Fact]
    public void IndexedColorIdentitySurvivesBrightBoldResetAndExtendedSequences()
    {
        var terminal = new AnsiTerminal();
        terminal.Append("\x1b[31;44mA\x1b[1mB\x1b[22mC\x1b[91;104mD\x1b[39;49mE\x1b[38;5;1;48;5;12mF\x1b[38;2;205;49;49;48;2;130;170;255mG\x1b[38;5;196mH");
        var runs = terminal.Lines[0].Runs;
        Assert.Equal(new int?[] { 1, 9, 1, 9, null, 1, null, null }, runs.Select(r => r.Style.ForegroundIndex));
        Assert.Equal(new int?[] { 4, 4, 4, 12, null, 12, null, null }, runs.Select(r => r.Style.BackgroundIndex));
        Assert.Equal(runs[5].Style.Foreground, runs[6].Style.Foreground);
        Assert.Equal(runs[5].Style.Background, runs[6].Style.Background);
    }
    [Fact]
    public void OldThemesDefaultToAnsiPaletteAndOverridesValidateAndRoundTrip()
    {
        // Presets ship a full palette; a theme saved before that had no overrides at all.
        var theme = UserTheme.FromPreset("Ember") with { Name = "Custom", AnsiColors = new() };
        var json = JsonSerializer.Serialize(theme);
        var old = JsonSerializer.Deserialize<UserTheme>(json.Replace("\"AnsiColors\":{},", ""))!;
        old.Validate(); Assert.Empty(old.AnsiColors);
        var changed = old with { AnsiColors = new() { [1] = "#112233", [15] = "#ABCDEF" } };
        var loaded = JsonSerializer.Deserialize<UserTheme>(JsonSerializer.Serialize(changed))!;
        loaded.Validate(); Assert.Equal("#112233", loaded.AnsiColors[1]);
        Assert.Throws<ArgumentException>(() => (old with { AnsiColors = new() { [16] = "#112233" } }).Validate());
        Assert.Throws<ArgumentException>(() => (old with { AnsiColors = new() { [1] = "no" } }).Validate());
    }
}
