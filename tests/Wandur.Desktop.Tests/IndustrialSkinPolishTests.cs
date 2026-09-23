using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Discovery;
using Wandur.Core.Settings;

namespace Wandur.Desktop.Tests;

/// <summary>Industrial skin polish: dark terminal on a light frame, terminal-field chrome, multi-size captures.</summary>
public sealed class IndustrialSkinPolishTests
{
    /// <summary>
    /// The contract fixture, not a shipped theme: these exercise panel and overlay skinning, which a live
    /// theme is free to stop using. Pointing them at whatever wandur.net serves today made them fail when
    /// the art was retuned, which says nothing about the code under test.
    /// </summary>
    private static WorldTheme Theme => JsonSerializer.Deserialize<WorldTheme>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "world-theme-industrial-skin.json")))!;

    private static string ColorOf(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color.ToString().ToLowerInvariant();
    private static string ColorOf(Color color) => color.ToString().ToLowerInvariant();

    private static string IndustrialAssetDir()
    {
        // Site hosts production PNGs; walk up from the test bin to the workspace.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "wandur-site", "src", "Wandur.Site", "wwwroot", "themes", "industrial-v2");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "MANIFEST.json")))
                return candidate;
            // From wandur-client/tests/.../bin
            candidate = Path.Combine(dir.FullName, "..", "..", "..", "..", "..", "wandur-site", "src", "Wandur.Site", "wwwroot", "themes", "industrial-v2");
            candidate = Path.GetFullPath(candidate);
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "MANIFEST.json")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("industrial-v2 theme assets not found beside the workspace.");
    }

    [AvaloniaFact]
    public async Task IndustrialSkinUsesDarkTerminalAndTerminalFieldChrome()
    {
        var path = Path.Combine(Path.GetTempPath(), "wandur-industrial-polish-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var world = new WorldListing
        {
            Id = "industrial", Name = "Industrial", Host = "127.0.0.1",
            Port = ((IPEndPoint)listener.LocalEndpoint).Port, Theme = Theme,
            Summary = "Polish", Description = "Light surfaces under painted metal."
        };
        var store = new SettingsStore(Path.Combine(path, "settings.json"));
        store.Save(new ClientSettings { Theme = "Paper", Profiles = [world.ToProfile() with { Theme = Theme }] });
        File.WriteAllText(Path.Combine(path, "directory.json"), JsonSerializer.Serialize(
            new { schema_version = 2, format = "wandur.directory", fetched_at = DateTimeOffset.UtcNow, worlds = new[] { world } },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));

        var assets = IndustrialAssetDir();
        var handler = new AssetHandler(assets);
        var http = new HttpClient(handler);
        using var catalog = new WorldCatalog(Path.Combine(path, "directory.json"), new Uri("http://127.0.0.1:8765/"), http);
        var window = new MainWindow(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), store,
            new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(),
            new MemoryScriptLibraryStore(), catalog: catalog);
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            await window.Sessions.OpenAsync(window.Controller.Settings.Profiles[0]);
            using var _ = await listener.AcceptTcpClientAsync();
            await WaitForAsync(() => ThemeService.AppliedWorldTheme?.Id == Theme.Id);
            Dispatcher.UIThread.RunJobs();

            // Read from the theme rather than pinned: retuning a palette is not a regression, and pinning
            // these made a colour change look like a broken test every time the skin moved.
            var terminal = ColorOf(Color.Parse(Theme.Colors.Terminal));
            var terminalText = ColorOf(Color.Parse(Theme.Colors.TerminalText));
            Assert.Equal(terminal, ColorOf((IBrush)Application.Current!.Resources["TerminalBrush"]!));
            Assert.Equal(terminalText, ColorOf((IBrush)Application.Current.Resources["TerminalTextBrush"]!));
            // The transcript must stay the darkest surface in the window, whichever way the palette goes.
            Assert.False(UserTheme.IsLightBackground(Theme.Colors.Terminal));
            Assert.Equal(UserTheme.IsLightBackground(Theme.Colors.Shell),
                UserTheme.IsLightBackground(Theme.Colors.Panel));
            Assert.True(Application.Current.Resources.ContainsKey("TerminalPlaceholderForeground"));

            var input = window.GetVisualDescendants().OfType<TextBox>().Single(b => b.Name == "CommandInput");
            Assert.Contains(input.Classes, c => c == "terminal-field");
            Assert.Equal(terminalText, ColorOf(input.CaretBrush));
            Assert.Equal(terminal, ColorOf(input.Background));
            // Fluent paints the visible fill on PART_BorderElement; ensure focus does not bleach it to shell.
            input.Focus();
            Dispatcher.UIThread.RunJobs();
            var border = input.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "PART_BorderElement");
            Assert.NotNull(border);
            Assert.Equal(terminal, ColorOf(border!.Background));

            // The panel header slot still places the dock header row, with or without artwork behind it.
            // The shipped theme is colours alone, so the slots in force are the client's default's.
            var applied = ThemeService.AppliedSkin!;
            Assert.Equal(applied.Layout!.PanelHeader!.Height, (double)Application.Current!.Resources["DockHeaderHeight"]!);
            var slot = applied.Layout.PanelHeader!;
            var body = applied.Panels?.Default?.Inset ?? default;
            var headerMargin = (Thickness)Application.Current.Resources["DockHeaderMargin"]!;
            Assert.Equal(slot.Inset.Left - body.Left, headerMargin.Left);
            Assert.Equal(slot.Inset.Right - body.Right, headerMargin.Right);

            // The shaded surfaces this theme is made of must reach the chrome the reader looks at.
            Assert.IsType<LinearGradientBrush>(Application.Current.Resources["DockHeaderBrush"]);
            Assert.IsType<LinearGradientBrush>(Application.Current.Resources["ChromeBrush"]);

            // Everything the painted band does (plaque placement, the toolbar in the band, ornament
            // clearance) is exercised by WorldThemeWindowSkinTests against the contract fixture, which has
            // a band. This theme ships no artwork, so there is nothing here to measure.
        }
        finally
        {
            await window.Sessions.DisposeAsync(); window.Close(); http.Dispose();
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var end = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < end)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return;
            await Task.Delay(20);
        }
        Assert.Fail("Timed out waiting for industrial skin images.");
    }

    private sealed class AssetHandler(string assetDir) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var file = Path.GetFileName(request.RequestUri!.AbsolutePath);
            var path = Path.Combine(assetDir, file);
            if (File.Exists(path))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(File.ReadAllBytes(path))
                });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
