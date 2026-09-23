using System.Text.Json;
using System.Text.Json.Nodes;
using Wandur.Core.Discovery;

namespace Wandur.Core.Tests;

/// <summary>
/// The nameplate is a shape and some colours, never a file. That is the whole point: a world configures
/// its title plate on a form instead of commissioning a PNG, and nothing about it can be the wrong size,
/// arrive stale from a cache, or fail to download.
/// </summary>
public sealed class WorldThemePlaqueTests
{
    private static JsonNode Theme(string? plaque, string? height = null)
    {
        var node = JsonNode.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "world-theme-skin-contract.json")))!;
        var bar = node["skin"]!["layout"]!["titlebar"]!.AsObject();
        if (plaque is not null) bar["plaque"] = JsonNode.Parse(plaque);
        if (height is not null) bar["height"] = JsonNode.Parse(height);
        return node;
    }

    private static WorldThemeSkin Skin(JsonNode node) =>
        JsonSerializer.Deserialize<WorldTheme>(node.ToJsonString())!.Skin!;

    [Fact]
    public void AShapeOnItsOwnIsAPlaque()
    {
        var plaque = Skin(Theme("""{ "shape": "chamfer" }""")).Layout!.TitleBar!.Plaque!;
        Assert.Equal("chamfer", plaque.Shape);
        Assert.Equal(6, plaque.Cap);
        // Colours left out are taken from the palette rather than guessed here.
        Assert.Null(plaque.Fill);
        Assert.Null(plaque.Edge);
        Assert.Null(plaque.Accent);
    }

    [Fact]
    public void ColoursAndCapRoundTrip()
    {
        var node = Theme("""
        { "shape": "notch", "cap": 10, "fill": "#2A2F36", "edge": "#3D444D", "accent": "#35C4E8" }
        """, "96");
        var theme = JsonSerializer.Deserialize<WorldTheme>(node.ToJsonString())!;
        var bar = theme.Skin!.Layout!.TitleBar!;
        Assert.Equal(96, bar.Height);
        Assert.Equal("notch", bar.Plaque!.Shape);
        Assert.Equal(10, bar.Plaque.Cap);
        Assert.Equal("#35C4E8", bar.Plaque.Accent);
        Assert.Equal(theme, JsonSerializer.Deserialize<WorldTheme>(JsonSerializer.Serialize(theme)));
    }

    [Theory]
    [InlineData("""{ "shape": "hexagon" }""")]
    [InlineData("""{ "shape": 3 }""")]
    [InlineData("""{ "cap": 40 }""")]
    [InlineData("""{ "cap": -1 }""")]
    public void AMalformedPlaqueCostsTheTitleItsPlateAndNothingElse(string plaque)
    {
        var bar = Skin(Theme(plaque)).Layout!.TitleBar!;
        Assert.Null(bar.Plaque);
        // The band it would have sat on is untouched, so the title is simply drawn straight onto it.
        Assert.True(bar.HostsToolbar);
        Assert.Equal("center", bar.TitleAlign);
    }

    [Fact]
    public void AnUnreadableColourIsDroppedWithoutLosingTheShape()
    {
        var plaque = Skin(Theme("""{ "shape": "round", "fill": "charcoal" }""")).Layout!.TitleBar!.Plaque!;
        Assert.Equal("round", plaque.Shape);
        Assert.Null(plaque.Fill);
    }

    [Theory]
    [InlineData("20")]
    [InlineData("200")]
    [InlineData("\"96\"")]
    public void AnOutOfRangeBandHeightLeavesTheClientItsOwn(string height)
    {
        Assert.Null(Skin(Theme(null, height)).Layout?.TitleBar?.Height);
    }

    /// <summary>The whole mounted nameplate is configuration: bracket, plate, padding and drop shadow.</summary>
    [Fact]
    public void BracketShadowAndPaddingRoundTrip()
    {
        var node = Theme("""
        {
          "shape": "chamfer", "cap": 5, "fill": "#36434A", "edge": "#27313A", "accent": "#41F8FD",
          "padding": { "left": 22, "top": 0, "right": 22, "bottom": 0 },
          "wings": { "extend": 46, "fill": "#D8D8D4", "edge": "#575B5A" },
          "shadow": { "color": "#000000", "opacity": 0.3, "blur": 4, "y": 2 }
        }
        """);
        var theme = JsonSerializer.Deserialize<WorldTheme>(node.ToJsonString())!;
        var plaque = theme.Skin!.Layout!.TitleBar!.Plaque!;
        Assert.Equal(22, plaque.Padding.Left);
        Assert.Equal(46, plaque.Wings!.Extend);
        Assert.Equal("#575B5A", plaque.Wings.Edge);
        Assert.Equal(0.3, plaque.Shadow!.Opacity);
        Assert.Equal(4, plaque.Shadow.Blur);
        Assert.Equal(theme, JsonSerializer.Deserialize<WorldTheme>(JsonSerializer.Serialize(theme)));
    }

    [Theory]
    [InlineData("""{ "wings": { "extend": 200 } }""")]
    [InlineData("""{ "wings": { "extend": 40, "edge": "grey" } }""")]
    [InlineData("""{ "shadow": { "color": "#000000", "opacity": 2 } }""")]
    [InlineData("""{ "shadow": { "color": "#00000080" } }""")]
    [InlineData("""{ "padding": { "left": 90, "top": 0, "right": 0, "bottom": 0 } }""")]
    public void AMalformedMountDropsThePlateNotTheBar(string plaque)
    {
        var bar = Skin(Theme(plaque)).Layout!.TitleBar!;
        Assert.Null(bar.Plaque);
        Assert.True(bar.HostsToolbar);
    }

    [Fact]
    public void ABevelledEdgeAndAToolbarSurfaceRoundTrip()
    {
        var node = Theme(null);
        node["skin"]!["edge"] = JsonNode.Parse("""{ "color": "#D8DAD8", "outline": "#5D636A", "thickness": 6 }""");
        node["skin"]!["surfaces"] = JsonNode.Parse("""{ "toolbar": { "from": "#BFC1BE", "to": "#AEB0AD" } }""");
        var theme = JsonSerializer.Deserialize<WorldTheme>(node.ToJsonString())!;
        Assert.Equal("#5D636A", theme.Skin!.Edge!.Outline);
        Assert.Equal(6, theme.Skin.Edge.Thickness);
        Assert.Equal("#BFC1BE", theme.Skin.Surfaces!.Toolbar!.From);
        Assert.Equal(theme, JsonSerializer.Deserialize<WorldTheme>(JsonSerializer.Serialize(theme)));
    }

    /// <summary>Two outlines and a highlight do not fit in less than three pixels.</summary>
    [Fact]
    public void ABevelledEdgeTooThinToHoldItsLinesIsDropped()
    {
        var node = Theme(null);
        node["skin"]!["edge"] = JsonNode.Parse("""{ "color": "#D8DAD8", "outline": "#5D636A", "thickness": 2 }""");
        Assert.Null(Skin(node).Edge);
    }
}
