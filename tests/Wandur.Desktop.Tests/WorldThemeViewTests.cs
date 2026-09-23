using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Discovery;
using Wandur.Core.Settings;
using Wandur.Core.Storage;
using Wandur.Desktop.ViewModels;
using Wandur.Desktop.Views;

namespace Wandur.Desktop.Tests;

public sealed class WorldThemeViewTests
{
    private static WorldTheme Theme => JsonSerializer.Deserialize<WorldTheme>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "world-theme.json")))!;
    private static string ColorOf(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color.ToString();

    /// <summary>
    /// Shaded chrome is derived from a world's colour rather than equal to it, so what matters is that every
    /// stop stays close to that colour: a world with navy chrome gets navy metal, not the user's grey.
    /// </summary>
    private static void AssertDerivedFrom(IBrush? brush, string hex, int tolerance = 64)
    {
        var target = Color.Parse(hex);
        var stops = brush switch
        {
            ISolidColorBrush solid => new[] { solid.Color },
            GradientBrush gradient => gradient.GradientStops.Select(s => s.Color).ToArray(),
            _ => throw new Xunit.Sdk.XunitException($"unexpected brush {brush?.GetType().Name ?? "null"}"),
        };
        foreach (var c in stops)
            Assert.True(Math.Abs(c.R - target.R) <= tolerance && Math.Abs(c.G - target.G) <= tolerance
                && Math.Abs(c.B - target.B) <= tolerance, $"{c} is not derived from {hex}");
    }

    [AvaloniaFact]
    public async Task AnOpenSessionThemesTheShellDockToolbarsAndDialogsAndClosingItRestoresTheUserDefault()
    {
        var path = Path.Combine(Path.GetTempPath(), "wandur-whole-theme-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var themed = new WorldListing { Id = "lotj", Name = "Legends of the Jedi", Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, Theme = Theme };
        var plain = new WorldListing { Id = "other", Name = "Other world", Host = "other.example", Port = 4000 };
        var store = new SettingsStore(Path.Combine(path, "settings.json"));
        var saved = themed.ToProfile() with { Theme = null };
        store.Save(new ClientSettings { Theme = "Paper", Profiles = [saved, plain.ToProfile()] });
        File.WriteAllText(Path.Combine(path, "directory.json"), JsonSerializer.Serialize(new { schema_version = 2, format = "wandur.directory", fetched_at = DateTimeOffset.UtcNow, worlds = new[] { themed, plain } }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
        using var http = new HttpClient(new NoArtwork(File.ReadAllText(Path.Combine(path, "directory.json")).Replace("\"summary\":\"\"", "\"summary\":\"Updated listing\"")));
        using var catalog = new WorldCatalog(Path.Combine(path, "directory.json"), http: http);
        var window = new MainWindow(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), store, new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore(), catalog: catalog);
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var toolbar = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "MainToolbar");
            var search = window.GetVisualDescendants().OfType<ListBox>().Single(b => b.Name == "DirectoryResults");
            var worlds = window.GetVisualDescendants().OfType<ListBox>().Single(b => b.Name == "WorldProfiles");
            // Highlighting the themed world in the directory or in the saved list leaves the personal theme.
            search.SelectedIndex = 0; Dispatcher.UIThread.RunJobs();
            Assert.Equal(ThemeVariant.Light, window.ActualThemeVariant);
            var savedRow = Assert.IsType<ListBoxItem>(worlds.ContainerFromIndex(0));
            var click = savedRow.TranslatePoint(new Point(20, savedRow.Bounds.Height / 2), window)!.Value;
            window.MouseDown(click, MouseButton.Left); window.MouseUp(click, MouseButton.Left); Dispatcher.UIThread.RunJobs();
            Assert.Equal(ThemeVariant.Light, window.ActualThemeVariant);

            await window.Sessions.OpenAsync(window.Controller.Settings.Profiles[0]);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
            Dispatcher.UIThread.RunJobs();
            var session = window.Sessions.Active;
            Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant);
            Assert.Equal("#ff091821", ColorOf(window.Background));
            // The toolbar sits transparent in the title band, which paints the world's chrome.
            var band = window.GetVisualDescendants().OfType<ThemeWindowSkinHost>().First();
            AssertDerivedFrom(band.BandBrush, "#112532");
            AssertDerivedFrom((IBrush)Application.Current!.Resources["DockSurfaceHeaderBrush"]!, "#091821");
            // Nothing a reader does in the directory, the saved list or a dialog moves it off that session.
            search.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
            Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant);
            worlds.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
            Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant);
            window.Controller.ShowNotice("status update"); Dispatcher.UIThread.RunJobs();
            Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant);
            await catalog.LoadAsync(force: true); Dispatcher.UIThread.RunJobs();
            Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant);
            using var dialogModel = new WorldBrowserViewModel(catalog, window.Sessions);
            var dialog = new BrowserTestWindow(dialogModel, catalog);
            dialog.Show(window); Dispatcher.UIThread.RunJobs();
            dialog.GetVisualDescendants().OfType<ListBox>().Single(b => b.Name == "DirectoryResults").SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs(); Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant);
            dialog.Close(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant);
            Assert.Equal("Paper", store.Load().Settings.Theme);
            var preferences = window.KeyBindings.Single(binding => binding.Gesture?.Key == Key.OemComma);
            preferences.Command!.Execute(null); Dispatcher.UIThread.RunJobs();
            var options = Assert.Single(window.OwnedWindows.OfType<OptionsDialog>());
            var settingsModel = Assert.IsType<PreferencesViewModel>(options.DataContext);
            settingsModel.Background = "#112233";
            window.Controller.ShowNotice("another status update"); Dispatcher.UIThread.RunJobs();
            Assert.Equal("#ff112233", ColorOf((IBrush)Application.Current!.Resources["TerminalBrush"]!));
            options.Close(); Dispatcher.UIThread.RunJobs();
            Assert.Equal("#ff070d15", ColorOf((IBrush)Application.Current!.Resources["TerminalBrush"]!));
            if (Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR") is { } captures)
            {
                Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
                using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
                Directory.CreateDirectory(captures); frame.Save(Path.Combine(captures, "whole-world-theme.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
            // Closing the session hands the window back to the personal theme.
            await window.Sessions.CloseAsync(session); Dispatcher.UIThread.RunJobs();
            Assert.Equal(ThemeVariant.Light, Application.Current!.RequestedThemeVariant);
            Assert.Equal("#ffeeefef", ColorOf(window.Background));
            Assert.Equal(Color.Parse(UserTheme.FromPreset("Paper").Colors["Terminal"]),
                Assert.IsAssignableFrom<ISolidColorBrush>(Application.Current!.Resources["TerminalBrush"]).Color);
        }
        finally { await window.Sessions.DisposeAsync(); window.Close(); }
    }

    private static string Snapshot(params WorldListing[] worlds) => JsonSerializer.Serialize(
        new { schema_version = 2, format = "wandur.directory", fetched_at = DateTimeOffset.UtcNow, worlds },
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

    /// <summary>
    /// The saved world list, the toolbar picker and the directory's Connect all open through
    /// <see cref="SessionWorkspace.OpenAsync"/>, over the real client database. A theme reaches the
    /// session whether the directory snapshot carries it or only the saved profile still does: a
    /// listing without a theme says nothing about that world's appearance, so it never evicts one.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OpeningASavedWorldFromTheListThemesTheShellWhicheverSideCarriesTheTheme(bool fromDirectory)
    {
        var path = Path.Combine(Path.GetTempPath(), "wandur-open-theme-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var listing = new WorldListing { Id = "lotj", Name = "Legends of the Jedi", Host = "127.0.0.1",
            Port = ((IPEndPoint)listener.LocalEndpoint).Port, Theme = fromDirectory ? Theme : null };
        var database = new ClientDatabase(Path.Combine(path, "wandur.db"));
        var store = new SqliteSettingsStore(database, Path.Combine(path, "settings.json"));
        store.Save(new ClientSettings { Theme = "Paper", Profiles = [listing.ToProfile() with { Theme = fromDirectory ? null : Theme }] });
        var cache = new SqliteWorldCatalogCache(database, Path.Combine(path, "directory.json"));
        cache.WriteSnapshot(Snapshot(listing));
        using var catalog = new WorldCatalog(cache, http: OfflineHttp.Client());
        var window = new MainWindow(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), store, new MemoryPasswordVault(),
            new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore(), catalog: catalog);
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var library = Assert.IsType<WorldLibraryViewModel>(window.GetVisualDescendants().OfType<WorldLibraryView>().First().DataContext);
            library.SelectedProfile = library.Profiles[0];
            await library.ConnectCommand.ExecuteAsync(null);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(Theme, window.Sessions.Active.Controller.WorldTheme);
            Assert.Equal(Theme, ThemeService.AppliedWorldTheme);
            Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant);
            Assert.Equal("#ff091821", ColorOf(window.Background));
            Assert.Equal("#ff091821", ColorOf((IBrush)Application.Current!.Resources["ShellBrush"]!));
            // The theme the world already had survives in storage, and saving the listing again keeps it.
            Assert.Equal(Theme, Assert.Single(store.Load().Settings.Profiles).Theme);
            using var browser = new WorldBrowserViewModel(catalog, window.Sessions);
            browser.Attach(action => action());
            Assert.Equal(Theme, browser.SaveSelectedWorld()!.Theme);
            Assert.Equal(Theme, Assert.Single(store.Load().Settings.Profiles).Theme);
        }
        finally { await window.Sessions.DisposeAsync(); window.Close(); }
    }

    /// <summary>A theme that only reaches the client on a later refresh still finds the open session.</summary>
    [AvaloniaFact]
    public async Task AThemeArrivingWithALaterDirectoryRefreshReachesTheOpenSession()
    {
        var path = Path.Combine(Path.GetTempPath(), "wandur-late-theme-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var themed = new WorldListing { Id = "lotj", Name = "Legends of the Jedi", Host = "127.0.0.1",
            Port = ((IPEndPoint)listener.LocalEndpoint).Port, Theme = Theme };
        var database = new ClientDatabase(Path.Combine(path, "wandur.db"));
        var store = new SqliteSettingsStore(database, Path.Combine(path, "settings.json"));
        store.Save(new ClientSettings { Theme = "Paper", Profiles = [themed.ToProfile() with { Theme = null }] });
        var cache = new SqliteWorldCatalogCache(database, Path.Combine(path, "directory.json"));
        cache.WriteSnapshot(Snapshot(themed with { Theme = null }));
        var published = false;
        using var http = new HttpClient(new NoArtwork(() => Snapshot(published ? themed : themed with { Theme = null })));
        using var catalog = new WorldCatalog(cache, http: http);
        var window = new MainWindow(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), store, new MemoryPasswordVault(),
            new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore(), catalog: catalog);
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            await window.Sessions.OpenAsync(window.Controller.Settings.Profiles[0]);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
            Dispatcher.UIThread.RunJobs();
            Assert.Null(ThemeService.AppliedWorldTheme);
            // The world gains a theme only once the session is already open.
            published = true;
            await catalog.LoadAsync(force: true); Dispatcher.UIThread.RunJobs();
            Assert.Equal(Theme, ThemeService.AppliedWorldTheme);
            Assert.Equal("#ff091821", ColorOf(window.Background));
        }
        finally { await window.Sessions.DisposeAsync(); window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingWorldAdoptsCatalogThemeAndToolsFollowActiveSession(bool readOnly)
    {
        var path = Path.Combine(Path.GetTempPath(), "wandur-theme-session-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var world = new WorldListing { Id = "test", Name = "Test world", Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, Theme = Theme };
        var profile = world.ToProfile() with { Theme = null };
        var store = new SettingsStore(Path.Combine(path, "settings.json"));
        store.Save(new ClientSettings { Theme = "Paper", Profiles = [profile] });
        File.WriteAllText(Path.Combine(path, "directory.json"), JsonSerializer.Serialize(new { schema_version = 2, format = "wandur.directory", fetched_at = DateTimeOffset.UtcNow, worlds = new[] { world } }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
        using var catalog = new WorldCatalog(Path.Combine(path, "directory.json"), http: OfflineHttp.Client());
        await using var sessions = new SessionWorkspace(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), readOnly ? new ReadOnlyStore(store) : store, new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore(), catalog: catalog);
        var tools = new ActiveSessionView(sessions, _ => new TextBlock { Text = "Navigation" });
        var window = new Window { Content = tools };
        try
        {
            window.Show(); await sessions.OpenAsync(profile);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
            Dispatcher.UIThread.RunJobs();
            var first = sessions.Active;
            Assert.Equal(Theme, first.Controller.WorldTheme);
            Assert.Equal(readOnly ? null : Theme, Assert.Single(store.Load().Settings.Profiles).Theme);
            Assert.Equal(ThemeVariant.Dark, tools.ActualThemeVariant);
            if (readOnly)
            {
                Assert.True(first.Controller.IsConnected);
                Assert.Equal(Wandur.Core.Localization.Strings.WorldThemeCouldNotBeSaved, first.Controller.Notice);
                using var browser = new WorldBrowserViewModel(catalog, sessions);
                browser.Attach(action => action());
                Assert.Equal(Theme, browser.SaveSelectedWorld()!.Theme);
                Assert.Equal(Wandur.Core.Localization.Strings.WorldThemeCouldNotBeSaved, browser.Feedback);
                return;
            }
            var terminal = new TerminalView(first.Controller);
            var terminalWindow = new Window { Content = terminal };
            try
            {
                terminalWindow.Show(); Dispatcher.UIThread.RunJobs();
                var terminalScope = terminal;
                Assert.Equal(ThemeVariant.Dark, terminalScope.ActualThemeVariant);
                await sessions.OpenAsync(); Dispatcher.UIThread.RunJobs();
                Assert.Null(sessions.Active.Controller.WorldTheme);
                Assert.Equal(ThemeVariant.Light, tools.ActualThemeVariant);
                Assert.Equal(ThemeVariant.Light, terminalScope.ActualThemeVariant);
                sessions.Select(first); Dispatcher.UIThread.RunJobs();
                Assert.Equal(ThemeVariant.Dark, tools.ActualThemeVariant);
                first.Controller.SaveSettings(first.Controller.Settings with { Foreground = "#AABBCC", Background = "#112233" });
                Assert.Equal("#ff112233", ColorOf((IBrush)Application.Current!.Resources["TerminalBrush"]!));
                using var editor = new ProfileEditorViewModel(first.Controller, catalog, first.Controller.Settings.Profiles[0]);
                editor.WorldName = "Renamed world";
                await editor.SaveCommand.ExecuteAsync(null);
                Assert.Empty(editor.Error);
                var renamed = Assert.Single(store.Load().Settings.Profiles);
                Assert.Equal(Theme, renamed.Theme);
                using var changed = new ProfileEditorViewModel(first.Controller, catalog, renamed);
                changed.Port = profile.Port + 1;
                await changed.SaveCommand.ExecuteAsync(null);
                Assert.Empty(changed.Error);
                Assert.Null(Assert.Single(store.Load().Settings.Profiles).Theme);
            }
            finally { terminalWindow.Close(); }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task DirectoryOrdersArtTagsDescriptionAndUpdatesSavedThemeWithoutLosingLogin()
    {
        var path = Path.Combine(Path.GetTempPath(), "wandur-theme-ui-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        var world = new WorldListing { Id = "lotj", Name = "Legends of the Jedi", Host = "legendsofthejedi.com", Port = 5656,
            Theme = Theme, Tags = ["Star Wars", "Roleplay"], Features = new() { Kind = "MUD", Language = "English" },
            Population = new() { LatestCount = 42, ReportedRange = "25–50" },
            Summary = "A galaxy shaped by its players.", Description = "Explore distant worlds, pilot starships and take a side in an evolving galactic story.\n\nYour character’s choices become part of the world.",
            GeneratedArtworkPath = "/art/test.png" };
        var plain = world with { Id = "other", Name = "Other world", Host = "other.example", Theme = null };
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        File.WriteAllText(Path.Combine(path, "directory.json"), JsonSerializer.Serialize(new { schema_version = 2, format = "wandur.directory", fetched_at = DateTimeOffset.UtcNow, worlds = new[] { world, plain } }, options));
        using var http = new HttpClient(new NoArtwork());
        using var catalog = new WorldCatalog(Path.Combine(path, "directory.json"), http: http);
        // This proves browsing never re-themes the window, so it pins a dark personal preset rather than
        // relying on whatever the client's default preset happens to be.
        var browsingStore = new SettingsStore(Path.Combine(path, "settings.json"));
        browsingStore.Save(new ClientSettings { Theme = "Ember" });
        await using var sessions = new SessionWorkspace(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), browsingStore, new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        using var model = new WorldBrowserViewModel(catalog, sessions);
        var browser = new WorldBrowserView(model, catalog);
        var window = new Window { Content = browser, Width = 1050, Height = 780 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            // Browsing never themes the window: the personal default stays until a session opens.
            Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant);
            Assert.Equal("#ff141519", ColorOf(window.Background));
            var title = browser.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "DirectoryWorldTitle");
            var children = Assert.IsType<StackPanel>(title.Parent).Children;
            Assert.Equal("DirectoryWorldTitle", children[0].Name);
            Assert.Equal("DirectoryArtworkFrame", children[1].Name);
            Assert.Equal("DirectoryWorldTags", children[2].Name);
            Assert.Equal("DirectoryWorldDescription", children[4].Name);
            var saved = world.ToProfile() with { Theme = null, Username = "pilot", PasswordId = Guid.NewGuid(), AutoLogin = true };
            sessions.Active.Controller.SaveSettings(sessions.Active.Controller.Settings with { Profiles = [saved] });
            var updated = model.SaveSelectedWorld();
            Assert.Equal(saved with { Theme = Theme }, updated);
            Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
            if (Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR") is { } captures)
            { Directory.CreateDirectory(captures); frame.Save(Path.Combine(captures, "world-theme-directory.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions()); }
            var list = browser.GetVisualDescendants().OfType<ListBox>().Single(t => t.Name == "DirectoryResults");
            list.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
            Assert.Equal("#ff141519", ColorOf(window.Background));
            list.SelectedIndex = 0; Dispatcher.UIThread.RunJobs();
            Assert.Equal("#ff141519", ColorOf(window.Background));
        }
        finally { window.Close(); }
    }
    private sealed class NoArtwork : HttpMessageHandler
    {
        private readonly Func<string>? _snapshot;
        public NoArtwork() { }
        public NoArtwork(string snapshot) => _snapshot = () => snapshot;
        public NoArtwork(Func<string> snapshot) => _snapshot = snapshot;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_snapshot is not null && request.RequestUri!.AbsolutePath == "/directory"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_snapshot()) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
    private sealed class ReadOnlyStore(ISettingsStore inner) : ISettingsStore
    {
        public string FilePath => inner.FilePath;
        public SettingsLoadResult Load() => inner.Load();
        public void Save(ClientSettings settings) => throw new IOException("Read-only test storage");
    }
}
