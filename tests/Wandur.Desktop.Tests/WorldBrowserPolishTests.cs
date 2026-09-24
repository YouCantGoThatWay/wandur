using System.Net;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Discovery;
using Wandur.Core.Settings;
using Wandur.Desktop.Terminal;
using Wandur.Desktop.Views;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.Tests;

public sealed class WorldBrowserPolishTests
{
    [AvaloniaTheory]
    [InlineData("Hull", 1040, 680)]
    [InlineData("Hull", 1536, 1024)]
    [InlineData("Slate", 1040, 680)]
    [InlineData("Slate", 1536, 1024)]
    public async Task EmbeddedDirectoryHasReadableHierarchyAndCompactSelectableCards(string theme, int width, int height)
    {
        await using var fixture = new Fixture(theme, width, height);
        var window = fixture.Window;
        try
        {
            window.Show(); Layout(window);
            var initialList = Find<ListBox>(window, "DirectoryResults");
            initialList.SelectedItem = initialList.Items.OfType<WorldListing>().Single(w => w.Id == "lantern");
            Layout(window);
            var browser = Find<WorldBrowserView>(window);
            var heading = browser.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == L.FindAMUD);
            Assert.True(heading.FontSize >= 20, "The directory needs a page heading above control and result text.");
            var search = Find<TextBox>(browser, "DirectorySearch");
            var list = Find<ListBox>(browser, "DirectoryResults");
            Capture(window, $"directory-polish-{theme.ToLowerInvariant()}-{width}x{height}.png");
            Assert.True(search.Bounds.Width > browser.Bounds.Width * .8, "Search should use the available directory width.");
            Assert.InRange(Find<Border>(browser, "DirectoryTitleBar").Bounds.Height, 30, 46);
            Assert.True(list.Bounds.Height > browser.Bounds.Height * .45, $"The directory header must leave room to browse: {list.Bounds.Height} of {browser.Bounds.Height}.");
            var rows = list.GetVisualDescendants().OfType<ListBoxItem>().ToArray();
            Assert.True(rows.Length >= 3);
            foreach (var row in rows)
            {
                var world = Assert.IsType<WorldListing>(row.DataContext);
                var name = row.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == world.Name);
                Assert.True(name.FontSize >= 14, "World names should be readable at the app's normal text size.");
                Assert.InRange(row.Bounds.Height, 60, 112);
                Assert.InRange(row.Bounds.Width, 160, list.Bounds.Width);
                var tile = Find<Border>(row, "DirectoryResultIdentity");
                Assert.InRange(tile.Bounds.Width, 28, 40);
            }
            var selected = rows.Single(r => r.IsSelected);
            var card = Find<Border>(selected, "DirectoryResultCard");
            Assert.Equal(Application.Current!.Resources["AccentBrush"], card.BorderBrush);
            var other = rows.First(r => !r.IsSelected);
            Assert.NotEqual(card.Background, Find<Border>(other, "DirectoryResultCard").Background);
            Capture(window, $"directory-polish-{theme.ToLowerInvariant()}-{width}x{height}.png");

            // Selection must update the existing detail and command targets, including browser-only worlds.
            list.SelectedItem = list.Items.OfType<WorldListing>().Single(w => w.WebOnly);
            Layout(window);
            Assert.Equal("Web Garden", Find<TextBlock>(browser, "DirectoryWorldTitle").Text);
            Assert.False(Find<Button>(browser, "ConnectDirectoryWorld").IsEnabled);
            Assert.False(Find<Button>(browser, "AddDirectoryWorld").IsEnabled);
            Assert.Contains(browser.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, L.PlayInBrowser));
            search.Text = "Lantern"; Layout(window);
            Assert.Single(list.Items);
            var connect = Find<Button>(browser, "ConnectDirectoryWorld");
            var save = Find<Button>(browser, "AddDirectoryWorld");
            Assert.True(connect.IsEnabled && save.IsEnabled);
            foreach (var button in new[] { connect, save })
            {
                var end = button.TranslatePoint(new Point(button.Bounds.Width, button.Bounds.Height), browser)!.Value;
                Assert.InRange(end.X, 0, browser.Bounds.Width);
                Assert.InRange(end.Y, 0, browser.Bounds.Height);
            }
            save.Command!.Execute(null);
            Assert.Single(window.Controller.Settings.Profiles);
            search.Text = "no matching world zzq"; Layout(window);
            Assert.Empty(list.Items);
            Assert.False(Find<Border>(browser, "DirectoryListingToolbar").IsVisible);
            Find<Button>(browser, "DirectoryResetFilters").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout(window);
            Assert.Equal(8, list.Items.Count);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task SelectedArtworkStaysUncroppedAndDoesNotFetchImagesForResultCards()
    {
        await using var fixture = new Fixture("Hull", 1040, 680, artwork: true);
        try
        {
            fixture.Window.Show(); Layout(fixture.Window);
            for (var attempt = 0; attempt < 100 && Find<Image>(fixture.Window, "DirectoryArtwork").Source is null; attempt++)
            { await Task.Delay(10); Layout(fixture.Window); }
            Layout(fixture.Window);
            var image = Find<Image>(fixture.Window, "DirectoryArtwork");
            Assert.NotNull(image.Source);
            Assert.Equal(Stretch.Uniform, image.Stretch);
            Assert.InRange(image.Bounds.Height, 1, 160);
            Assert.Single(fixture.Handler.ArtRequests);
            Assert.EndsWith("/orbit.png", fixture.Handler.ArtRequests[0]);
            var title = Find<TextBlock>(fixture.Window, "DirectoryWorldTitle");
            Assert.True(title.Bounds.Height > 0);
        }
        finally { fixture.Window.Close(); }
    }

    private static T Find<T>(Visual root, string? name = null) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => name is null || c.Name == name);

    private static void Layout(Window window)
    { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2); }

    private static void Capture(Window window, string name)
    {
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        frame.Save(Path.Combine(directory, name), new PngBitmapEncoderOptions());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "wandur-directory-polish-" + Guid.NewGuid());
        private readonly HttpClient _http;
        private readonly WorldCatalog _catalog;
        public FixtureHandler Handler { get; }
        public MainWindow Window { get; }

        public Fixture(string theme, int width, int height, bool artwork = false)
        {
            Directory.CreateDirectory(_directory);
            var world = new WorldListing
            {
                Id = "lantern", Name = "The Lantern & the Rain", Summary = "A quiet inn. A winding road. Somewhere to begin.",
                Description = "Follow the lanterns through a rain-soaked forest, trade stories at the inn, and discover the old paths beyond the village.\n\nFeatures:\n* Explore the old roads\n* Meet fellow travellers\n* Build a home in the valley",
                Host = "lantern.example.org", Port = 4000, TlsPort = 4001,
                Availability = new() { Online = true }, Population = new() { LatestCount = 42 },
                Features = new() { Theme = "Fantasy", Kind = "MUD", Language = "English" },
                Source = new() { Name = "Test directory" }, Tags = ["Exploration", "Roleplay"],
                BannerUrl = artwork ? "https://art.example.org/lantern.png" : ""
            };
            var worlds = new[] { world, world with { Id = "orbital", Name = "Orbital Station", Host = "orbit.example.org", Summary = "Find a home among the stars.", Description = "Explore the station and its distant outposts.", Features = new() { Theme = "Science fiction", Kind = "MUSH" }, BannerUrl = artwork ? "https://art.example.org/orbit.png" : "" },
                new WorldListing { Id = "web", Name = "Web Garden", WebOnly = true, PlayUrl = "https://garden.example.org", Features = new() { Theme = "Social" } } }
                .Concat(Enumerable.Range(1, 5).Select(i => world with { Id = "valley" + i, Name = "Valley " + i, Host = $"valley{i}.example.org", Summary = "Explore a distant valley.", Description = "Build a home beside the river.", BannerUrl = "" })).ToArray();
            var snapshot = JsonSerializer.Serialize(new { format = "wandur.directory", schema_version = 2, fetched_at = DateTimeOffset.UtcNow, worlds },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
            var cache = Path.Combine(_directory, "directory.json");
            File.WriteAllText(cache, snapshot);
            Handler = new FixtureHandler(snapshot); _http = new HttpClient(Handler);
            _catalog = new WorldCatalog(cache, new Uri("https://directory.example.org/"), _http);
            var store = new SettingsStore(Path.Combine(_directory, "settings.json"));
            store.Save(new ClientSettings { Theme = theme, UseWorldThemes = false });
            Window = new MainWindow(new TranscriptDisplayFactory(), store, new MemoryPasswordVault(),
                new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore(), catalog: _catalog)
                { Width = width, Height = height };
        }

        public async ValueTask DisposeAsync()
        {
            await Window.Sessions.DisposeAsync(); _catalog.Dispose(); _http.Dispose();
            Directory.Delete(_directory, true);
        }
    }

    private sealed class FixtureHandler(string snapshot) : HttpMessageHandler
    {
        public List<string> ArtRequests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/directory")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(snapshot) });
            if (request.RequestUri.Host == "art.example.org")
            {
                ArtRequests.Add(request.RequestUri.AbsoluteUri);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(TestPng.Rgba(480, 180)) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
