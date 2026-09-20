using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Data.Sqlite;
using Wandur.Core.Discovery;
using Wandur.Core.Settings;
using Wandur.Core.Storage;
using Wandur.Desktop.ViewModels;
using Wandur.Desktop.Views;

namespace Wandur.Desktop.Tests;

/// <summary>The saved worlds list: most used first, reordered when nobody is pointing at it, with the directory's picture beside each world.</summary>
public sealed class SavedWorldsTests
{
    [AvaloniaFact]
    public async Task ConnectionsPutTheMostUsedWorldFirstAndRowsShowArtworkOrInitials()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wandur-saved-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        using var firstServer = new TcpListener(IPAddress.Loopback, 0); firstServer.Start();
        using var secondServer = new TcpListener(IPAddress.Loopback, 0); secondServer.Start();
        var first = new ConnectionProfile { Name = "First World", Host = "127.0.0.1", Port = ((IPEndPoint)firstServer.LocalEndpoint).Port };
        var second = new ConnectionProfile { Name = "Second World", Host = "127.0.0.1", Port = ((IPEndPoint)secondServer.LocalEndpoint).Port };
        var third = new ConnectionProfile { Name = "Third Realm", Host = "third.example.org", Port = 4000 };
        var database = new ClientDatabase(Path.Combine(directory, "wandur.db"));
        var store = new SqliteSettingsStore(database, Path.Combine(directory, "settings.json"));
        store.Save(new ClientSettings { Profiles = [first, second, third] });
        var usage = new SqliteWorldUsageStore(database);
        // The directory lists the first two with generated artwork already in the browser's cache; the third is not listed.
        var listings = new[]
        {
            new WorldListing { Id = "first", Name = "First World", Host = first.Host, Port = first.Port, GeneratedArtworkPath = "games/first/art" },
            new WorldListing { Id = "second", Name = "Second World", Host = second.Host, Port = second.Port, GeneratedArtworkPath = "games/second/art" }
        };
        File.WriteAllText(Path.Combine(directory, "directory.json"), JsonSerializer.Serialize(new { schema_version = 2, format = "wandur.directory", fetched_at = DateTimeOffset.UtcNow, worlds = listings },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
        var art = Path.Combine(directory, "directory-art"); Directory.CreateDirectory(art);
        WritePicture(Path.Combine(art, listings[0].ArtKey + ".png"), 0xFF3C6E9F);
        WritePicture(Path.Combine(art, listings[1].ArtKey + ".png"), 0xFFB7702A);
        using var catalog = new WorldCatalog(Path.Combine(directory, "directory.json"), new Uri("http://offline.invalid/"), OfflineHttp.Client());
        var sessions = new SessionWorkspace(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), store, new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore(), catalog: catalog, usage: usage);
        var panel = new WorldLibraryView(sessions, () => { });
        var model = Assert.IsType<WorldLibraryViewModel>(panel.DataContext);
        var window = new Window { Content = panel, Width = 285, Height = 400 };
        var clients = new List<TcpClient>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        async Task ConnectOnce(ConnectionProfile profile, TcpListener server)
        {
            await sessions.OpenAsync(profile);
            clients.Add(await server.AcceptTcpClientAsync(timeout.Token));
            await WaitFor(() => sessions.Active.Controller.IsConnected, "the session connects");
            Dispatcher.UIThread.RunJobs();
            await sessions.CloseAsync(sessions.Active);
            Dispatcher.UIThread.RunJobs();
        }
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var list = panel.GetVisualDescendants().OfType<ListBox>().Single();
            string[] Names() => model.Profiles.Select(p => p.Name).ToArray();
            Assert.Equal(["First World", "Second World", "Third Realm"], Names());

            // Thumbnails: the two listed worlds get their pictures, the third its initials, all in the same tile.
            WorldThumbnail Tile(int index) => Assert.Single(((ListBoxItem)list.ContainerFromIndex(index)!).GetVisualDescendants().OfType<WorldThumbnail>());
            await WaitFor(() => Tile(0).ShowsArtwork && Tile(1).ShowsArtwork, "listed worlds show their pictures");
            Assert.True(Tile(0).ShowsArtwork && Tile(1).ShowsArtwork, "listed worlds show their pictures");
            Assert.False(Tile(2).ShowsArtwork);
            Assert.Equal("TR", Assert.Single(Tile(2).GetVisualDescendants().OfType<TextBlock>(), t => t.Name == "WorldThumbnailInitials").Text);
            foreach (var index in new[] { 0, 1, 2 }) { Assert.Equal(40, Tile(index).Bounds.Width); Assert.Equal(30, Tile(index).Bounds.Height); }
            var pictured = Assert.Single(Tile(0).GetVisualDescendants().OfType<Image>()).Source as Bitmap;
            Assert.NotNull(pictured);
            Assert.True(pictured.PixelSize.Width <= 80 && pictured.PixelSize.Height <= 60, $"the picture is scaled down once: {pictured.PixelSize}");
            Capture(window, "saved-worlds.png");

            // Two connections to the second world put it first; the count belongs to the world.
            await ConnectOnce(second, secondServer);
            Assert.Equal(["Second World", "First World", "Third Realm"], Names());
            await ConnectOnce(second, secondServer);
            Assert.Equal(2, usage.Load()[second.Id].Connections);
            Assert.False(usage.Load().ContainsKey(first.Id));
            Assert.Equal(["Second World", "First World", "Third Realm"], Names());
            // The manual order in the settings is untouched.
            Assert.Equal([first.Id, second.Id, third.Id], store.Load().Settings.Profiles.Select(p => p.Id));

            // With the pointer over the list, a reorder waits until it leaves.
            var over = list.TranslatePoint(new Point(list.Bounds.Width / 2, 10), window)!.Value;
            window.MouseMove(over); Dispatcher.UIThread.RunJobs();
            Assert.True(list.IsPointerOver, "the pointer is over the list");
            for (var i = 0; i < 3; i++) await ConnectOnce(first, firstServer);
            Assert.Equal(3, usage.Load()[first.Id].Connections);
            Assert.True(model.ReorderPending, "the reorder waits while the pointer is over the list");
            Assert.Equal(["Second World", "First World", "Third Realm"], Names());
            window.MouseMove(new Point(window.Bounds.Width - 1, window.Bounds.Height - 1)); Dispatcher.UIThread.RunJobs();
            Assert.False(list.IsPointerOver);
            Assert.False(model.ReorderPending);
            Assert.Equal(["First World", "Second World", "Third Realm"], Names());
        }
        finally
        {
            window.Close(); await sessions.DisposeAsync();
            foreach (var client in clients) client.Dispose();
            SqliteConnection.ClearAllPools(); Directory.Delete(directory, true);
        }
    }

    private static async Task WaitFor(Func<bool> predicate, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!predicate() && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.True(predicate(), what);
    }

    private static void WritePicture(string path, uint color)
    {
        using var bitmap = new WriteableBitmap(new PixelSize(160, 100), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var pixels = bitmap.Lock())
        {
            var row = new byte[pixels.RowBytes];
            for (var x = 0; x < 160; x++) { row[x * 4] = (byte)color; row[x * 4 + 1] = (byte)(color >> 8); row[x * 4 + 2] = (byte)(color >> 16); row[x * 4 + 3] = 0xFF; }
            for (var y = 0; y < 100; y++) System.Runtime.InteropServices.Marshal.Copy(row, 0, pixels.Address + y * pixels.RowBytes, row.Length);
        }
        bitmap.Save(path, new PngBitmapEncoderOptions());
    }

    private static void Capture(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR") is not { } directory) return;
        Directory.CreateDirectory(directory);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        using var frame = window.CaptureRenderedFrame();
        frame!.Save(Path.Combine(directory, name), new PngBitmapEncoderOptions());
    }
}
