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
