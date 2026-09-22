using System.Text.Json;
using System.Text.Json.Nodes;
using Wandur.Core.Discovery;

namespace Wandur.Core.Tests;

public sealed class WorldThemeSkinTests
{
    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "world-theme-industrial-skin.json");
    private static JsonNode SkinTheme => JsonNode.Parse(File.ReadAllText(FixturePath))!;

    [Fact]
    public void IndustrialSkinParsesAndRoundTrips()
    {
        var theme = JsonSerializer.Deserialize<WorldTheme>(File.ReadAllText(FixturePath))!;
        Assert.True(theme.IsValid);
        Assert.NotNull(theme.Skin);
        Assert.Equal(1, theme.Skin!.Version);
        Assert.NotNull(theme.Skin.Window);
        Assert.Equal("themes/industrial-v2/window-border.png", theme.Skin.Window!.Border.Url);
        Assert.Equal(new SkinPixelSize(512, 384), theme.Skin.Window.Border.SourceSize);
        Assert.Equal(new SkinPixelBox(31, 75, 31, 48), theme.Skin.Window.Border.Slice);
        Assert.Equal(new SkinBox(16, 38, 16, 24), theme.Skin.Window.Border.Thickness);
        Assert.Equal(new SkinBox(16, 38, 16, 24), theme.Skin.Window.Inset);
        Assert.Equal(3, theme.Skin.Window.Overlays.Count);
        Assert.Equal("header", theme.Skin.Window.Overlays[0].Id);
        Assert.Equal("top-center", theme.Skin.Window.Overlays[0].Anchor);
        Assert.Equal(new SkinSize(320, 64), theme.Skin.Window.Overlays[0].Size);
        Assert.Equal(new SkinFooterClearance(108, 108, 40), theme.Skin.Window.FooterClearance);
        Assert.Equal(new SkinSize(1200, 760), theme.Skin.Window.CompactBelow);
        Assert.NotNull(theme.Skin.Panels?.Default);
        Assert.Equal(32, theme.Skin.Panels!.Default!.HeaderHeight);
        Assert.Equal(new SkinPixelSize(512, 1024), theme.Skin.Panels.Default.Border.SourceSize);
        Assert.Equal(new SkinPixelBox(42, 79, 42, 41), theme.Skin.Panels.Default.Border.Slice);
        Assert.NotNull(theme.Frame);
        Assert.Equal(theme, JsonSerializer.Deserialize<WorldTheme>(JsonSerializer.Serialize(theme)));
        var key = WorldThemeSkinJson.CanonicalKey(theme.Skin);
        Assert.Equal(key, WorldThemeSkinJson.CanonicalKey(
            JsonSerializer.Deserialize<WorldTheme>(JsonSerializer.Serialize(theme))!.Skin!));
    }

    [Fact]
    public void UnknownSkinVersionKeepsLegacyFrameAndColors()
    {
        var json = JsonNode.Parse(File.ReadAllText(FixturePath))!;
        json["skin"]!["version"] = 99;
        var theme = JsonSerializer.Deserialize<WorldTheme>(json.ToJsonString());
        Assert.NotNull(theme);
        Assert.True(theme!.IsValid);
        Assert.Null(theme.Skin);
        Assert.NotNull(theme.Frame);
        Assert.Equal("#15202B", theme.Colors.TerminalText);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("nonobject")]
    [InlineData("version99")]
    [InlineData("negative_thickness")]
    [InlineData("string_metric")]
    [InlineData("invalid_url")]
    [InlineData("source_slice_bounds")]
    [InlineData("duplicate_anchors")]
    [InlineData("bad_overlay_keeps_window")]
    [InlineData("bad_panel_keeps_window")]
    [InlineData("bad_window_keeps_panel")]
    [InlineData("unknown_keys")]
    [InlineData("excessive_overlays")]
    [InlineData("footer_overlap")]
    public void SkinFailureBoundaries(string problem)
    {
        var node = SkinTheme;
        switch (problem)
        {
            case "absent":
                node.AsObject().Remove("skin");
                break;
            case "nonobject":
                node["skin"] = "bad";
                break;
            case "version99":
                node["skin"]!["version"] = 99;
                break;
            case "negative_thickness":
                node["skin"]!["window"]!["border"]!["thickness"]!["left"] = -1;
                break;
            case "string_metric":
                node["skin"]!["window"]!["border"]!["thickness"]!["left"] = "16";
                break;
            case "invalid_url":
                node["skin"]!["window"]!["border"]!["url"] = "file:///secret.png";
                break;
            case "source_slice_bounds":
                node["skin"]!["window"]!["border"]!["slice"] = JsonNode.Parse("""{"left":400,"top":75,"right":400,"bottom":48}""");
                break;
            case "duplicate_anchors":
                node["skin"]!["window"]!["overlays"]![1]!["anchor"] = "top-center";
                break;
            case "bad_overlay_keeps_window":
                node["skin"]!["window"]!["overlays"]![0]!["size"]!["height"] = 999;
                break;
            case "bad_panel_keeps_window":
                node["skin"]!["panels"]!["default"]!["header_height"] = 99;
                break;
            case "bad_window_keeps_panel":
                node["skin"]!["window"]!["border"]!["url"] = "../nope.png";
                break;
            case "unknown_keys":
                node["skin"]!["extra"] = "ignored";
                node["skin"]!["window"]!["extra"] = 1;
                break;
            case "excessive_overlays":
                var overlays = node["skin"]!["window"]!["overlays"]!.AsArray();
                overlays.Add(JsonNode.Parse("""{"id":"extra","url":"themes/industrial-v2/extra.png","anchor":"top-center","size":{"width":10,"height":10}}"""));
                break;
            case "footer_overlap":
                node["skin"]!["window"]!["footer_clearance"]!["min_height"] = 0;
                break;
        }

        var theme = JsonSerializer.Deserialize<WorldTheme>(node.ToJsonString())!;
        Assert.True(theme.IsValid);
        Assert.NotNull(theme.Frame);
        Assert.Equal("#15202B", theme.Colors.TerminalText);

        switch (problem)
        {
            case "absent":
            case "nonobject":
            case "version99":
                Assert.Null(theme.Skin);
                break;
            case "negative_thickness":
            case "string_metric":
            case "invalid_url":
            case "source_slice_bounds":
                Assert.NotNull(theme.Skin);
                Assert.Null(theme.Skin!.Window);
                Assert.NotNull(theme.Skin.Panels?.Default);
                break;
            case "bad_window_keeps_panel":
                Assert.NotNull(theme.Skin);
                Assert.Null(theme.Skin!.Window);
                Assert.NotNull(theme.Skin.Panels?.Default);
                break;
            case "bad_panel_keeps_window":
                Assert.NotNull(theme.Skin?.Window);
                Assert.Null(theme.Skin!.Panels?.Default);
                break;
            case "bad_overlay_keeps_window":
                Assert.NotNull(theme.Skin?.Window);
                Assert.Equal(2, theme.Skin!.Window!.Overlays.Count);
                Assert.DoesNotContain(theme.Skin.Window.Overlays, o => o.Id == "header");
                break;
            case "duplicate_anchors":
                Assert.NotNull(theme.Skin?.Window);
                Assert.True(theme.Skin!.Window!.Overlays.Count <= 2);
                Assert.Single(theme.Skin.Window.Overlays, o => o.Anchor == "top-center");
                break;
            case "unknown_keys":
                Assert.NotNull(theme.Skin?.Window);
                Assert.NotNull(theme.Skin!.Panels?.Default);
                Assert.Equal(3, theme.Skin.Window!.Overlays.Count);
                break;
            case "excessive_overlays":
                Assert.NotNull(theme.Skin?.Window);
                Assert.Empty(theme.Skin!.Window!.Overlays);
                break;
            case "footer_overlap":
                Assert.NotNull(theme.Skin?.Window);
                Assert.DoesNotContain(theme.Skin!.Window!.Overlays, o => o.Anchor is "bottom-left" or "bottom-right");
                Assert.Contains(theme.Skin.Window.Overlays, o => o.Id == "header");
                break;
        }
    }

    [Fact]
    public void RepeatedParsesShareCanonicalKey()
    {
        var a = JsonSerializer.Deserialize<WorldTheme>(File.ReadAllText(FixturePath))!.Skin!;
        var b = JsonSerializer.Deserialize<WorldTheme>(File.ReadAllText(FixturePath))!.Skin!;
        Assert.False(ReferenceEquals(a, b));
        Assert.Equal(WorldThemeSkinJson.CanonicalKey(a), WorldThemeSkinJson.CanonicalKey(b));
    }
}
