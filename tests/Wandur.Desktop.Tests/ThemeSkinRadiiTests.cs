using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Wandur.Core.Discovery;
using Wandur.Desktop.Converters;

namespace Wandur.Desktop.Tests;

/// <summary>
/// Legacy radii remain parseable metadata; rendered panels and controls always use Fleet radii.
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
        // Invalid legacy metadata is dropped rather than silently rewritten.
        Assert.Null(WithRadii(radii).Skin?.Radii?.Panel);
    }

    [AvaloniaFact]
    public void LegacyRadiiCannotOverrideFleetResourcesOrDockChrome()
    {
        ThemeService.Apply(new(), WithRadii("""{ "panel": 0, "control": 2 }"""));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new CornerRadius(2), (CornerRadius)Application.Current!.Resources["CardCornerRadius"]!);
        Assert.Equal(new CornerRadius(3), (CornerRadius)Application.Current.Resources["ControlCornerRadius"]!);
        Assert.Equal(2, DockChromeConverter.Radius);

        ThemeService.Apply(new(), WithRadii("""{ "panel": 14 }"""));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new CornerRadius(2), (CornerRadius)Application.Current.Resources["CardCornerRadius"]!);
        Assert.Equal(new CornerRadius(3), (CornerRadius)Application.Current.Resources["ControlCornerRadius"]!);
        Assert.Equal(2, DockChromeConverter.Radius);
    }

    /// <summary>Theme corner_radius and absent world metadata both retain the same Fleet corners.</summary>
    [AvaloniaFact]
    public void ASkinWithoutRadiiAndNoWorldBothUseFleetCorners()
    {
        var theme = WithRadii(null) with { CornerRadius = 14 };
        ThemeService.Apply(new(), theme);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, DockChromeConverter.Radius);

        ThemeService.Apply(new(), null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, DockChromeConverter.Radius);
    }

    /// <summary>
    /// Check real dock header borders, including the square edge facing the document, so a correct
    /// resource value cannot hide stale or missing visual bindings.
    /// </summary>
    [AvaloniaFact]
    public async Task DockHeadersKeepFleetCornersWhenLegacyThemeRequestsSquarePanels()
    {
        await using var harness = await DockHarness.OpenAsync(WithRadii("""{ "panel": 0 }"""));
        Dispatcher.UIThread.RunJobs();

        var borders = harness.Window.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Name == "PART_Border" && b.TemplatedParent is ToolChromeControl)
            .ToList();
        Assert.NotEmpty(borders);
        Assert.All(borders, border =>
        {
            var dock = Assert.IsAssignableFrom<IToolDock>(border.DataContext);
            var expected = dock.Alignment switch
            {
                Alignment.Left => new CornerRadius(2, 0, 0, 0),
                Alignment.Right => new CornerRadius(0, 2, 0, 0),
                _ => new CornerRadius(2, 2, 0, 0),
            };
            Assert.Equal(expected, border.CornerRadius);
        });
    }
}
