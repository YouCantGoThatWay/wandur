using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Discovery;
using Wandur.Core.Settings;

namespace Wandur.Desktop.Tests;

public sealed class MetallicThemeTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetallicThemeLoadsTextureAndKeepsTheTranscriptSolid(bool narrowImage)
    {
        var path = Path.Combine(Path.GetTempPath(), "wandur-metal-" + Guid.NewGuid()); Directory.CreateDirectory(path);
        var theme = JsonSerializer.Deserialize<WorldTheme>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "world-theme-metallic.json")))!;
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var world = new WorldListing { Id = "lotj", Name = "Legends of the Jedi", Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, Theme = theme,
            Summary = "A galaxy shaped by its players.", Description = "Explore distant worlds, pilot starships and take a side in an evolving galactic story.", Tags = ["Star Wars", "Roleplay"] };
        File.WriteAllText(Path.Combine(path, "directory.json"), JsonSerializer.Serialize(new { schema_version = 2, format = "wandur.directory", fetched_at = DateTimeOffset.UtcNow, worlds = new[] { world } }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
        var handler = new ImageHandler(narrowImage); using var http = new HttpClient(handler);
        using var catalog = new WorldCatalog(Path.Combine(path, "directory.json"), http: http);
        var window = new MainWindow(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), new SettingsStore(Path.Combine(path, "settings.json")), new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore(), catalog: catalog);
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var toolbar = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "MainToolbar");
            // The world theme arrives with its session, not with a highlighted directory row.
            Assert.False(HasImage(toolbar.Background));
            await window.Sessions.OpenAsync(world.ToProfile()); Dispatcher.UIThread.RunJobs();
            // The toolbar sits transparent inside the title band, so the chrome's texture is painted by the
            // band, not by the toolbar. That is where a textured theme's material has to survive.
            var band = window.GetVisualDescendants().OfType<ThemeWindowSkinHost>().First();
            Assert.IsType<DrawingBrush>(band.BandBrush);
            var end = DateTime.UtcNow.AddSeconds(5);
            while (!HasImage(band.BandBrush) && DateTime.UtcNow < end) { await Task.Delay(15); Dispatcher.UIThread.RunJobs(); }
            Assert.True(HasImage(band.BandBrush)); Assert.Equal(1, handler.ImageRequests);
            var dockHeaders = window.GetVisualDescendants().OfType<Grid>().Where(g => g.Name == "PART_Grip").ToArray();
            Assert.NotEmpty(dockHeaders);
            Assert.All(dockHeaders, header => Assert.True(HasImage(header.Background)));
            var texture = ((DrawingGroup)((DrawingBrush)band.BandBrush!).Drawing!).Children
                .OfType<GeometryDrawing>().Select(d => d.Brush).OfType<ImageBrush>().Single();
            Assert.Equal(narrowImage ? 8 : 1024, Assert.IsType<Bitmap>(texture.Source).PixelSize.Width);
            Assert.IsAssignableFrom<ISolidColorBrush>(Application.Current!.Resources["TerminalBrush"]);
            Assert.Equal(Color.Parse("#0D0F12"), ((ISolidColorBrush)Application.Current.Resources["TerminalBrush"]!).Color);
            Assert.Equal(Color.Parse("#25282B"), Assert.IsAssignableFrom<ISolidColorBrush>(Application.Current.Resources["PanelBrush"]).Color);
            Assert.Equal(Color.Parse("#18191B"), Assert.IsAssignableFrom<ISolidColorBrush>(Application.Current.Resources["ShellBrush"]).Color);
            Assert.IsType<LinearGradientBrush>(Application.Current.Resources["PrimaryFaceBrush"]);
            Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
            if (!narrowImage && Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR") is { } captures)
            { Directory.CreateDirectory(captures); frame.Save(Path.Combine(captures, "metallic-workspace.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions()); }
            var themed = window.Sessions.Active;
            await window.Sessions.CloseAsync(themed); Dispatcher.UIThread.RunJobs();
            Assert.False(HasImage(toolbar.Background));   // the texture is gone; the toolbar's own shading is not a texture
            // Reopening and closing at once has to cancel the queued texture load before it runs.
            await window.Sessions.OpenAsync(world.ToProfile());
            await window.Sessions.CloseAsync(window.Sessions.Active);
            Dispatcher.UIThread.RunJobs(); await Task.Delay(25); Dispatcher.UIThread.RunJobs();
            Assert.False(HasImage(toolbar.Background));   // the texture is gone; the toolbar's own shading is not a texture
        }
        finally { window.Close(); await window.Sessions.DisposeAsync(); }
    }
    private static bool HasImage(IBrush? brush) => brush is DrawingBrush { Drawing: DrawingGroup group } && group.Children.OfType<GeometryDrawing>().Any(d => d.Brush is ImageBrush);
    private sealed class ImageHandler(bool narrowImage) : HttpMessageHandler
    {
        public int ImageRequests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.Contains("theme-assets"))
            {
                ImageRequests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(narrowImage
                    ? Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAgAAAAECAYAAACzzX7wAAAAEklEQVR4nGPY0mTzHx9moL0CABxATiGmNzirAAAAAElFTkSuQmCC")
                    : File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "brushed-gunmetal-v1.png"))) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
