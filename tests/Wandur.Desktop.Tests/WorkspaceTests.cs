using System.Net;
using System.Net.Sockets;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Settings;
using Wandur.Core.Discovery;
using Wandur.Desktop;
using Wandur.Desktop.Views;
using Wandur.Desktop.ViewModels;

[assembly: AvaloniaTestApplication(typeof(Wandur.Desktop.Tests.TestApplication))]

namespace Wandur.Desktop.Tests;

public static class TestApplication
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().WithInterFont().UseSkia().UseHarfBuzz().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class WorkspaceTests
{
    private static MainWindow CreateWindow()
    {
        var window = new MainWindow(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), new SettingsStore(Path.Combine(Path.GetTempPath(), "wandur-ui-" + Guid.NewGuid(), "settings.json")), new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    [AvaloniaFact]
    public async Task EditingSelectedWorldPreservesItsIdentityAndOtherProfiles()
    {
        var window = CreateWindow();
        try
        {
            var edit = Find<Button>(window, "EditSavedWorld");
            Assert.False(edit.IsEffectivelyEnabled);
            var first = new ConnectionProfile { Name = "First", Host = "first.example.org" };
            var second = new ConnectionProfile { Name = "Second", Host = "second.example.org" };
            window.Controller.SaveSettings(window.Controller.Settings with { Profiles = [first, second] });
            var list = Find<ListBox>(window, "WorldProfiles");
            list.SelectedItem = second;
            Dispatcher.UIThread.RunJobs();
            Assert.True(edit.IsEffectivelyEnabled);
            edit.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var dialog = Assert.Single(window.OwnedWindows.OfType<ProfileDialog>());
            Assert.Equal("Second", Find<TextBox>(dialog, "WorldName").Text);
            Find<TextBox>(dialog, "WorldName").Text = "Renamed";
            Find<Button>(dialog, "SaveWorld").Command!.Execute(null);
            await WaitFor(() => !dialog.IsVisible);
            Assert.Equal(2, window.Controller.Settings.Profiles.Count);
            Assert.Equal(first, window.Controller.Settings.Profiles[0]);
            Assert.Equal("Renamed", window.Controller.Settings.Profiles.Single(p => p.Id == second.Id).Name);
            list.SelectedIndex = -1;
            Assert.False(edit.IsEffectivelyEnabled);
        }
        finally { await window.Sessions.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task LegacyMudBrightGrayIsRenderedWithTheBrightForeground()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wandur-ansi-" + Guid.NewGuid());
        await using var controller = new WorkspaceController(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), new SettingsStore(Path.Combine(directory, "settings.json")), new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        controller.Terminal.Append("\u001b[1;30mLegacy bright gray is readable on the dark terminal.\r\n" +
            "This uses the same 1;30 ANSI sequence sent by Legends of the Jedi.\r\n\r\n" +
            "\u001b[1;32mBright green\u001b[0m · default text\r\n" +
            "\u001b[1;36mBright cyan\u001b[0m · default text\r\n" +
            "\u001b[30;47mBlack text on a light background\u001b[0m\r\n");
        var window = new Window { Width = 900, Height = 550, Content = new TerminalView(controller) };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var terminal = Find<Iciclecreek.Terminal.TerminalView>(window, "Transcript").Terminal;
            var palette = terminal.Colors.Take();
            var gray = terminal.Buffer.GetLine(0)![0];
            Assert.Equal(Avalonia.Media.Color.Parse("#808080"), Iciclecreek.Avalonia.Terminal.BufferCellExtensions.GetForegroundColor(gray, palette));
            var background = Enumerable.Range(0, terminal.Buffer.Lines.Length).Select(i => terminal.Buffer.GetLine(i)!).Single(l => l.TranslateToString(true).StartsWith("Black text"))[0];
            Assert.Equal(Avalonia.Media.Color.Parse("#D4D4D4"), Iciclecreek.Avalonia.Terminal.BufferCellExtensions.GetBackgroundColor(background, palette));
            Capture(window, "ansi-bright-colors.png");
        }
        finally { window.Close(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static void Capture(Window window, string name)
    {
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var path = Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR");
        if (path is not null) { Directory.CreateDirectory(path); frame.Save(Path.Combine(path, name), new Avalonia.Media.Imaging.PngBitmapEncoderOptions()); }
    }

    private static System.Windows.Input.ICommand MenuCommand(MainWindow window, string menu, string item)
    {
        var group = NativeMenu.GetMenu(window)!.Items.OfType<NativeMenuItem>().Single(i => i.Header == menu);
        return group.Menu!.Items.OfType<NativeMenuItem>().Single(i => i.Header == item).Command!;
    }

    [AvaloniaFact]
    public async Task DirectoryBrowserSearchesAndAddsWithoutDuplicatesOrOpeningASession()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wandur-browser-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var snapshot = System.Text.Json.JsonSerializer.Serialize(new
        {
            schema_version = 1, fetched_at = DateTimeOffset.UtcNow, games = new object[]
            {
                new { id = 1, name = "The Lantern & the Rain", intro = "A quiet inn. A winding road. Somewhere to begin.", description = "Follow the lanterns through a rain-soaked forest, trade stories at the inn, and discover the old paths beyond the village. Every journey begins with a single command.\n\nThis is sample data for the directory UI test.", connection = new { host = "lantern.example.org", port = 4000, tls_port = 4001 }, tags = new { custom = new[] { new { name = "Fantasy" }, new { name = "Roleplay" } } } },
                new { id = 2, name = "Orbital Station", intro = "Find a home among the stars.", connection = new { host = "orbit.example.org", port = 5000 } },
                new { id = 3, name = "Web Garden", status = new { web_only = true } }
            }
        });
        var path = Path.Combine(directory, "directory.json");
        var data = System.Text.Json.Nodes.JsonNode.Parse(snapshot)!;
        data["games"]![0]!["status"] = System.Text.Json.Nodes.JsonNode.Parse("""{"confirmed_online":true,"latest_players":0}""");
        data["games"]![0]!["tags"]!["categories"] = System.Text.Json.Nodes.JsonNode.Parse("""{"play_count":{"name":"75-100"},"roleplaying":{"name":"Suggested"}}""");
        File.WriteAllText(path, data.ToJsonString());
        using var http = new HttpClient(new NoArtworkHandler());
        using var catalog = new WorldCatalog(path, http: http);
        await using var sessions = new SessionWorkspace(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), new SettingsStore(Path.Combine(directory, "settings.json")), new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        var dialog = new BrowserTestWindow(catalog, sessions);
        try
        {
            dialog.Show(); Dispatcher.UIThread.RunJobs();
            Find<TextBox>(dialog, "DirectorySearch").Text = "lantren";
            Dispatcher.UIThread.RunJobs();
            var results = Find<ListBox>(dialog, "DirectoryResults");
            Assert.Single(results.Items);
            var facts = dialog.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToArray();
            Assert.Contains("Listed player range", facts);
            Assert.Contains("75-100", facts);
            Assert.Contains("Last observed players", facts);
            Assert.Contains("0", facts);
            Assert.Contains("Suggested", facts);
            Assert.DoesNotContain("Average players", facts);
            var add = Find<Button>(dialog, "AddDirectoryWorld");
            add.Command!.Execute(null);
            add.Command!.Execute(null);
            Assert.Single(sessions.Active.Controller.Settings.Profiles);
            Assert.False(sessions.Active.Controller.HasSession);
            Find<TextBox>(dialog, "DirectorySearch").Text = "";
            Dispatcher.UIThread.RunJobs();
            Capture(dialog, "directory-browser.png");
            foreach (var width in new[] { 1380, 1040 })
            {
                dialog.Width = width; Dispatcher.UIThread.RunJobs();
                Capture(dialog, $"directory-dialog-{width}.png");
            }
            Find<TextBox>(dialog, "DirectorySearch").Text = "web";
            Dispatcher.UIThread.RunJobs();
            Assert.False(Find<Button>(dialog, "AddDirectoryWorld").IsEnabled);
            var embedded = new MainWindow(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), new SettingsStore(Path.Combine(directory, "embedded-settings.json")), new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore(), catalog: catalog);
            try
            {
                embedded.Show(); Dispatcher.UIThread.RunJobs();
                var embeddedResults = Find<ListBox>(embedded, "DirectoryResults");
                embeddedResults.SelectedItem = embeddedResults.Items.OfType<WorldListing>().Single(w => w.Name == "The Lantern & the Rain");
                Dispatcher.UIThread.RunJobs();
                foreach (var width in new[] { 1380, 1040 })
                {
                    embedded.Width = width;
                    Dispatcher.UIThread.RunJobs();
                    Capture(embedded, $"directory-embedded-{width}.png");
                    var browser = Assert.Single(embedded.GetVisualDescendants().OfType<WorldBrowserView>());
                    var addButton = Find<Button>(embedded, "AddDirectoryWorld");
                    var connectButton = Find<Button>(embedded, "ConnectDirectoryWorld");
                    Assert.True(browser.Bounds.Width > 0);
                    var connectEnd = connectButton.TranslatePoint(new Point(connectButton.Bounds.Width, 0), browser);
                    Assert.NotNull(connectEnd);
                    Assert.InRange(connectEnd.Value.X, 0, browser.Bounds.Width);
                    Assert.True(addButton.IsEffectivelyVisible);
                }
            }
            finally { await embedded.Sessions.DisposeAsync(); embedded.Close(); }
        }
        finally { dialog.Close(); Directory.Delete(directory, true); }
    }

    private sealed class NoArtworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    [AvaloniaFact]
    public async Task AdvancedSearchCombinesPreferencesPopulationAndRatingsAndResets()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wandur-advanced-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "directory.json");
        var json = """
        {"schema_version":1,"fetched_at":"DATE","games":[
          {"id":1,"name":"Amber Forest","description":"Explore the world.\n\nFeatures:\n* Crafting\n* Exploration",
           "connection":{"host":"amber.example.org","port":4000,"tls_port":4001},
           "status":{"latest_players":12,"confirmed_online":true},"reviews":{"average_rating":4.5,"rating_count":10,"count":3},
           "dates":{"created":"1997-01-01T00:00:00Z"},
           "tags":{"categories":{"theme":{"name":"Fantasy"},"language":{"name":"English"},"player_killing":{"name":"Not Allowed"}}}},
          {"id":2,"name":"Blue Forest","connection":{"host":"blue.example.org","port":4000},
           "status":{"latest_players":0},"tags":{"categories":{"theme":{"name":"Fantasy"},"language":{"name":"English"},"player_killing":{"name":"Allowed"}}}},
          {"id":3,"name":"Cloud Station","tags":{"categories":{"theme":{"name":"Sci-Fi"},"language":{"name":"English"}}}}
        ]}
        """.Replace("DATE", DateTimeOffset.UtcNow.ToString("O"));
        File.WriteAllText(path, json);
        using var http = new HttpClient(new NoArtworkHandler());
        using var catalog = new WorldCatalog(path, http: http);
        await using var sessions = new SessionWorkspace(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), new SettingsStore(Path.Combine(directory, "settings.json")), new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        var dialog = new BrowserTestWindow(catalog, sessions);
        try
        {
            dialog.Show(); Dispatcher.UIThread.RunJobs();
            Find<Expander>(dialog, "DirectoryAdvancedSearch").IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            var results = Find<ListBox>(dialog, "DirectoryResults");
            Find<ComboBox>(dialog, "DirectoryThemeFilter").SelectedItem = "Fantasy";
            Find<ComboBox>(dialog, "DirectoryLanguageFilter").SelectedItem = "English";
            Dispatcher.UIThread.RunJobs(); Assert.Equal(2, results.Items.Count);
            Find<ComboBox>(dialog, "DirectoryPlayerKillingFilter").SelectedItem = "Not Allowed";
            Dispatcher.UIThread.RunJobs(); Assert.Equal("Amber Forest", ((WorldListing)Assert.Single(results.Items)!).Name);
            Find<TextBox>(dialog, "DirectorySearch").Text = "station";
            Dispatcher.UIThread.RunJobs(); Assert.Empty(results.Items);
            Find<Button>(dialog, "DirectoryResetFilters").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs(); Assert.Equal(3, results.Items.Count);
            Find<NumericUpDown>(dialog, "DirectoryMinimumPlayers").Value = 0;
            Dispatcher.UIThread.RunJobs(); Assert.Equal(2, results.Items.Count); // unknown is not zero
            Find<NumericUpDown>(dialog, "DirectoryMinimumPlayers").Value = 1;
            Dispatcher.UIThread.RunJobs(); Assert.Single(results.Items);
            Find<NumericUpDown>(dialog, "DirectoryMaximumPlayers").Value = 0;
            Dispatcher.UIThread.RunJobs(); Assert.Empty(results.Items);
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("Minimum players exceeds maximum") == true);
            Find<Button>(dialog, "DirectoryResetFilters").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Find<ComboBox>(dialog, "DirectoryRatingFilter").SelectedIndex = 2;
            Dispatcher.UIThread.RunJobs(); Assert.Single(results.Items);
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("10 ratings") == true);
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "1997");
            Find<CheckBox>(dialog, "DirectoryTlsFilter").IsChecked = true;
            Dispatcher.UIThread.RunJobs(); Assert.Single(results.Items);
            Find<Button>(dialog, "DirectoryResetFilters").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Find<ComboBox>(dialog, "DirectorySort").SelectedIndex = 2;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new[] { "Amber Forest", "Blue Forest", "Cloud Station" }, results.Items.Cast<WorldListing>().Select(w => w.Name));
            Capture(dialog, "directory-advanced.png");
        }
        finally { dialog.Close(); Directory.Delete(directory, true); }
    }

    [AvaloniaFact]
    public async Task ApiDirectoryDisplaysArtworkFiltersConnectionsAndWorksOffline()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wandur-api-browser-" + Guid.NewGuid());
        var cache = Path.Combine(directory, "directory.json");
        var worlds = new[]
        {
            new WorldListing { Id = "test:1", Name = "Amber Forest", Summary = "An ancient forest", Host = "forest.example.org", Port = 4000,
                BannerUrl = "https://images.example.org/forest.png", Availability = new() { Online = true }, Source = new() { Name = "Test directory" } },
            new WorldListing { Id = "test:2", Name = "Blue Mountain", Host = "mountain.example.org", TlsPort = 4443, GeneratedArtworkPath = "worlds/test:2/art" },
            new WorldListing { Id = "test:3", Name = "Web Garden", WebOnly = true }
        };
        var json = System.Text.Json.JsonSerializer.Serialize(new { format = "wandur.directory", schema_version = 2, fetched_at = DateTimeOffset.UtcNow, worlds },
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower });
        using var handler = new BrowserApiHandler(json);
        using var http = new HttpClient(handler);
        using var catalog = new WorldCatalog(cache, new Uri("http://directory.example/"), http);
        await using var sessions = new SessionWorkspace(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), new SettingsStore(Path.Combine(directory, "settings.json")), new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        var dialog = new BrowserTestWindow(catalog, sessions);
        try
        {
            dialog.Show(); Dispatcher.UIThread.RunJobs();
            await WaitFor(() => catalog.Worlds.Count == 3);
            var connection = Find<ComboBox>(dialog, "DirectoryConnectionFilter");
            var online = Find<CheckBox>(dialog, "DirectoryOnlineFilter");
            await WaitFor(() => dialog.GetVisualDescendants().OfType<Image>().Any(i => i.Source is not null));
            Assert.Contains("https://images.example.org/forest.png", handler.Requests);
            connection.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, Find<ListBox>(dialog, "DirectoryResults").Items.Count);
            online.IsChecked = true; Dispatcher.UIThread.RunJobs();
            Assert.Single(Find<ListBox>(dialog, "DirectoryResults").Items);
            online.IsChecked = false;
            Find<TextBox>(dialog, "DirectorySearch").Text = "mountain"; Dispatcher.UIThread.RunJobs();
            await WaitFor(() => dialog.GetVisualDescendants().OfType<Image>().Any(i => i.Source is not null));
            Assert.Contains("http://directory.example/worlds/test:2/art", handler.Requests);
            Find<Button>(dialog, "AddDirectoryWorld").Command!.Execute(null);
            Assert.True(Assert.Single(sessions.Active.Controller.Settings.Profiles).UseTls);
            Find<TextBox>(dialog, "DirectorySearch").Text = "";
            connection.SelectedIndex = 2; Dispatcher.UIThread.RunJobs();
            Assert.Equal("Web Garden", ((WorldListing)Assert.Single(Find<ListBox>(dialog, "DirectoryResults").Items)!).Name);
            Assert.False(Find<Button>(dialog, "AddDirectoryWorld").IsEnabled);
            dialog.Close();
            handler.Offline = true;
            using var offline = new WorldCatalog(cache, http: http);
            dialog = new BrowserTestWindow(offline, sessions); dialog.Show(); Dispatcher.UIThread.RunJobs();
            await WaitFor(() => dialog.GetVisualDescendants().OfType<Image>().Any(i => i.Source is not null));
            Assert.Equal(3, offline.Worlds.Count);
            Assert.NotNull(offline.Warning); // startup refresh failed offline; saved worlds remain (see WorldCatalogTests.FreshAndExpiredCachesSurviveFailedStartupRefresh)
            Assert.Contains(offline.BaseUri.ToString(), offline.Warning);
        }
        finally { dialog.Close(); Directory.Delete(directory, true); }
    }

    private sealed class BrowserApiHandler(string directoryJson) : HttpMessageHandler
    {
        public bool Offline { get; set; }
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (Offline) throw new HttpRequestException("offline");
            Requests.Add(request.RequestUri!.AbsoluteUri);
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Content = request.RequestUri.AbsolutePath == "/directory" ? new StringContent(directoryJson) :
                new ByteArrayContent(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAgAAAAECAYAAACzzX7wAAAAEklEQVR4nGPY0mTzHx9moL0CABxATiGmNzirAAAAAElFTkSuQmCC"));
            return Task.FromResult(response);
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SendingWithoutEchoSeparatesThePromptFromTheResponse(bool privateInput)
    {
        var window = CreateWindow();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        listener.Start();
        try
        {
            await window.Controller.StartAsync(new ConnectionProfile { Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port });
            using var server = await listener.AcceptTcpClientAsync(timeout.Token);
            await server.GetStream().WriteAsync(System.Text.Encoding.UTF8.GetBytes("Room> "), timeout.Token);
            await WaitFor(() => window.Controller.Terminal.PlainText == "Room> ");
            window.Controller.SetManualPrivate(privateInput);
            await window.Controller.SendAsync("look");
            var buffer = new byte[6];
            await server.GetStream().ReadExactlyAsync(buffer, timeout.Token);
            Assert.Equal("look\r\n", System.Text.Encoding.UTF8.GetString(buffer));
            // Some MUDs assume Enter has already moved the local cursor to a new line.
            await server.GetStream().WriteAsync(System.Text.Encoding.UTF8.GetBytes("A quiet room.\r\n> "), timeout.Token);
            await WaitFor(() => window.Controller.Terminal.PlainText.Contains("A quiet room."));
            Assert.Equal("Room> \nA quiet room.\n> ", window.Controller.Terminal.PlainText);
            Assert.DoesNotContain("look", window.Controller.Terminal.PlainText);
        }
        finally { await window.Sessions.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task SwitchingTabsPreservesDraftsAndRoutesMenusToTheSelectedSession()
    {
        var window = CreateWindow();
        try
        {
            await window.Sessions.OpenAsync();
            var first = window.Sessions.Active;
            await first.Controller.SendAsync("north");
            Dispatcher.UIThread.RunJobs(); // Materialize the browser-to-terminal content swap.
            var input = Find<TextBox>(window, "CommandInput");
            input.Text = "first draft";
            await window.Sessions.OpenAsync();
            var second = window.Sessions.Active;
            second.Controller.SetManualPrivate(true);
            Dispatcher.UIThread.RunJobs();
            Find<TextBox>(window, "CommandInput").Text = "second secret";
            window.Sessions.Select(first);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("first draft", Find<TextBox>(window, "CommandInput").Text);
            Assert.Equal('\0', Find<TextBox>(window, "CommandInput").PasswordChar);
            Assert.Equal("north", first.Controller.History.Previous(""));
            MenuCommand(window, "Session", "Disconnect").Execute(null);
            await WaitFor(() => !first.Controller.IsConnected);
            Assert.True(second.Controller.IsConnected);
            window.Sessions.Select(second);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("second secret", Find<TextBox>(window, "CommandInput").Text);
            Assert.NotEqual('\0', Find<TextBox>(window, "CommandInput").PasswordChar);
            MenuCommand(window, "File", "Close").Execute(null);
            await WaitFor(() => window.Sessions.Tabs.Count == 1);
            Assert.Same(first, window.Sessions.Active);
            Assert.False(second.Controller.IsConnected);
            Assert.Contains("The Old Market", first.Controller.Terminal.PlainText);
        }
        finally { await window.Sessions.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task SessionBarPrivateToggleDrivesOnlyThatSessionAndSyncsOnTabSwitch()
    {
        var window = CreateWindow();
        try
        {
            await window.Sessions.OpenAsync();
            var first = window.Sessions.Active;
            Dispatcher.UIThread.RunJobs(); // Materialize the browser-to-terminal content swap.
            await window.Sessions.OpenAsync();
            var second = window.Sessions.Active;
            Dispatcher.UIThread.RunJobs();
            Assert.NotSame(first, second);
            // Only the active session's TerminalView (and its own PrivateInputToggle) is in the visual tree.
            var toggle = Find<ToggleButton>(window, "PrivateInputToggle");
            Assert.NotEqual(true, toggle.IsChecked);
            toggle.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(second.Controller.ManualPrivate);
            Assert.False(first.Controller.ManualPrivate);
            first.Controller.SetManualPrivate(true);
            window.Sessions.Select(first);
            Dispatcher.UIThread.RunJobs();
            Assert.True(Find<ToggleButton>(window, "PrivateInputToggle").IsChecked);
            window.Sessions.Select(second);
            Dispatcher.UIThread.RunJobs();
            Assert.True(Find<ToggleButton>(window, "PrivateInputToggle").IsChecked);
        }
        finally { await window.Sessions.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task ComposerInputAndSendButtonFillTheTerminalPaneWidth()
    {
        var window = CreateWindow();
        window.Width = 900;
        window.Height = 700;
        Dispatcher.UIThread.RunJobs();
        try
        {
            await window.Sessions.OpenAsync();
            Dispatcher.UIThread.RunJobs();
            var composer = Find<Border>(window, "Composer");
            var terminalPane = (Control)composer.GetVisualParent()!;
            var input = Find<TextBox>(window, "CommandInput");
            var entry = (Grid)input.Parent!;
            var actions = Find<StackPanel>(window, "ComposerActions");
            var send = Find<Button>(window, "SendCommand");
            Assert.True(terminalPane.Bounds.Width > 0);
            Assert.Equal(terminalPane.Bounds.Width - 8, entry.Bounds.Width, 1);
            // Two 36px buttons with 4px between them sit left of the box; the box keeps the rest.
            Assert.Equal(76, actions.Bounds.Width, 1);
            Assert.Equal(entry.Bounds.Width - actions.Bounds.Width - send.Bounds.Width - 20, input.Bounds.Width, 1);
        }
        finally { await window.Sessions.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task LookButtonSendsLookAndTheCommandsFlyoutSendsEveryQuickCommand()
    {
        var window = CreateWindow();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        listener.Start();
        try
        {
            await window.Sessions.OpenAsync(new ConnectionProfile { Name = "Local test", Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port });
            using var server = await listener.AcceptTcpClientAsync(timeout.Token);
            Dispatcher.UIThread.RunJobs();
            var look = Find<Button>(window, "LookButton");
            var commands = Find<Button>(window, "CommandsButton");
            Assert.True(look.IsEnabled);
            Assert.True(commands.IsEnabled);
            look.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => window.Controller.CommandsSent == 1);
            Assert.Equal("look\r\n", await ReadSent(server, timeout.Token));

            var flyout = (Flyout)commands.Flyout!;
            var content = (Control)flyout.Content!;
            var buttons = content.GetLogicalDescendants().OfType<Button>().ToArray();
            Assert.Equal(["QuickLook", "QuickWho", "QuickInventory", "QuickHelp", "CompassNorth", "CompassWest", "CompassLook", "CompassEast", "CompassSouth", "CompassUp", "CompassDown"],
                buttons.Select(b => b.Name ?? "").ToArray());
            var sent = 1;
            foreach (var (name, command) in new[] { ("QuickWho", "who"), ("QuickInventory", "inventory"), ("CompassNorth", "north"), ("CompassUp", "up") })
            {
                flyout.ShowAt(commands);
                Dispatcher.UIThread.RunJobs();
                Assert.True(flyout.IsOpen);
                var button = buttons.Single(b => b.Name == name);
                Assert.True(button.IsEnabled);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                Assert.False(flyout.IsOpen); // A sent command closes the flyout.
                sent++;
                await WaitFor(() => window.Controller.CommandsSent == sent);
                Assert.Equal(command + "\r\n", await ReadSent(server, timeout.Token));
            }
        }
        finally { await window.Sessions.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task ComposerCommandButtonsAreDisabledWhileDisconnectedOrPrivate()
    {
        var window = CreateWindow();
        try
        {
            await window.Sessions.OpenAsync();
            Dispatcher.UIThread.RunJobs();
            var look = Find<Button>(window, "LookButton");
            var commands = Find<Button>(window, "CommandsButton");
            var quick = ((Control)((Flyout)commands.Flyout!).Content!).GetLogicalDescendants().OfType<Button>().ToArray();
            Assert.True(look.IsEnabled);
            Assert.True(commands.IsEnabled);
            Assert.All(quick, button => Assert.True(button.IsEnabled));
            window.Controller.SetManualPrivate(true);
            Dispatcher.UIThread.RunJobs();
            Assert.False(look.IsEnabled);
            Assert.False(commands.IsEnabled);
            Assert.All(quick, button => Assert.False(button.IsEnabled));
            window.Controller.SetManualPrivate(false);
            await window.Controller.DisconnectAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.False(window.Controller.IsConnected);
            Assert.False(look.IsEnabled);
            Assert.False(commands.IsEnabled);
            Assert.All(quick, button => Assert.False(button.IsEnabled));
        }
        finally { await window.Sessions.DisposeAsync(); window.Close(); }
    }

    private static async Task<string> ReadSent(TcpClient server, CancellationToken token)
    {
        var buffer = new byte[64];
        var count = await server.GetStream().ReadAsync(buffer, token);
        return System.Text.Encoding.UTF8.GetString(buffer, 0, count);
    }

    [AvaloniaFact]
    public async Task BackgroundOutputAndClosingOneSocketLeaveTheOtherConnectionAlive()
    {
        var window = CreateWindow();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        listener.Start();
        try
        {
            var profile = new ConnectionProfile { Name = "Local test", Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port };
            await window.Sessions.OpenAsync(profile);
            var first = window.Sessions.Active;
            using var serverOne = await listener.AcceptTcpClientAsync(timeout.Token);
            await window.Sessions.OpenAsync(profile);
            var second = window.Sessions.Active;
            using var serverTwo = await listener.AcceptTcpClientAsync(timeout.Token);
            await serverOne.GetStream().WriteAsync(System.Text.Encoding.UTF8.GetBytes("Background message\r\n"), timeout.Token);
            await WaitFor(() => first.HasActivity);
            Assert.DoesNotContain("Background message", second.Controller.Terminal.PlainText);
            window.Sessions.Select(first);
            Assert.False(first.HasActivity);
            Assert.Contains("Background message", first.Controller.Terminal.PlainText);
            await window.Sessions.CloseAsync(first);
            Assert.True(second.Controller.IsConnected);
            await second.Controller.SendAsync("look");
            var buffer = new byte[16];
            var count = await serverTwo.GetStream().ReadAsync(buffer, timeout.Token);
            Assert.Equal("look\r\n", System.Text.Encoding.UTF8.GetString(buffer, 0, count));
            Assert.Equal(0, await serverOne.GetStream().ReadAsync(buffer, timeout.Token));
            await window.Sessions.CloseAsync(second);
            Assert.Single(window.Sessions.Tabs);
            Assert.False(window.Controller.HasSession);
        }
        finally { await window.Sessions.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task PreferencesAndSavedWorldsStaySharedAcrossSessions()
    {
        var window = CreateWindow();
        try
        {
            await window.Sessions.OpenAsync();
            var first = window.Controller;
            await window.Sessions.OpenAsync();
            window.Controller.SaveSettings(window.Controller.Settings with { Theme = "Forest", Profiles = [new ConnectionProfile { Name = "Shared world", Host = "mud.example.org" }] });
            window.Sessions.Select(window.Sessions.Tabs[0]);
            Assert.Equal("Forest", first.Settings.Theme);
            Assert.Equal("Shared world", Assert.Single(first.Settings.Profiles).Name);
            window.Sessions.NewTab();
            Assert.Equal("Forest", window.Controller.Settings.Theme);
            Assert.Equal("Shared world", Assert.Single(window.Controller.Settings.Profiles).Name);
        }
        finally { await window.Sessions.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task ViewMenuTogglesEachPanelWithoutClosingSessions()
    {
        var window = CreateWindow();
        try
        {
            await window.Sessions.OpenAsync();
            MenuCommand(window, "View", "Workspace").Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(window.IsPanelVisible());
            MenuCommand(window, "View", "Workspace").Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.IsPanelVisible());
            Assert.True(window.Controller.IsConnected);
            MenuCommand(window, "View", "Show Toolbar").Execute(null);
            Assert.False(window.ToolbarVisible);
            MenuCommand(window, "View", "Show Toolbar").Execute(null);
            Assert.True(window.ToolbarVisible);
        }
        finally { await window.Sessions.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task OpeningAnotherWorldKeepsBothSessionsAliveAndIndependent()
    {
        var window = CreateWindow();
        try
        {
            await window.Sessions.OpenAsync();
            Dispatcher.UIThread.RunJobs();
            var first = window.Controller;
            await first.SendAsync("north");
            await window.Sessions.OpenAsync();
            Dispatcher.UIThread.RunJobs();
            var second = window.Controller;
            Assert.NotSame(first, second);
            Assert.True(first.IsConnected);
            Assert.True(second.IsConnected);
            Assert.DoesNotContain("The Old Market", second.Terminal.PlainText);
            Assert.Equal("draft", second.History.Previous("draft"));
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }

    [AvaloniaFact]
    public async Task DemoIsPlayableThroughTheRenderedCommandInput()
    {
        var window = CreateWindow();
        try
        {
            Capture(window, "welcome.png");
            await window.Sessions.OpenAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.Controller.IsConnected);
            var input = Find<TextBox>(window, "CommandInput");
            Assert.True(input.IsEnabled);
            input.Focus();
            window.KeyTextInput("north");
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            window.Controller.FlushOutput();
            Assert.Contains("The Old Market", window.Controller.Terminal.PlainText);
            Assert.Equal("", input.Text);
            Assert.Equal("north", window.Controller.History.Previous(""));
            await window.Controller.SendAsync("south");
            Capture(window, "demo.png");
            var transcript = Find<Iciclecreek.Terminal.TerminalView>(window, "Transcript");
            Assert.Equal(transcript.Terminal.Buffer.YBase, transcript.ViewportY);
            Assert.True(window.Controller.Display.IsFollowingTail);
            Assert.True(transcript.Terminal.Buffer.Y < transcript.Terminal.Rows, "The latest prompt must fit in the terminal viewport.");
        }
        finally { await window.Controller.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task DisconnectClearsASensitiveDraftBeforeRemovingTheMask()
    {
        var window = CreateWindow();
        try
        {
            await window.Controller.StartAsync();
            window.Controller.SetManualPrivate(true);
            Dispatcher.UIThread.RunJobs(); // Materialize the browser-to-terminal content swap.
            var input = Find<TextBox>(window, "CommandInput");
            input.Text = "unsent-secret";
            await window.Controller.DisconnectAsync();
            Assert.True(string.IsNullOrEmpty(input.Text));
            await window.Controller.StartAsync();
            Assert.True(string.IsNullOrEmpty(Find<TextBox>(window, "CommandInput").Text));
        }
        finally { await window.Controller.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task PrivateInputIsMaskedAndExcludedFromTranscriptAndHistory()
    {
        var window = CreateWindow();
        try
        {
            await window.Controller.StartAsync();
            window.Controller.SetManualPrivate(true);
            Dispatcher.UIThread.RunJobs(); // Materialize the browser-to-terminal content swap.
            var input = Find<TextBox>(window, "CommandInput");
            Assert.NotEqual('\0', input.PasswordChar);
            input.Text = "test-secret-129";
            Find<Button>(window, "SendCommand").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain("test-secret-129", window.Controller.Terminal.PlainText);
            Assert.Equal("draft", window.Controller.History.Previous("draft"));
            Assert.Equal("", input.Text);
        }
        finally { await window.Controller.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task FloatingAndResettingToolPanelsPreservesTheActiveSession()
    {
        var window = CreateWindow();
        try
        {
            await window.Controller.StartAsync();
            window.Workspace.FloatDockable(window.Workspace.WorldsTool!);
            Dispatcher.UIThread.RunJobs();
            Assert.NotEmpty(window.Workspace.HostWindows!);
            Assert.True(window.Controller.IsConnected);
            window.ResetLayout();
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.Controller.IsConnected);
            await window.Controller.SendAsync("east");
            Assert.Contains("A Bridge of Moss & Stone", window.Controller.Terminal.PlainText);
            Assert.True(Find<TextBox>(window, "CommandInput").IsEnabled);
        }
        finally { await window.Controller.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task ToolCloseButtonsHidePanelsAndRestoreKeepsTheSessionAlive()
    {
        var window = CreateWindow();
        try
        {
            await window.Controller.StartAsync();
            var closeButtons = window.GetVisualDescendants().OfType<Button>()
                .Where(b => b.Name == "PART_CloseButton" && b.IsEffectivelyVisible).ToArray();
            Assert.Equal(2, closeButtons.Length);
            for (var i = 0; i < closeButtons.Length; i++)
            {
                // Closing a nested tool dock can recreate the remaining panel headers.
                var button = window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "PART_CloseButton" && b.IsEffectivelyVisible);
                button.Focus();
                window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
                Dispatcher.UIThread.RunJobs();
            }
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain(window.GetVisualDescendants(), c => c is WorldLibraryView or MapView);
            Assert.True(window.Controller.IsConnected);
            MenuCommand(window, "View", "Restore Panels").Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Single(window.GetVisualDescendants().OfType<WorldLibraryView>());
            Assert.Single(window.GetVisualDescendants().OfType<MapView>());
            Assert.True(await window.Controller.SendAsync("north"));
            Assert.Contains("The Old Market", window.Controller.Terminal.PlainText);
        }
        finally { await window.Controller.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task SavingASecondWorldClosesTheDialogAndPreservesBothProfiles()
    {
        var window = CreateWindow();
        ProfileDialog? dialog = null;
        try
        {
            foreach (var (name, host, port) in new[] { ("Zee MUD", "mud.zeemud.org", 4000), ("d20MUD : Star Wars ( d20 / SAGA MUD )", "mud.d20mud.com", 5500) })
            {
                dialog = new ProfileDialog(new ProfileEditorViewModel(window.Controller, new ControlledDirectory()));
                dialog.Show(window);
                Dispatcher.UIThread.RunJobs();
                Find<TextBox>(dialog, "WorldHost").Text = $"{host}:{port}";
                Find<TextBox>(dialog, "WorldName").Text = name;
                Find<Button>(dialog, "SaveWorld").Command!.Execute(null);
                Dispatcher.UIThread.RunJobs();
                Assert.False(dialog.IsVisible, string.Join(" | ", dialog.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text)));
            }
            Assert.Equal(2, window.Controller.Settings.Profiles.Count);
            Assert.Equal(2, Find<ListBox>(window, "WorldProfiles").ItemCount);
            Capture(window, "compact-worlds.png");
        }
        finally { dialog?.Close(); await window.Controller.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task PastedWorldAddressFillsHostAndPortAndSavesTheSuggestedName()
    {
        var window = CreateWindow();
        var directory = new ControlledDirectory();
        var dialog = new ProfileDialog(new ProfileEditorViewModel(window.Controller, directory));
        try
        {
            dialog.Show(window);
            Dispatcher.UIThread.RunJobs();
            Find<TextBox>(dialog, "WorldHost").Text = "telnet://mud.example.org:4567/";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("mud.example.org", Find<TextBox>(dialog, "WorldHost").Text);
            Assert.Equal(4567m, Find<NumericUpDown>(dialog, "WorldPort").Value);
            await directory.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            directory.Result.SetResult(new("The Lantern", new Uri("https://www.mudconnect.com/")));
            await WaitFor(() => Find<TextBox>(dialog, "WorldName").Text == "The Lantern");
            Find<Button>(dialog, "SaveWorld").Command!.Execute(null);
            var profile = Assert.Single(window.Controller.Settings.Profiles);
            Assert.Equal("mud.example.org", profile.Host);
            Assert.Equal(4567, profile.Port);
            Assert.Equal("The Lantern", profile.Name);
        }
        finally { dialog.Close(); await window.Controller.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task LateDirectoryResultsNeverOverwriteATypedName()
    {
        var window = CreateWindow();
        var directory = new ControlledDirectory();
        var dialog = new ProfileDialog(new ProfileEditorViewModel(window.Controller, directory));
        try
        {
            dialog.Show(window);
            Dispatcher.UIThread.RunJobs();
            Find<TextBox>(dialog, "WorldHost").Text = "mud.example.org:4567";
            await directory.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Find<TextBox>(dialog, "WorldName").Text = "My own name";
            directory.Result.SetResult(new("The Lantern", new Uri("https://www.mudconnect.com/")));
            await WaitFor(() => Find<TextBlock>(dialog, "DirectoryStatus").Text?.Contains("The Lantern") == true);
            Assert.Equal("My own name", Find<TextBox>(dialog, "WorldName").Text);
        }
        finally { dialog.Close(); await window.Controller.DisposeAsync(); window.Close(); }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) { await Task.Delay(10, timeout.Token); Dispatcher.UIThread.RunJobs(); }
    }

    [AvaloniaFact]
    public async Task DirectoryFailureDoesNotPreventSavingAManuallyNamedWorld()
    {
        var window = CreateWindow();
        var directory = new ControlledDirectory();
        var dialog = new ProfileDialog(new ProfileEditorViewModel(window.Controller, directory));
        try
        {
            dialog.Show(window);
            Dispatcher.UIThread.RunJobs();
            Find<TextBox>(dialog, "WorldHost").Text = "mud.example.org:4567";
            await directory.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            directory.Result.SetException(new HttpRequestException("Offline"));
            await WaitFor(() => Find<TextBlock>(dialog, "DirectoryStatus").Text?.Contains("unavailable") == true);
            Find<TextBox>(dialog, "WorldName").Text = "My world";
            Find<Button>(dialog, "SaveWorld").Command!.Execute(null);
            Assert.Equal("My world", Assert.Single(window.Controller.Settings.Profiles).Name);
        }
        finally { dialog.Close(); await window.Controller.DisposeAsync(); window.Close(); }
    }

    private sealed class ControlledDirectory : IWorldDirectory
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<WorldNameSuggestion?> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<WorldNameSuggestion?> LookupAsync(string host, int port, CancellationToken token = default)
        {
            Started.TrySetResult();
            return Result.Task;
        }
    }

    [AvaloniaFact]
    public async Task PreferencesSaveAndCanceledPreviewRestoresTheSavedTheme()
    {
        var window = CreateWindow();
        try
        {
            var options = new OptionsDialog(new PreferencesViewModel(window.Controller, ThemeService.Apply));
            options.Show(window);
            Dispatcher.UIThread.RunJobs();
            options.FindControl<ListBox>("SettingsSections")!.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            Find<ComboBox>(options, "ThemeChoice").SelectedValue = "Forest";
            Find<Button>(options, "SavePreferences").Command!.Execute(null);
            Assert.Equal("Forest", window.Controller.Settings.Theme);
            options = new OptionsDialog(new PreferencesViewModel(window.Controller, ThemeService.Apply));
            options.Show(window);
            Dispatcher.UIThread.RunJobs();
            options.FindControl<ListBox>("SettingsSections")!.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            Find<ComboBox>(options, "ThemeChoice").SelectedValue = "Paper";
            options.Close();
            Assert.Equal("Forest", window.Controller.Settings.Theme);
            Assert.Equal(Avalonia.Styling.ThemeVariant.Dark, Application.Current!.RequestedThemeVariant);
        }
        finally { await window.Controller.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task StartingDuringSocketTeardownDoesNotLoseTheNewSessionsStatus()
    {
        var window = CreateWindow();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            await window.Controller.StartAsync(new ConnectionProfile { Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port });
            using var server = await listener.AcceptTcpClientAsync(timeout.Token);
            var stopping = window.Controller.DisconnectAsync();
            var starting = window.Controller.StartAsync();
            await Task.WhenAll(stopping, starting).WaitAsync(timeout.Token);
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.Controller.IsDemo);
            Assert.True(window.Controller.IsConnected);
            Assert.Contains("Offline demo", window.Controller.Status);
            Assert.True(await window.Controller.SendAsync("look"));
        }
        finally { await window.Controller.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task RefusedConnectionShowsAnErrorAndAllowsStartingAnotherWorld()
    {
        var window = CreateWindow();
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            await window.Controller.StartAsync(new ConnectionProfile { Host = "127.0.0.1", Port = port });
            Assert.False(window.Controller.IsConnected);
            Assert.False(window.Controller.IsConnecting);
            Assert.NotNull(window.Controller.Notice);
            await window.Controller.StartAsync();
            Assert.True(window.Controller.IsConnected);
            Assert.Null(window.Controller.Notice);
        }
        finally { await window.Controller.DisposeAsync(); window.Close(); }
    }
}
