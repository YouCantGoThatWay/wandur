using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Discovery;
using Wandur.Core.Settings;

namespace Wandur.Desktop.Tests;

public sealed class WorldThemeFrameViewTests
{
    // 96x96 PNG with a cyan rim so legacy asset loading has a real bitmap to retain.
    private static readonly byte[] BezelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAGAAAABgCAYAAADimHc4AAAAu0lEQVR42u3RAQ0AIAwDwRqbbyThAmSwhWvyBnqptY/eFScAACAAAAQAgAAAEAAAAgBAAABoAEA+HwAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAANABQAAACAAAAQAgAAAEAIAAABAAAALQvgvkbjNYmbBdNAAAAABJRU5ErkJggg==");

    [AvaloniaFact]
    public async Task LegacyBezelDownloadAndPaletteSwitchKeepFleetGeometry()
    {
        var path = Path.Combine(Path.GetTempPath(), "wandur-bezel-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        var framed = JsonSerializer.Deserialize<WorldTheme>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "world-theme-lotj-bezel.json")))!;
        var palette = JsonSerializer.Deserialize<WorldTheme>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "world-theme-icesus.json")))!;
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var framedWorld = new WorldListing
        {
            Id = "lotj", Name = "Legends of the Jedi", Host = "127.0.0.1", Port = port, Theme = framed,
            Summary = "A galaxy shaped by its players.", Description = "Explore distant worlds.", Tags = ["Star Wars"]
        };
        var paletteWorld = framedWorld with { Id = "icesus", Name = "Icesus", Port = port + 1, Theme = palette };
        File.WriteAllText(Path.Combine(path, "directory.json"), JsonSerializer.Serialize(
            new { schema_version = 2, format = "wandur.directory", fetched_at = DateTimeOffset.UtcNow, worlds = new[] { framedWorld, paletteWorld } },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
        var handler = new BezelHandler();
        using var http = new HttpClient(handler);
        using var catalog = new WorldCatalog(Path.Combine(path, "directory.json"), new Uri("http://127.0.0.1:8765/"), http);
        var window = new MainWindow(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), new SettingsStore(Path.Combine(path, "settings.json")),
            new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore(), catalog: catalog);
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var bezel = window.GetVisualDescendants().OfType<ThemeBezelHost>().Single(b => b.Name == "ThemeBezel");
            var shell = window.GetVisualDescendants().OfType<ThemeWindowSkinHost>().Single();
            Assert.Null(bezel.BorderBitmap);
            Assert.Equal(default, bezel.Inset);

            await window.Sessions.OpenAsync(framedWorld.ToProfile());
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(framed, window.Sessions.Active.Controller.WorldTheme);
            Assert.Equal(framed, ThemeService.AppliedWorldTheme);
            var end = DateTime.UtcNow.AddSeconds(5);
            while (ThemeService.AppliedImages?.ContainsKey("frame-border") != true && DateTime.UtcNow < end)
            { await Task.Delay(15); Dispatcher.UIThread.RunJobs(); }
            Assert.True(ThemeService.AppliedImages?.ContainsKey("frame-border") == true);
            Assert.Null(bezel.BorderBitmap);
            Assert.Equal(default, bezel.Inset);
            Assert.Equal(50, shell.BandHeight);
            Assert.Equal(6, shell.EdgeThickness);
            Assert.Null(ThemeSkinResources.FromApplied());
            Assert.True(handler.BorderRequests >= 1);
            Assert.Equal(Color.Parse("#FFFFFF"), Assert.IsAssignableFrom<ISolidColorBrush>(Application.Current!.Resources["TerminalBrush"]).Color);

            await window.Sessions.CloseAsync(window.Sessions.Active);
            Dispatcher.UIThread.RunJobs();
            Assert.Null(bezel.BorderBitmap);
            Assert.Equal(default, bezel.Inset);

            await window.Sessions.OpenAsync(paletteWorld.ToProfile());
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(40); Dispatcher.UIThread.RunJobs();
            Assert.Equal(palette, ThemeService.AppliedWorldTheme);
            Assert.Null(palette.Frame);
            Assert.Null(bezel.BorderBitmap);
            Assert.Equal(default, bezel.Inset);
            Assert.Equal(Color.Parse(palette.Colors.Terminal), Assert.IsAssignableFrom<ISolidColorBrush>(Application.Current.Resources["TerminalBrush"]).Color);
            Assert.Equal(50, shell.BandHeight);
            Assert.Equal(6, shell.EdgeThickness);
        }
        finally
        {
            window.Close();
            await window.Sessions.DisposeAsync();
            Directory.Delete(path, true);
        }
    }

    private sealed class BezelHandler : HttpMessageHandler
    {
        public int BorderRequests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.Contains("imperial-bezel", StringComparison.Ordinal))
            {
                BorderRequests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(BezelPng) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
