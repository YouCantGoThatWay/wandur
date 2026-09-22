using System.Text.Json;
using System.Text.Json.Nodes;
using Wandur.Core.Discovery;

namespace Wandur.Core.Tests;

public sealed class WorldThemeFrameTests
{
    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "world-theme-lotj-bezel.json");
    private static string PalettePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "world-theme-icesus.json");
    private static JsonNode Bezel => JsonNode.Parse(File.ReadAllText(FixturePath))!;

    [Fact]
    public void FramedThemeParsesAndRoundTrips()
    {
        var theme = JsonSerializer.Deserialize<WorldTheme>(File.ReadAllText(FixturePath))!;
        Assert.True(theme.IsValid);
        Assert.NotNull(theme.Frame);
        Assert.Equal("bezel", theme.Frame!.Kind);
        Assert.Equal(28, theme.Frame.Inset!.Left);
        Assert.Equal(36, theme.Frame.Inset.Top);
        Assert.Equal(0, theme.Frame.ContentRadius);
        Assert.Equal("#3DB8E8", theme.Frame.Accent);
        Assert.Equal("WANDUR", theme.Frame.Plaque);
        Assert.Equal("themes/imperial-bezel/border.png", theme.Frame.Assets!.Border!.Url);
        Assert.Equal(48, theme.Frame.Assets.Border.Slice.Left);
        Assert.Equal(theme, JsonSerializer.Deserialize<WorldTheme>(JsonSerializer.Serialize(theme)));
    }

    [Fact]
    public void PaletteOnlyThemeHasNoFrame()
    {
        var theme = JsonSerializer.Deserialize<WorldTheme>(File.ReadAllText(PalettePath))!;
        Assert.True(theme.IsValid);
        Assert.Null(theme.Frame);
    }

    [Theory]
    [InlineData("kind", "ornate")]
    [InlineData("inset", "bad")]
    [InlineData("url", "file:///secret.png")]
    [InlineData("url", "../secret.png")]
    [InlineData("accent", "cyan")]
    [InlineData("plaque", "control")]
    [InlineData("slice", "wide")]
    [InlineData("content_radius", "huge")]
    public void BadFrameIsDroppedWithoutLosingThePalette(string field, string problem)
    {
        var node = Bezel;
        switch (field)
        {
            case "kind": node["frame"]!["kind"] = problem; break;
            case "inset":
                node["frame"]!["inset"] = problem == "bad"
                    ? JsonNode.Parse("""{"left":2,"top":36,"right":28,"bottom":32}""")
                    : node["frame"]!["inset"];
                break;
            case "url": node["frame"]!["assets"]!["border"]!["url"] = problem; break;
            case "accent": node["frame"]!["accent"] = problem; break;
            case "plaque": node["frame"]!["plaque"] = problem == "control" ? "bad\nline" : new string('x', 41); break;
            case "slice":
                node["frame"]!["assets"]!["border"]!["slice"] = JsonNode.Parse("""{"left":300,"top":56,"right":48,"bottom":48}""");
                break;
            case "content_radius": node["frame"]!["content_radius"] = 99; break;
        }
        var theme = JsonSerializer.Deserialize<WorldTheme>(node.ToJsonString())!;
        Assert.True(theme.IsValid);
        Assert.Null(theme.Frame);
        Assert.Equal("lotj-imperial-bezel-v1", theme.Id);
        Assert.Equal("#3DB8E8", theme.Colors.Accent);
    }

    [Fact]
    public void MissingInsetUsesBezelDefaultsWhenFrameIsOtherwiseValid()
    {
        var node = Bezel;
        node["frame"]!.AsObject().Remove("inset");
        var theme = JsonSerializer.Deserialize<WorldTheme>(node.ToJsonString())!;
        Assert.NotNull(theme.Frame);
        Assert.Null(theme.Frame!.Inset);
        Assert.Equal(WorldThemeFrame.DefaultBezelInset, theme.Frame.EffectiveInset);
    }
}
