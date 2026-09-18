using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Wandur.Core.Discovery;
using Wandur.Core.Settings;

namespace Wandur.Desktop.Tests;

public sealed class IcesusThemeTests
{
    [AvaloniaFact]
    public async Task CuratedThemeIsAdoptedByAnExistingConnectionAndRendersWithSolidReadableTranscript()
    {
        var path = Path.Combine(Path.GetTempPath(), "wandur-icesus-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        var theme = JsonSerializer.Deserialize<WorldTheme>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "world-theme-icesus.json")))!;
        using var server = new TcpListener(IPAddress.Loopback, 0); server.Start();
        var world = new WorldListing { Id = "icesus-preview", Name = "Icesus MUD", Host = "127.0.0.1",
            Port = ((IPEndPoint)server.LocalEndpoint).Port, Theme = theme };
        var profile = world.ToProfile() with { Theme = null };
        var store = new SettingsStore(Path.Combine(path, "settings.json"));
        store.Save(new ClientSettings { Theme = "Paper", Profiles = [profile] });
        var json = JsonSerializer.Serialize(new { schema_version = 2, format = "wandur.directory", fetched_at = DateTimeOffset.UtcNow,
            worlds = new[] { world } }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        File.WriteAllText(Path.Combine(path, "directory.json"), json);
        using var http = new HttpClient(new NoNetwork());
        using var catalog = new WorldCatalog(Path.Combine(path, "directory.json"), http: http);
        var window = new MainWindow(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), store, new MemoryPasswordVault(),
            new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore(), catalog: catalog);
        try
        {
            window.Show(); await window.Sessions.OpenAsync(profile);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var socket = await server.AcceptTcpClientAsync(timeout.Token);
            await socket.GetStream().WriteAsync(Encoding.UTF8.GetBytes(
                "\r\n  ICESUS   |   Beyond the ice.\r\n\r\n" +
                "  An online adventure in a frozen world.\r\n\r\n" +
                "  Theme preview using sample text.\r\n" +
                "  The wind moves through the snow-covered valley.\r\n" +
                "  A lantern glows beside the inn's open door.\r\n\r\n" +
                "  Exits: \u001b[36mnorth east south\u001b[0m\r\n\r\n"), timeout.Token);
            for (var i = 0; i < 10; i++) { await Task.Delay(10, timeout.Token); Dispatcher.UIThread.RunJobs(); window.Controller.FlushOutput(); }
            Assert.Equal(theme, window.Controller.WorldTheme);
            Assert.Equal(theme, Assert.Single(store.Load().Settings.Profiles).Theme);
            Assert.Equal("Paper", store.Load().Settings.Theme);
            Assert.Equal(Color.Parse(theme.Colors.Terminal), Assert.IsAssignableFrom<ISolidColorBrush>(Application.Current!.Resources["TerminalBrush"]).Color);
            Assert.Equal(Color.Parse(theme.Colors.Panel), Assert.IsAssignableFrom<ISolidColorBrush>(Application.Current.Resources["PanelBrush"]).Color);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
            if (Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR") is { } captures)
            {
                Directory.CreateDirectory(captures);
                frame.Save(Path.Combine(captures, "icesus-workspace.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
        }
        finally { window.Close(); await window.Sessions.DisposeAsync(); Directory.Delete(path, true); }
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
