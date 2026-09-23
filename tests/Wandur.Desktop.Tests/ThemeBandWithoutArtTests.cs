using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Discovery;

namespace Wandur.Desktop.Tests;

/// <summary>
/// A theme that moves its toolbar into the title bar and ships no artwork still needs a title bar.
/// Reserving that room used to depend on having a window bitmap, so a themed window with no art reserved
/// nothing: the bar had no surface to paint on, the content started at the very top of the window, and
/// the nameplate floated over the transcript.
/// </summary>
public sealed class ThemeBandWithoutArtTests
{
    [AvaloniaFact]
    public async Task ReturningFromAnInBandToolbarRestoresFleetToolbarBeforeFallbackMenu()
    {
        await using var harness = await DockHarness.OpenAsync(Frameless(96));
        Dispatcher.UIThread.RunJobs(); harness.Window.UpdateLayout();
        ThemeService.Apply(new Wandur.Core.Settings.ClientSettings { Theme = "Hull", UseWorldThemes = false });
        Dispatcher.UIThread.RunJobs(); harness.Window.UpdateLayout();
        var toolbar = harness.Window.GetVisualDescendants().OfType<Avalonia.Controls.Border>().Single(b => b.Name == "MainToolbar");
        var menu = harness.Window.GetVisualDescendants().OfType<Avalonia.Controls.Menu>().Single(b => b.Name == "MainMenu");
        var stack = Assert.IsType<Avalonia.Controls.StackPanel>(toolbar.Parent);
        Assert.True(stack.Children.IndexOf(toolbar) < stack.Children.IndexOf(menu));
        Assert.True(double.IsNaN(toolbar.Height), "The old band's explicit toolbar height must not survive a theme change.");
    }

    [AvaloniaFact]
    public async Task FleetPlaqueInAWorldThemeRespectsItsOwnShortBandAndPalette()
    {
        var theme = Frameless(28);
        theme = theme with { Name = "A very long fleet world title that must stay clear of both cyan lamps", Skin = theme.Skin! with { Layout = theme.Skin.Layout! with
        { TitleBar = theme.Skin.Layout.TitleBar! with { HostsToolbar = false,
            Plaque = new WorldThemeSkinPlaque { Shape = "fleet" } } } } };
        await using var harness = await DockHarness.OpenAsync(theme);
        Dispatcher.UIThread.RunJobs(); harness.Window.UpdateLayout();
        Assert.False(FleetSkin.IsActive);
        var plaque = harness.Window.GetVisualDescendants().OfType<ThemePlaque>().Single();
        var origin = plaque.TranslatePoint(default, harness.Window)!.Value;
        Assert.True(origin.Y + plaque.Bounds.Height <= 28);
        var text = harness.Window.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>().Single(t => t.Name == "AppTitle");
        var textOrigin = text.TranslatePoint(default, plaque)!.Value;
        Assert.True(textOrigin.X >= 64, "World plaque text must stay inside the fixed lamp-safe inset.");
        Assert.True(textOrigin.X + text.Bounds.Width <= plaque.Bounds.Width - 64);
    }

    private static WorldTheme Frameless(double? height, bool plaque = true)
    {
        var node = JsonNode.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "world-theme-skin-contract.json")))!;
        var skin = node["skin"]!.AsObject();
        skin.Remove("window");
        skin.Remove("panels");
        var bar = skin["layout"]!["titlebar"]!.AsObject();
        if (height is { } value) bar["height"] = value;
        if (plaque) bar["plaque"] = JsonNode.Parse("""{ "shape": "chamfer", "cap": 6 }""");
        return JsonSerializer.Deserialize<WorldTheme>(node.ToJsonString())!;
    }

    private static ThemeWindowSkinHost Host(MainWindow window) =>
        window.GetVisualDescendants().OfType<ThemeWindowSkinHost>().First();

    [AvaloniaFact]
    public async Task ADeclaredBandIsPaintedAndReservedWithNoArtwork()
    {
        await using var harness = await DockHarness.OpenAsync(Frameless(64));
        Dispatcher.UIThread.RunJobs();

        var host = Host(harness.Window);
        host.ApplyFromTheme();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(64, host.BandHeight);
        Assert.NotNull(host.BandBrush);
        // Reserved, or the content would start underneath the bar rather than below it.
        Assert.True(host.Padding.Top >= 64 || host.Child?.Bounds.Top >= 64,
            $"the band reserved no room: padding {host.Padding}, child at {host.Child?.Bounds.Top}");
    }

    [AvaloniaFact]
    public async Task AThemeThatDeclaresNoBandKeepsTheClientsOwnChrome()
    {
        await using var harness = await DockHarness.OpenAsync(Frameless(null, plaque: false));
        Dispatcher.UIThread.RunJobs();

        var host = Host(harness.Window);
        host.ApplyFromTheme();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, host.BandHeight);
        Assert.Null(host.BandBrush);
    }
}
