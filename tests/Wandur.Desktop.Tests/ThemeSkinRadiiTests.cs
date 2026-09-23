using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Discovery;
using Wandur.Desktop.Converters;

namespace Wandur.Desktop.Tests;

/// <summary>
/// Whether a docked panel has square or soft corners belongs to the theme. A skin that paints flat
/// military chrome and one that paints a consumer shell want opposite answers, and the client cannot
/// guess which.
/// </summary>
public sealed class ThemeSkinRadiiTests
{
    private static WorldTheme WithRadii(string? radii)
    {
        var node = JsonNode.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "world-theme-skin-contract.json")))!;
        var skin = node["skin"]!.AsObject();
        skin.Remove("window");
        skin.Remove("panels");
        if (radii is null) skin.Remove("radii");
        else skin["radii"] = JsonNode.Parse(radii);
        return JsonSerializer.Deserialize<WorldTheme>(node.ToJsonString())!;
    }

    [Fact]
    public void RadiiParseAndRoundTrip()
    {
        var theme = WithRadii("""{ "panel": 0, "control": 3 }""");
        Assert.Equal(0, theme.Skin!.Radii!.Panel);
        Assert.Equal(3, theme.Skin.Radii.Control);
        Assert.Equal(theme, JsonSerializer.Deserialize<WorldTheme>(JsonSerializer.Serialize(theme)));
    }

    [Theory]
    [InlineData("""{ "panel": -1 }""")]
    [InlineData("""{ "panel": 25 }""")]
    [InlineData("""{ "panel": "8" }""")]
    public void AnOutOfRangeRadiusIsIgnoredRatherThanClamped(string radii)
    {
        // Clamping would paint a corner the theme never asked for; dropping it keeps the client's own.
        Assert.Null(WithRadii(radii).Skin?.Radii?.Panel);
    }

    [AvaloniaFact]
    public void SquarePanelsReachBothTheResourceAndTheDockChrome()
    {
        ThemeService.Apply(new(), WithRadii("""{ "panel": 0, "control": 2 }"""));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new CornerRadius(0), (CornerRadius)Application.Current!.Resources["CardCornerRadius"]!);
        Assert.Equal(new CornerRadius(2), (CornerRadius)Application.Current.Resources["ControlCornerRadius"]!);
        Assert.Equal(0, DockChromeConverter.Radius);

        ThemeService.Apply(new(), WithRadii("""{ "panel": 14 }"""));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new CornerRadius(14), (CornerRadius)Application.Current.Resources["CardCornerRadius"]!);
        Assert.Equal(14, DockChromeConverter.Radius);
    }

    /// <summary>Without skin radii the theme's own corner_radius still applies; only then the client's.</summary>
    [AvaloniaFact]
    public void ASkinWithoutRadiiFallsBackToTheThemeThenTheClient()
    {
        var theme = WithRadii(null);
        ThemeService.Apply(new(), theme);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(theme.CornerRadius, DockChromeConverter.Radius);

        ThemeService.Apply(new(), null);
        Dispatcher.UIThread.RunJobs();
        // With no world at all, the radius is the client's own default skin's.
        Assert.Equal(DefaultSkin.Radii.Panel, DockChromeConverter.Radius);
    }

    /// <summary>
    /// The one that matters: the panel the reader sees has to actually change shape. The converter feeds a
    /// binding, and a binding does not re-run because a static moved, so this is the assertion that would
    /// catch a radius that is set correctly and never painted.
    /// </summary>
    [AvaloniaFact]
    public async Task TheDockedPanelItselfBecomesSquare()
    {
        await using var harness = await DockHarness.OpenAsync(WithRadii("""{ "panel": 0 }"""));
        Dispatcher.UIThread.RunJobs();

        var corners = harness.Window.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Name is "PART_Border")
            .Select(b => b.CornerRadius)
            .Where(r => r != default)
            .ToList();
        Assert.All(corners, r => Assert.True(
            r.TopLeft == 0 && r.TopRight == 0 && r.BottomLeft == 0 && r.BottomRight == 0,
            $"a docked panel kept a rounded corner the theme asked to square: {r}"));
    }
}
