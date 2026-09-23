using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Discovery;

namespace Wandur.Desktop.Tests;

/// <summary>
/// A theme is allowed to want no window frame. The legacy bezel exists to stand in when modular window art
/// fails to load, and it cannot tell the difference between that and a theme that removed its frame on
/// purpose, so removing the frame put the old bezel straight back on screen.
/// </summary>
public sealed class ThemeFramelessSkinTests
{
    private static WorldTheme Frameless()
    {
        var node = JsonNode.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "world-theme-skin-contract.json")))!;
        var skin = node["skin"]!.AsObject();
        skin.Remove("window");
        skin.Remove("panels");
        var theme = JsonSerializer.Deserialize<WorldTheme>(node.ToJsonString())!;
        Assert.NotNull(theme.Frame);                 // the legacy bezel is still declared
        Assert.Null(theme.Skin!.Window);
        Assert.True(theme.Skin.HasContent);
        return theme;
    }

    [AvaloniaFact]
    public async Task ASkinThatNamesNoWindowGetsNoLegacyBezel()
    {
        await using var harness = await DockHarness.OpenAsync(Frameless());
        Dispatcher.UIThread.RunJobs();

        Assert.Null(ThemeService.AppliedImages?.GetValueOrDefault("frame-border"));
        foreach (var bezel in harness.Window.GetVisualDescendants().OfType<ThemeBezelHost>())
        {
            bezel.ApplyFromTheme();
            Assert.Null(bezel.BorderBitmap);
            Assert.Equal(default, bezel.Inset);
        }
    }

    /// <summary>The fallback still has to work: a window the theme wanted, whose art never arrived.</summary>
    [AvaloniaFact]
    public async Task AWindowWhoseArtFailsStillFallsBackToTheBezel()
    {
        var theme = JsonSerializer.Deserialize<WorldTheme>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "world-theme-skin-contract.json")))!;
        Assert.NotNull(theme.Skin!.Window);

        // No handler maps the window art, so every asset 404s and the modular border cannot load.
        await using var harness = await DockHarness.OpenAsync(theme, new MissingAssetHandler());
        await WaitForAsync(() => ThemeService.AppliedImages is not null);
        Assert.False(ThemeSkinResources.FromApplied() is { WindowReady: true });
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var end = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < end)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return;
            await Task.Delay(15);
        }
        Assert.Fail("Timed out waiting for the theme to settle.");
    }

    private sealed class MissingAssetHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }
}
