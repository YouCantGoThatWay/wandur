using System.Text.Json;
using System.Text.Json.Nodes;
using Wandur.Core.Discovery;

namespace Wandur.Core.Tests;

/// <summary>
/// Shaded surfaces are the part of a skin that needs no artwork: two colours, a highlight and a bevel per
/// slot. They exist because a docking workspace reads its panel headers, not the frame around the window,
/// and because a gradient cannot be ruined by a slice that does not match the picture.
/// </summary>
public sealed class WorldThemeSkinSurfaceTests
{
    private static JsonNode Theme()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "world-theme-skin-contract.json");
        var node = JsonNode.Parse(File.ReadAllText(path))!;
        node["skin"]!["surfaces"] = JsonNode.Parse("""
        {
          "titlebar":     { "from": "#8A97A6", "to": "#3A434E", "gloss": 0.45, "bevel": "raised" },
          "panel_header": { "from": "#CBD6E1", "to": "#7F8D9C", "gloss": 0.4,  "bevel": "raised" },
          "panel_body":   { "from": "#F4F7FA", "to": "#E3EAF1" },
          "footer":       { "from": "#C2CDD8", "to": "#7F8D9C", "bevel": "sunken" }
        }
        """);
        return node;
    }

    private static WorldThemeSkin Skin(JsonNode node) =>
        JsonSerializer.Deserialize<WorldTheme>(node.ToJsonString())!.Skin!;

    [Fact]
    public void SurfacesParseAndRoundTrip()
    {
        var theme = JsonSerializer.Deserialize<WorldTheme>(Theme().ToJsonString())!;
        var surfaces = theme.Skin!.Surfaces!;
        Assert.Equal("#8A97A6", surfaces.TitleBar!.From);
        Assert.Equal("#3A434E", surfaces.TitleBar.To);
        Assert.Equal(0.45, surfaces.TitleBar.Gloss);
        Assert.Equal("raised", surfaces.TitleBar.Bevel);
        Assert.Equal(0, surfaces.PanelBody!.Gloss);
        Assert.Equal("none", surfaces.PanelBody.Bevel);
        Assert.Equal("sunken", surfaces.Footer!.Bevel);
        Assert.Equal(theme, JsonSerializer.Deserialize<WorldTheme>(JsonSerializer.Serialize(theme)));
    }

    /// <summary>A theme with nothing but surfaces is a complete skin: no artwork is required.</summary>
    [Fact]
    public void SurfacesAloneAreEnoughToBeASkin()
    {
        var node = Theme();
        var skin = node["skin"]!.AsObject();
        skin.Remove("window");
        skin.Remove("panels");
        skin.Remove("layout");
        var parsed = Skin(node);
        Assert.Null(parsed.Window);
        Assert.True(parsed.HasContent);
        Assert.NotNull(parsed.Surfaces?.TitleBar);
    }

    [Theory]
    [InlineData("from", "\"not-a-colour\"")]
    [InlineData("to", "\"#12345\"")]
    [InlineData("gloss", "1.5")]
    [InlineData("gloss", "\"0.4\"")]
    [InlineData("bevel", "\"embossed\"")]
    public void ABadValueDropsOnlyThatSurface(string field, string raw)
    {
        var node = Theme();
        node["skin"]!["surfaces"]!["panel_header"]![field] = JsonNode.Parse(raw);
        var surfaces = Skin(node).Surfaces!;
        Assert.Null(surfaces.PanelHeader);
        Assert.NotNull(surfaces.TitleBar);
        Assert.NotNull(surfaces.Footer);
    }

    [Fact]
    public void AnUnknownSlotIsIgnoredWithoutCostingTheKnownOnes()
    {
        var node = Theme();
        node["skin"]!["surfaces"]!["sidebar"] = JsonNode.Parse("""{"from":"#000000","to":"#FFFFFF"}""");
        var surfaces = Skin(node).Surfaces!;
        Assert.NotNull(surfaces.TitleBar);
        Assert.NotNull(surfaces.PanelHeader);
    }

    [Fact]
    public void EverySurfaceBeingBadLeavesNoSurfacesRatherThanAnEmptyOne()
    {
        var node = Theme();
        node["skin"]!["surfaces"] = JsonNode.Parse("""{"titlebar":{"from":"nope","to":"nope"}}""");
        Assert.Null(Skin(node).Surfaces);
    }

    /// <summary>
    /// The smallest surface a world owner can type: two colours. Everything else has a default, because a
    /// theme is meant to be filled in on a form by whoever runs the MUD, not authored by a designer.
    /// </summary>
    [Fact]
    public void TwoColoursAreEnoughForASurface()
    {
        var node = Theme();
        node["skin"]!["surfaces"] = JsonNode.Parse("""
        { "panel_header": { "from": "#96A2AE", "to": "#5A646F" } }
        """);
        var header = Skin(node).Surfaces!.PanelHeader!;
        Assert.Equal(0, header.Gloss);
        Assert.Equal("none", header.Bevel);
        Assert.Equal(1, header.BevelStrength);
        Assert.Equal(0, header.Grain);
        Assert.Null(header.Rule);
    }

    [Theory]
    [InlineData("bevel_strength", "1.1")]
    [InlineData("bevel_strength", "-0.1")]
    [InlineData("grain", "2")]
    [InlineData("grain", "\"0.5\"")]
    [InlineData("rule", "\"cyan\"")]
    public void AnOutOfRangeFinishDropsOnlyThatSurface(string field, string raw)
    {
        var node = Theme();
        node["skin"]!["surfaces"]!["panel_header"]![field] = JsonNode.Parse(raw);
        var surfaces = Skin(node).Surfaces!;
        Assert.Null(surfaces.PanelHeader);
        Assert.NotNull(surfaces.TitleBar);
    }

    [Fact]
    public void FinishValuesSurviveTheRoundTrip()
    {
        var node = Theme();
        node["skin"]!["surfaces"]!["panel_header"]!["bevel_strength"] = 0.4;
        node["skin"]!["surfaces"]!["panel_header"]!["grain"] = 0.6;
        var theme = JsonSerializer.Deserialize<WorldTheme>(node.ToJsonString())!;
        var header = theme.Skin!.Surfaces!.PanelHeader!;
        Assert.Equal(0.4, header.BevelStrength);
        Assert.Equal(0.6, header.Grain);
        Assert.Equal(theme, JsonSerializer.Deserialize<WorldTheme>(JsonSerializer.Serialize(theme)));
    }
}
