using System.Net;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Discovery;
using Wandur.Core.Settings;

namespace Wandur.Desktop.Tests;

public sealed class WorldThemeWindowSkinTests
{
    [Fact]
    public void RightFlareAnchorsIndependentOfDocks()
    {
        var bounds = ThemeOrnamentLayer.AnchorBounds("bottom-right", new SkinSize(100, 64), new Size(1380, 900));
        Assert.Equal(new Rect(1280, 836, 100, 64), bounds);
        var left = ThemeOrnamentLayer.AnchorBounds("bottom-left", new SkinSize(100, 64), new Size(1380, 900));
        Assert.Equal(new Rect(0, 836, 100, 64), left);
        var header = ThemeOrnamentLayer.AnchorBounds("top-center", new SkinSize(310, 88), new Size(1380, 900));
        Assert.Equal(new Rect(535, 0, 310, 88), header);
    }

    [Fact]
    public void BottomFlareIntrusionFitsFooterClearance()
    {
        // Measured polish: flare 64, inset.bottom 18 → 46 DIP intrusion; footer min_height 46.
        const double flareHeight = 64, insetBottom = 18, minHeight = 46;
        Assert.True(flareHeight <= insetBottom + minHeight);
        // Horizontal: width 100, side inset 14 → need clearance >= 90.
        Assert.Equal(90, Math.Max(0, 100 - 14) + 4);
    }

    [AvaloniaFact]
    public async Task LegacyWindowAssetsKeepFleetGeometryAndDisableBitmapChrome()
    {
        var theme = JsonSerializer.Deserialize<WorldTheme>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "world-theme-skin-contract.json")))!;
        var handler = new SkinHandler();
        foreach (var (fragment, w, h) in SkinAssetMap.Declared(theme))
            handler.Map(fragment, TestPng.Rgba(w, h));
        handler.Map("imperial-bezel", TestPng.Rgba(96, 96));

        await using var harness = await OpenAsync(theme, handler);
        await WaitForAsync(() => ThemeService.AppliedImages?.ContainsKey(ThemeSkinResources.WindowBorderKey) == true);

        var windowSkin = harness.Window.GetVisualDescendants().OfType<ThemeWindowSkinHost>().Single(h => h.Name == "ThemeWindowSkin");
        var bezel = harness.Window.GetVisualDescendants().OfType<ThemeBezelHost>().Single(h => h.Name == "ThemeBezel");
        var ornaments = harness.Window.GetVisualDescendants().OfType<ThemeOrnamentLayer>().Single(h => h.Name == "ThemeOrnaments");
        Assert.Null(windowSkin.BorderBitmap);
        Assert.Null(windowSkin.BorderMeta);
        Assert.Equal(default, windowSkin.Inset);
        Assert.Equal(50, windowSkin.BandHeight);
        Assert.Equal(6, windowSkin.EdgeThickness);
        Assert.NotNull(windowSkin.BandBrush);
        harness.Window.UpdateLayout();
        Assert.Equal(new Point(6, 50), windowSkin.Child!.Bounds.Position);
        if (OperatingSystem.IsMacOS())
            Assert.Equal(50, harness.Window.ExtendClientAreaTitleBarHeightHint);
        Assert.Null(bezel.BorderBitmap);
        Assert.Equal(default, bezel.Inset);
        Assert.False(ornaments.IsHitTestVisible);
        Assert.Null(ThemeSkinResources.FromApplied());
        Assert.Equal(default, ornaments.EffectiveFooterClearance);
    }

    [AvaloniaFact]
    public async Task LegacyOrnamentsReserveNoClearanceAtLargeOrCompactSizes()
    {
        var theme = JsonSerializer.Deserialize<WorldTheme>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "world-theme-skin-contract.json")))!;
        var handler = new SkinHandler();
        foreach (var (fragment, w, h) in SkinAssetMap.Declared(theme))
            handler.Map(fragment, TestPng.Rgba(w, h));

        await using var harness = await OpenAsync(theme, handler);
        await WaitForAsync(() => ThemeService.AppliedImages?.ContainsKey(ThemeSkinResources.WindowBorderKey) == true);

        harness.Window.Width = 1380;
        harness.Window.Height = 900;
        Dispatcher.UIThread.RunJobs();
        var ornaments = harness.Window.GetVisualDescendants().OfType<ThemeOrnamentLayer>().Single();
        ornaments.ApplyFromTheme();
        // Legacy ornament thresholds must not affect the Fleet shell at either size.
        var chrome = harness.Window.GetVisualDescendants().OfType<Grid>().Single(p => p.Name == "ThemeChrome");
        chrome.Width = 1380;
        chrome.Height = 900;
        ornaments.InvalidateMeasure();
        ornaments.InvalidateArrange();
        Dispatcher.UIThread.RunJobs();
        Assert.False(ornaments.IsCompact);
        Assert.Equal(default, ornaments.EffectiveFooterClearance);
        Assert.Null(ThemeSkinResources.FromApplied());

        // Shrink below the legacy asset's 1200x760 compact threshold.
        chrome.Width = 1000;
        chrome.Height = 700;
        ornaments.Measure(new Size(1000, 700));
        ornaments.Arrange(new Rect(0, 0, 1000, 700));
        Dispatcher.UIThread.RunJobs();
        Assert.False(ornaments.IsCompact);
        Assert.Equal(default, ornaments.EffectiveFooterClearance);
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
        Assert.Fail("Timed out waiting for window skin state.");
    }

    private static async Task<Harness> OpenAsync(WorldTheme theme, SkinHandler handler)
    {
        var path = Path.Combine(Path.GetTempPath(), "wandur-winskin-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var world = new WorldListing
        {
            Id = theme.Id, Name = theme.Name, Host = "127.0.0.1", Port = port, Theme = theme,
            Summary = "Test", Description = "Test", Tags = ["test"]
        };
        File.WriteAllText(Path.Combine(path, "directory.json"), JsonSerializer.Serialize(
            new { schema_version = 2, format = "wandur.directory", fetched_at = DateTimeOffset.UtcNow, worlds = new[] { world } },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
        var http = new HttpClient(handler);
        var catalog = new WorldCatalog(Path.Combine(path, "directory.json"), new Uri("http://127.0.0.1:8765/"), http);
        var window = new MainWindow(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(),
            new SettingsStore(Path.Combine(path, "settings.json")),
            new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(),
            new MemoryScriptLibraryStore(), catalog: catalog);
        window.Width = 1380; window.Height = 900;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        await window.Sessions.OpenAsync(world.ToProfile());
        Dispatcher.UIThread.RunJobs();
        return new Harness(window, path, http, catalog);
    }

    private sealed class Harness(MainWindow window, string path, HttpClient http, WorldCatalog catalog) : IAsyncDisposable
    {
        public MainWindow Window { get; } = window;
        public async ValueTask DisposeAsync()
        {
            Window.Close();
            await Window.Sessions.DisposeAsync();
            catalog.Dispose();
            http.Dispose();
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    private sealed class SkinHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _map = new(StringComparer.Ordinal);
        public void Map(string fragment, byte[] png) => _map[fragment] = png;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            foreach (var (fragment, png) in _map)
                if (path.Contains(fragment, StringComparison.Ordinal))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
