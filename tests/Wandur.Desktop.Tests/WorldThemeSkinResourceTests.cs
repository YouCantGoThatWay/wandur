using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Wandur.Core.Discovery;
using Wandur.Core.Settings;

namespace Wandur.Desktop.Tests;

public sealed class WorldThemeSkinResourceTests
{
    private static WorldTheme Industrial =>
        JsonSerializer.Deserialize<WorldTheme>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "world-theme-industrial-skin.json")))!;

    [AvaloniaFact]
    public async Task PanelUrlIsRequestedOnceEvenWithSharedKey()
    {
        var theme = Industrial;
        var handler = new SkinHandler();
        handler.Map("dock-panel.png", TestPng.Rgba(512, 1024));
        handler.Map("window-border.png", TestPng.Rgba(512, 384));
        handler.Map("header.png", TestPng.Rgba(212, 56));
        handler.Map("corner-left.png", TestPng.Rgba(200, 128));
        handler.Map("corner-right.png", TestPng.Rgba(200, 128));

        await using var harness = await SkinHarness.OpenAsync(theme, handler);
        await harness.WaitForKey(ThemeSkinResources.WindowBorderKey);
        Assert.Equal(1, handler.Count("dock-panel.png"));
        Assert.Equal(1, handler.Count("window-border.png"));
        Assert.True(ThemeService.AppliedImages!.ContainsKey(ThemeSkinResources.PanelDefaultKey));
        Assert.True(ThemeService.AppliedImages.ContainsKey(ThemeSkinResources.WindowBorderKey));
        Assert.False(ThemeService.AppliedImages.ContainsKey("frame-border"));
        var skin = ThemeSkinResources.FromApplied();
        Assert.NotNull(skin);
        Assert.True(skin!.WindowReady);
        Assert.True(skin.PanelReady);
        Assert.Equal(3, skin.Overlays.Count);
    }

    [AvaloniaFact]
    public async Task MismatchedSourceSizeDropsThatComponentKeepsSiblings()
    {
        var theme = Industrial;
        var handler = new SkinHandler();
        handler.Map("window-border.png", TestPng.Rgba(64, 64)); // wrong vs 512x384
        handler.Map("dock-panel.png", TestPng.Rgba(512, 1024));
        handler.Map("header.png", TestPng.Rgba(212, 56));
        handler.Map("corner-left.png", TestPng.Rgba(200, 128));
        handler.Map("corner-right.png", TestPng.Rgba(200, 128));
        // Legacy fallback still present on the theme.
        handler.Map("imperial-bezel", TestPng.Rgba(96, 96));

        await using var harness = await SkinHarness.OpenAsync(theme, handler);
        var end = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < end)
        {
            Dispatcher.UIThread.RunJobs();
            if (ThemeService.AppliedImages is { } images &&
                (images.ContainsKey("frame-border") || images.ContainsKey(ThemeSkinResources.PanelDefaultKey)))
                break;
            await Task.Delay(15);
        }
        Assert.False(ThemeService.AppliedImages?.ContainsKey(ThemeSkinResources.WindowBorderKey) == true);
        Assert.True(ThemeService.AppliedImages?.ContainsKey(ThemeSkinResources.PanelDefaultKey) == true);
        Assert.True(ThemeService.AppliedImages?.ContainsKey("frame-border") == true);
    }

    [AvaloniaFact]
    public async Task BrokenOverlayDoesNotGateWindowBorder()
    {
        var theme = Industrial;
        var handler = new SkinHandler();
        handler.Map("window-border.png", TestPng.Rgba(512, 384));
        handler.Map("dock-panel.png", TestPng.Rgba(512, 1024));
        handler.Map("header.png", TestPng.Rgba(10, 10)); // bad aspect vs destination
        handler.Map("corner-left.png", TestPng.Rgba(200, 128));
        handler.Map("corner-right.png", TestPng.Rgba(200, 128));

        await using var harness = await SkinHarness.OpenAsync(theme, handler);
        await harness.WaitForKey(ThemeSkinResources.WindowBorderKey);
        Assert.True(ThemeService.AppliedImages!.ContainsKey(ThemeSkinResources.WindowBorderKey));
        Assert.False(ThemeService.AppliedImages.ContainsKey(ThemeSkinResources.OverlayKey("header")));
        Assert.True(ThemeService.AppliedImages.ContainsKey(ThemeSkinResources.OverlayKey("left-flare")));
    }

    [AvaloniaFact]
    public async Task StaleThemeCompletionDoesNotRepaintActiveSession()
    {
        var industrial = Industrial;
        var palette = JsonSerializer.Deserialize<WorldTheme>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "world-theme-icesus.json")))!;
        var handler = new SkinHandler { DelayMs = 80 };
        handler.Map("window-border.png", TestPng.Rgba(512, 384));
        handler.Map("dock-panel.png", TestPng.Rgba(512, 1024));
        handler.Map("header.png", TestPng.Rgba(212, 56));
        handler.Map("corner-left.png", TestPng.Rgba(200, 128));
        handler.Map("corner-right.png", TestPng.Rgba(200, 128));

        await using var harness = await SkinHarness.OpenAsync(industrial, handler);
        // Switch to palette-only before industrial images finish.
        await harness.Window.Sessions.CloseAsync(harness.Window.Sessions.Active);
        Dispatcher.UIThread.RunJobs();
        await harness.OpenWorld(palette with { Id = "icesus-palette" }, portOffset: 1);
        await Task.Delay(200);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("icesus-palette", ThemeService.AppliedWorldTheme?.Id);
        Assert.False(ThemeService.AppliedImages?.ContainsKey(ThemeSkinResources.WindowBorderKey) == true);
    }

    private sealed class SkinHarness : IAsyncDisposable
    {
        public MainWindow Window { get; }
        private readonly string _path;
        private readonly HttpClient _http;
        private readonly WorldCatalog _catalog;

        private SkinHarness(MainWindow window, string path, HttpClient http, WorldCatalog catalog)
        {
            Window = window; _path = path; _http = http; _catalog = catalog;
        }

        public static async Task<SkinHarness> OpenAsync(WorldTheme theme, SkinHandler handler)
        {
            var path = Path.Combine(Path.GetTempPath(), "wandur-skin-" + Guid.NewGuid());
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
            window.Show();
            Dispatcher.UIThread.RunJobs();
            await window.Sessions.OpenAsync(world.ToProfile());
            Dispatcher.UIThread.RunJobs();
            return new SkinHarness(window, path, http, catalog);
        }

        public async Task OpenWorld(WorldTheme theme, int portOffset)
        {
            using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var world = new WorldListing
            {
                Id = theme.Id, Name = theme.Name, Host = "127.0.0.1", Port = port, Theme = theme,
                Summary = "Test", Description = "Test", Tags = ["test"]
            };
            await Window.Sessions.OpenAsync(world.ToProfile());
            Dispatcher.UIThread.RunJobs();
        }

        public async Task WaitForKey(string key)
        {
            var end = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < end)
            {
                Dispatcher.UIThread.RunJobs();
                if (ThemeService.AppliedImages?.ContainsKey(key) == true) return;
                await Task.Delay(15);
            }
            Assert.Fail($"Timed out waiting for theme image key {key}");
        }

        public async ValueTask DisposeAsync()
        {
            Window.Close();
            await Window.Sessions.DisposeAsync();
            _catalog.Dispose();
            _http.Dispose();
            if (Directory.Exists(_path)) Directory.Delete(_path, true);
        }
    }

    private sealed class SkinHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _map = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
        public int DelayMs { get; set; }

        public void Map(string pathFragment, byte[] png) => _map[pathFragment] = png;
        public int Count(string pathFragment) => _counts.GetValueOrDefault(pathFragment);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (DelayMs > 0) await Task.Delay(DelayMs, cancellationToken);
            var path = request.RequestUri!.AbsolutePath;
            foreach (var (fragment, png) in _map)
            {
                if (!path.Contains(fragment, StringComparison.Ordinal)) continue;
                _counts[fragment] = _counts.GetValueOrDefault(fragment) + 1;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}

/// <summary>Minimal RGBA PNG writer for synthetic theme assets in tests.</summary>
internal static class TestPng
{
    public static byte[] Rgba(int width, int height, byte r = 40, byte g = 80, byte b = 120, byte a = 255)
    {
        using var raw = new MemoryStream();
        var row = new byte[1 + width * 4];
        for (var x = 0; x < width; x++)
        {
            var i = 1 + x * 4;
            row[i] = r; row[i + 1] = g; row[i + 2] = b; row[i + 3] = a;
        }
        for (var y = 0; y < height; y++) raw.Write(row);
        var compressed = Compress(raw.ToArray());
        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        WriteChunk(png, "IHDR"u8, Buf =>
        {
            Span<byte> ihdr = stackalloc byte[13];
            BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
            BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
            ihdr[8] = 8; ihdr[9] = 6; // 8-bit RGBA
            Buf.Write(ihdr);
        });
        WriteChunk(png, "IDAT"u8, Buf => Buf.Write(compressed));
        WriteChunk(png, "IEND"u8, _ => { });
        return png.ToArray();
    }

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(data);
        return output.ToArray();
    }

    private static void WriteChunk(Stream png, ReadOnlySpan<byte> type, Action<Stream> writeData)
    {
        using var data = new MemoryStream();
        writeData(data);
        var payload = data.ToArray();
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, payload.Length);
        png.Write(len);
        png.Write(type);
        png.Write(payload);
        var crc = Crc32(type, payload);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        png.Write(crcBytes);
    }

    private static uint Crc32(ReadOnlySpan<byte> type, byte[] payload)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in type) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (var b in payload) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }

    private static readonly uint[] CrcTable = CreateCrcTable();
    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
