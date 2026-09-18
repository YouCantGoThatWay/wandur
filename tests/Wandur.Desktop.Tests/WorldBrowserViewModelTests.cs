using Avalonia.Headless.XUnit;
using Wandur.Core.Discovery;
using Wandur.Core.Settings;
using Wandur.Desktop.ViewModels;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Desktop.Views;

namespace Wandur.Desktop.Tests;

public sealed class WorldBrowserViewModelTests
{
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FirstAttachmentRendersCurrentDetailsEvenWithoutModelChanges(bool empty)
    {
        await using var fixture = new Fixture(empty);
        var browser = new WorldBrowserView(fixture.Model, fixture.Catalog);
        var window = new Window { Content = browser, Width = 1000, Height = 700 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var text = browser.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToArray();
            if (empty)
            {
                Assert.Contains(fixture.Model.EmptyTitle, text);
                Assert.Contains(fixture.Model.EmptyDescription, text);
            }
            else
            {
                Assert.Contains(fixture.Model.SelectedWorld!.Name, text);
                Assert.Single(browser.GetVisualDescendants().OfType<Button>(), b => b.Name == "ConnectDirectoryWorld");
            }
            Assert.Equal(Wandur.Core.Localization.Strings.FindAMUD, fixture.Sessions.Active.Title);
            Assert.Equal(Wandur.Core.Localization.Strings.FindAMUDInTheDirectory, fixture.Sessions.Active.Endpoint);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task SearchPageRetainsQueryAndSelectionWithoutReplacingLiveSessions()
    {
        await using var fixture = new Fixture();
        var content = new SessionContentView(fixture.Sessions, catalog: fixture.Catalog);
        var window = new Window { Content = content, Width = 1000, Height = 700 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var browser = Assert.Single(content.GetVisualDescendants().OfType<WorldBrowserView>());
            var search = browser.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "DirectorySearch");
            search.Text = "Blue";
            await fixture.Sessions.OpenAsync(); Dispatcher.UIThread.RunJobs();
            var session = fixture.Sessions.Active;
            Assert.True(session.Controller.IsConnected);
            Assert.Empty(content.GetVisualDescendants().OfType<WorldBrowserView>());
            fixture.Sessions.Browse(); Dispatcher.UIThread.RunJobs();
            Assert.Same(browser, Assert.Single(content.GetVisualDescendants().OfType<WorldBrowserView>()));
            Assert.Equal("Blue", search.Text);
            Assert.Equal("Blue", Assert.IsType<WorldListing>(browser.GetVisualDescendants().OfType<ListBox>().Single(t => t.Name == "DirectoryResults").SelectedItem).Name);
            Assert.Single(fixture.Sessions.Tabs);
            session.Controller.ShowNotice("Background event"); Dispatcher.UIThread.RunJobs();
            Assert.True(fixture.Sessions.IsBrowsing);
            Assert.True(session.Controller.IsConnected);
            fixture.Sessions.Select(session); Dispatcher.UIThread.RunJobs();
            Assert.Single(content.GetVisualDescendants().OfType<TerminalView>());
            fixture.Sessions.Browse(); Dispatcher.UIThread.RunJobs();
            // Rebuilding docking layout also reuses the model and query.
            window.Content = new SessionContentView(fixture.Sessions, catalog: fixture.Catalog);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Blue", window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "DirectorySearch").Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task DirectoryDetailsRetainScrollWhenReturningAndAllowScrollingBackUp()
    {
        await using var fixture = new Fixture(longDescription: true);
        var content = new SessionContentView(fixture.Sessions, catalog: fixture.Catalog);
        var window = new Window { Content = content, Width = 1000, Height = 700 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var browser = content.GetVisualDescendants().OfType<WorldBrowserView>().Single();
            browser.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "DirectorySearch").Text = "Blue";
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var scroll = browser.GetVisualDescendants().OfType<ScrollViewer>().Single(t => t.Name == "DirectoryDetailsScroll");
            scroll.Offset = new Avalonia.Vector(0, 250);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(250, scroll.Offset.Y);
            await fixture.Sessions.OpenAsync(); Dispatcher.UIThread.RunJobs();
            fixture.Sessions.Browse(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Equal(250, scroll.Offset.Y);
            scroll.Offset = new Avalonia.Vector(0, 50); Dispatcher.UIThread.RunJobs();
            var model = fixture.Sessions.Browser(fixture.Catalog);
            model.Query = model.Query with { Sort = 2 }; Dispatcher.UIThread.RunJobs();
            Assert.Equal(50, scroll.Offset.Y);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task QueryCombinesFacetsAndMeasurementsWithoutTreatingUnknownAsZero()
    {
        await using var fixture = new Fixture();
        var model = fixture.Model;
        model.Query = new() { MinimumPlayers = 0 };
        Assert.Equal(new[] { "Amber", "Blue" }, model.Results.Select(w => w.Name));
        model.Query = model.Query with { MinimumPlayers = 1, MaximumPlayers = 0 };
        Assert.Empty(model.Results);
        Assert.Contains("Minimum players exceeds maximum", model.FilterHint);
        model.Query = new() { Facets = new Dictionary<string, string> { ["Theme"] = "fantasy", ["Language"] = "English" }, TlsOnly = true, Rating = 2 };
        Assert.Equal("Amber", Assert.Single(model.Results).Name);
        model.Query = model.Query with { Search = "station" };
        Assert.Empty(model.Results);
        model.ResetFiltersCommand.Execute(null);
        Assert.Equal(3, model.Results.Count);
        Assert.Empty(model.Query.Facets);
        Assert.Null(model.Query.MinimumPlayers);
    }

    [AvaloniaFact]
    public async Task SortingRetainsSelectionAndSavingDeduplicatesEndpointAndTls()
    {
        await using var fixture = new Fixture();
        var model = fixture.Model;
        model.SelectedWorld = model.Results.Single(w => w.Name == "Blue");
        model.Query = new() { Sort = 2 };
        Assert.Equal(new[] { "Amber", "Blue", "Station" }, model.Results.Select(w => w.Name));
        Assert.Equal("Blue", model.SelectedWorld!.Name);
        model.SelectedWorld = model.Results.Single(w => w.Name == "Amber");
        model.SaveCommand.Execute(null);
        var saved = Assert.Single(fixture.Sessions.Active.Controller.Settings.Profiles);
        // A directory update may rename the world or change host capitalization.
        model.SelectedWorld = model.SelectedWorld! with { Name = "Renamed", Host = "AMBER.EXAMPLE.ORG" };
        Assert.Same(saved, model.SaveSelectedWorld());
        Assert.Single(fixture.Sessions.Active.Controller.Settings.Profiles);
        Assert.False(fixture.Sessions.Active.Controller.HasSession);
        model.UseTls = true;
        model.SaveCommand.Execute(null);
        Assert.Equal(2, fixture.Sessions.Active.Controller.Settings.Profiles.Count);
        Assert.Contains(fixture.Sessions.Active.Controller.Settings.Profiles, p => p.UseTls && p.Port == 4001);
        model.SelectedWorld = model.Results.Single(w => w.Name == "Station");
        Assert.False(model.SaveCommand.CanExecute(null));
        Assert.False(model.ConnectCommand.CanExecute(null));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "wandur-browser-vm-" + Guid.NewGuid());
        private readonly WorldCatalog _catalog;
        public WorldCatalog Catalog => _catalog;
        public SessionWorkspace Sessions { get; }
        public WorldBrowserViewModel Model { get; }
        public Fixture(bool empty = false, bool longDescription = false)
        {
            Directory.CreateDirectory(_directory);
            var cache = Path.Combine(_directory, "directory.json");
            File.WriteAllText(cache, """
                {"schema_version":1,"fetched_at":"DATE","games":[
                  {"id":1,"name":"Amber","connection":{"host":"amber.example.org","port":4000,"tls_port":4001},
                   "status":{"latest_players":12,"confirmed_online":true},"reviews":{"average_rating":4.5,"rating_count":10},
                   "tags":{"categories":{"theme":{"name":"Fantasy"},"language":{"name":"English"}}}},
                  {"id":2,"name":"Blue","connection":{"host":"blue.example.org","port":4000},"status":{"latest_players":0},
                   "tags":{"categories":{"theme":{"name":"Fantasy"},"language":{"name":"English"}}}},
                  {"id":3,"name":"Station","status":{"web_only":true},"tags":{"categories":{"theme":{"name":"Sci-Fi"}}}}
                ]}
                """.Replace("DATE", DateTimeOffset.UtcNow.ToString("O")));
            if (longDescription)
            {
                var snapshot = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(cache))!;
                snapshot["games"]![1]!["description"] = string.Join("\n\n", Enumerable.Repeat("A long road beside the blue river. There is much to discover here.", 100));
                File.WriteAllText(cache, snapshot.ToJsonString());
            }
            if (empty)
            {
                var snapshot = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(cache))!;
                snapshot["games"] = new System.Text.Json.Nodes.JsonArray();
                File.WriteAllText(cache, snapshot.ToJsonString());
            }
            _catalog = new WorldCatalog(cache);
            Sessions = new SessionWorkspace(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), new SettingsStore(Path.Combine(_directory, "settings.json")), new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
            Model = new WorldBrowserViewModel(_catalog, Sessions);
        }
        public async ValueTask DisposeAsync()
        {
            Model.Dispose(); _catalog.Dispose(); await Sessions.DisposeAsync();
            Directory.Delete(_directory, true);
        }
    }
}
