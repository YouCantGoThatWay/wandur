using System.Net;
using System.Text.Json;
using Avalonia.Threading;
using Wandur.Core.Discovery;
using Wandur.Core.Settings;

namespace Wandur.Desktop.Tests;

/// <summary>
/// Opens a real window on a world carrying a theme, with no artwork server: a skin made of shaded
/// surfaces downloads nothing, so the tests for it must not need a handler to answer for assets.
/// </summary>
internal sealed class DockHarness(MainWindow window, string path, HttpClient http, WorldCatalog catalog)
    : IAsyncDisposable
{
    public MainWindow Window { get; } = window;

    public static async Task<DockHarness> OpenAsync(WorldTheme theme, HttpMessageHandler? handler = null)
    {
        var path = Path.Combine(Path.GetTempPath(), "wandur-surface-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var world = new WorldListing
        {
            Id = theme.Id, Name = theme.Name, Host = "127.0.0.1", Port = port, Theme = theme,
            Summary = "Test", Description = "Test", Tags = ["test"],
        };
        File.WriteAllText(Path.Combine(path, "directory.json"), JsonSerializer.Serialize(
            new { schema_version = 2, format = "wandur.directory", fetched_at = DateTimeOffset.UtcNow, worlds = new[] { world } },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));

        var http = handler is null ? new HttpClient() : new HttpClient(handler);
        var catalog = new WorldCatalog(Path.Combine(path, "directory.json"), new Uri("http://127.0.0.1:8765/"), http);
        var window = new MainWindow(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(),
            new SettingsStore(Path.Combine(path, "settings.json")),
            new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(),
            new MemoryScriptLibraryStore(), catalog: catalog) { Width = 1380, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        await window.Sessions.OpenAsync(world.ToProfile());
        Dispatcher.UIThread.RunJobs();
        return new DockHarness(window, path, http, catalog);
    }

    public async ValueTask DisposeAsync()
    {
        Window.Close();
        await Window.Sessions.DisposeAsync();
        catalog.Dispose();
        http.Dispose();
        try { Directory.Delete(path, true); } catch (IOException) { }
    }
}
