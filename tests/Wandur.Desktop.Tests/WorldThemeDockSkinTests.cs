using System.Net;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Wandur.Core.Discovery;
using Wandur.Core.Settings;

namespace Wandur.Desktop.Tests;

public sealed class WorldThemeDockSkinTests
{
    [AvaloniaFact]
    public async Task PanelSkinWrapsToolDocksWithoutResettingLayout()
    {
        var theme = JsonSerializer.Deserialize<WorldTheme>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "world-theme-industrial-skin.json")))!;
        var handler = new SkinHandler();
        handler.Map("window-border.png", TestPng.Rgba(512, 384));
        handler.Map("dock-panel.png", TestPng.Rgba(512, 1024));
        handler.Map("header.png", TestPng.Rgba(320, 64));
        handler.Map("corner-left.png", TestPng.Rgba(240, 128));
        handler.Map("corner-right.png", TestPng.Rgba(240, 128));

        await using var harness = await OpenAsync(theme, handler);
        await WaitForAsync(() => ThemeService.AppliedImages?.ContainsKey(ThemeSkinResources.PanelDefaultKey) == true);

        var skins = harness.Window.GetVisualDescendants().OfType<ThemeDockSkinHost>().ToList();
        Assert.True(skins.Count >= 3, $"expected left/map/channels skin hosts, got {skins.Count}");
        Assert.Contains(skins, s => s.IsSkinActive && s.BorderBitmap is not null);

        var library = Assert.IsAssignableFrom<IToolDock>(harness.Window.Workspace.WorldsTool!.Owner);
        var layout = Assert.IsAssignableFrom<IProportionalDock>(library.Owner);
        Assert.Equal(new[] { "left", "splitter", "documents", "splitter", "right" }, LayoutShape(layout));

        // Closing both right tools still collapses the column.
        harness.Window.ToggleMap();
        harness.Window.ToggleChannels();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        Assert.Equal(new[] { "left", "splitter", "documents" }, LayoutShape(layout));
        Assert.DoesNotContain(layout.VisibleDockables!, item => item.Id == "right");

        harness.Window.ToggleMap();
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(layout.VisibleDockables!, item => item.Id == "right");
    }

    [AvaloniaFact]
    public async Task SkinHostPreservesCloseAndTitleParts()
    {
        var theme = JsonSerializer.Deserialize<WorldTheme>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "world-theme-industrial-skin.json")))!;
        var handler = new SkinHandler();
        handler.Map("window-border.png", TestPng.Rgba(512, 384));
        handler.Map("dock-panel.png", TestPng.Rgba(512, 1024));
        handler.Map("header.png", TestPng.Rgba(320, 64));
        handler.Map("corner-left.png", TestPng.Rgba(240, 128));
        handler.Map("corner-right.png", TestPng.Rgba(240, 128));

        await using var harness = await OpenAsync(theme, handler);
        await WaitForAsync(() => ThemeService.AppliedImages?.ContainsKey(ThemeSkinResources.PanelDefaultKey) == true);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);

        var leftChrome = harness.Window.GetVisualDescendants().OfType<ToolChromeControl>()
            .Single(c => c.DataContext is IToolDock dock && dock.Alignment == Alignment.Left);
        Assert.NotNull(leftChrome.GetVisualDescendants().OfType<Button>().SingleOrDefault(b => b.Name == "PART_CloseButton"));
        Assert.NotNull(leftChrome.GetVisualDescendants().OfType<TextBlock>().SingleOrDefault(t => t.Name == "PART_Title"));
        Assert.NotNull(leftChrome.GetVisualDescendants().OfType<Grid>().SingleOrDefault(g => g.Name == "PART_Grip"));
        Assert.Contains(leftChrome.GetSelfAndVisualAncestors().OfType<ThemeDockSkinHost>(), h => h.IsSkinActive);
    }

    private static string[] LayoutShape(IDock dock) =>
        dock.VisibleDockables!.Select(item => item is IProportionalDockSplitter ? "splitter" : item.Id ?? "?").ToArray();

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var end = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < end)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return;
            await Task.Delay(15);
        }
        Assert.Fail("Timed out waiting for dock skin.");
    }

    private static async Task<Harness> OpenAsync(WorldTheme theme, SkinHandler handler)
    {
        var path = Path.Combine(Path.GetTempPath(), "wandur-dockskin-" + Guid.NewGuid());
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
            new MemoryScriptLibraryStore(), catalog: catalog) { Width = 1380, Height = 900 };
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
