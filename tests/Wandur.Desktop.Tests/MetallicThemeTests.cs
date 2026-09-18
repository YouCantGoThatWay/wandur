using System.Net;
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
        var world = new WorldListing { Id = "lotj", Name = "Legends of the Jedi", Host = "legendsofthejedi.com", Port = 5656, Theme = theme,
            Summary = "A galaxy shaped by its players.", Description = "Explore distant worlds, pilot starships and take a side in an evolving galactic story.", Tags = ["Star Wars", "Roleplay"] };
        File.WriteAllText(Path.Combine(path, "directory.json"), JsonSerializer.Serialize(new { schema_version = 2, format = "wandur.directory", fetched_at = DateTimeOffset.UtcNow, worlds = new[] { world } }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
        var handler = new ImageHandler(narrowImage); using var http = new HttpClient(handler);
        using var catalog = new WorldCatalog(Path.Combine(path, "directory.json"), http: http);
        var window = new MainWindow(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), new SettingsStore(Path.Combine(path, "settings.json")), new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore(), catalog: catalog);
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var toolbar = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "MainToolbar");
            Assert.IsType<DrawingBrush>(toolbar.Background);
            var end = DateTime.UtcNow.AddSeconds(5);
            while (!HasImage(toolbar.Background) && DateTime.UtcNow < end) { await Task.Delay(15); Dispatcher.UIThread.RunJobs(); }
            Assert.True(HasImage(toolbar.Background)); Assert.Equal(1, handler.ImageRequests);
            var dockHeaders = window.GetVisualDescendants().OfType<Grid>().Where(g => g.Name == "PART_Grip").ToArray();
            Assert.NotEmpty(dockHeaders);
            Assert.All(dockHeaders, header => Assert.True(HasImage(header.Background)));
            var texture = ((DrawingGroup)((DrawingBrush)toolbar.Background!).Drawing!).Children
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
            window.Sessions.PreviewWorldTheme(this, null); Dispatcher.UIThread.RunJobs();
            Assert.IsAssignableFrom<ISolidColorBrush>(toolbar.Background);
            window.Sessions.PreviewWorldTheme(this, theme);
            window.Sessions.PreviewWorldTheme(this, null); // Cancel queued texture load before it runs.
            Dispatcher.UIThread.RunJobs(); await Task.Delay(25); Dispatcher.UIThread.RunJobs();
            Assert.IsAssignableFrom<ISolidColorBrush>(toolbar.Background);
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
