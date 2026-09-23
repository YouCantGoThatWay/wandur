using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Wandur.Core.Discovery;

namespace Wandur.Desktop.Tests;

/// <summary>
/// A skin made of shaded surfaces and nothing else has to reach the controls. The brushes are correct long
/// before that is true: they are written to resources a control only picks up if nothing later overrides
/// it, which is exactly the kind of gap that looks from the outside like the theme never changed.
/// </summary>
public sealed class ThemeSkinSurfaceRenderTests
{
    private static WorldTheme SurfacesOnly()
    {
        var node = JsonNode.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "world-theme-skin-contract.json")))!;
        var skin = node["skin"]!.AsObject();
        skin.Remove("window");
        skin.Remove("panels");
        skin["surfaces"] = JsonNode.Parse("""
        {
          "titlebar":     { "from": "#8A97A6", "to": "#3A434E", "gloss": 0.45, "bevel": "raised" },
          "panel_header": { "from": "#CBD6E1", "to": "#7F8D9C", "gloss": 0.4,  "bevel": "raised" },
          "panel_body":   { "from": "#F4F7FA", "to": "#E3EAF1" },
          "footer":       { "from": "#C2CDD8", "to": "#7F8D9C", "gloss": 0.35, "bevel": "raised" }
        }
        """);
        return JsonSerializer.Deserialize<WorldTheme>(node.ToJsonString())!;
    }

    private static LinearGradientBrush Gradient(object? value, string key)
    {
        Assert.True(value is LinearGradientBrush,
            $"{key} should be a gradient once the skin names that surface, was {value?.GetType().Name ?? "null"}");
        return (LinearGradientBrush)value!;
    }

    [AvaloniaFact]
    public void NamedSurfacesBecomeGradientResources()
    {
        var theme = SurfacesOnly();
        ThemeService.Apply(new(), theme);
        Dispatcher.UIThread.RunJobs();

        foreach (var key in new[] { "DockHeaderBrush", "DockSurfaceHeaderBrush", "DockSurfaceHeaderActiveBrush" })
        {
            var brush = Gradient(Avalonia.Application.Current!.Resources[key], key);
            Assert.Equal(Color.Parse("#CBD6E1"), brush.GradientStops[1].Color);   // after the bevel stop
            Assert.Equal(Color.Parse("#7F8D9C"), brush.GradientStops[^2].Color);
        }
        var chrome = Gradient(Avalonia.Application.Current!.Resources["ChromeBrush"], "ChromeBrush");
        Assert.Equal(Color.Parse("#8A97A6"), chrome.GradientStops[1].Color);
    }

    /// <summary>The hard step at the midline is what reads as machined metal; a smooth fade does not.</summary>
    [AvaloniaFact]
    public void GlossPutsAHardStepAtTheMidline()
    {
        var brush = (LinearGradientBrush)ThemeSkinSurfaces.Build(
            new WorldThemeSkinSurface { From = "#CBD6E1", To = "#7F8D9C", Gloss = 0.4, Bevel = "raised" })!;
        var below = brush.GradientStops.Single(s => Math.Abs(s.Offset - 0.499) < 1e-9);
        var above = brush.GradientStops.Single(s => Math.Abs(s.Offset - 0.5) < 1e-9);
        Assert.NotEqual(below.Color, above.Color);
        Assert.Equal(0d, brush.GradientStops[0].Offset);
        Assert.Equal(1d, brush.GradientStops[^1].Offset);
    }

    [AvaloniaFact]
    public void ASurfaceTheSkinDoesNotNameIsLeftAlone()
    {
        Assert.Null(ThemeSkinSurfaces.Build(null));
        Assert.Null(ThemeSkinSurfaces.Build(new WorldThemeSkinSurface { From = "nope", To = "#7F8D9C" }));
    }

    /// <summary>
    /// The one that matters: the dock header the reader actually looks at must end up painted with the
    /// skin's gradient, not merely have it sitting in a resource dictionary.
    /// </summary>
    [AvaloniaFact]
    public async Task ThePanelHeaderIsPaintedWithTheSkinsGradient()
    {
        var theme = SurfacesOnly();
        await using var harness = await DockHarness.OpenAsync(theme);
        Dispatcher.UIThread.RunJobs();

        var chromes = harness.Window.GetVisualDescendants().OfType<ToolChromeControl>().ToList();
        Assert.NotEmpty(chromes);

        var grips = chromes
            .SelectMany(c => c.GetVisualDescendants().OfType<Grid>())
            .Where(g => g.Name == "PART_Grip")
            .ToList();
        Assert.NotEmpty(grips);
        Assert.All(grips, grip => Gradient(grip.Background, "PART_Grip.Background"));
    }

    /// <summary>
    /// The shipped theme, unmodified: window frame and surfaces together. Stripping the window made the
    /// earlier case pass while saying nothing about the combination the directory actually serves, and a
    /// frame host that repaints dock chrome would be invisible to it.
    /// </summary>
    [AvaloniaFact]
    public async Task TheShippedThemePaintsItsPanelHeadersToo()
    {
        var theme = JsonSerializer.Deserialize<WorldTheme>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "world-theme-industrial-skin.json")))!;
        // The shipped theme is colours alone; its headers are shaded by the client's default from those colours.

        var handler = new SkinHandler();
        foreach (var (fragment, w, h) in SkinAssetMap.Declared(theme))
            handler.Map(fragment, TestPng.Rgba(w, h));

        await using var harness = await DockHarness.OpenAsync(theme, handler);
        Dispatcher.UIThread.RunJobs();

        var grips = harness.Window.GetVisualDescendants().OfType<ToolChromeControl>()
            .SelectMany(c => c.GetVisualDescendants().OfType<Grid>())
            .Where(g => g.Name == "PART_Grip")
            .ToList();
        Assert.NotEmpty(grips);
        Assert.All(grips, grip => Gradient(grip.Background, "PART_Grip.Background"));
    }

    /// <summary>Answers for whatever assets a theme declares; a surfaces-only skin needs none.</summary>
    private sealed class SkinHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _map = new(StringComparer.Ordinal);
        public void Map(string fragment, byte[] png) => _map[fragment] = png;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            foreach (var (fragment, png) in _map)
                if (path.Contains(fragment, StringComparison.Ordinal))
                    return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    { Content = new ByteArrayContent(png) });
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }
}
