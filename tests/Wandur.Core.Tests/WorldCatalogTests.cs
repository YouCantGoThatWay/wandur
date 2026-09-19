using System.Net;
using System.Text.Json;
using Wandur.Core.Discovery;

namespace Wandur.Core.Tests;

public sealed class WorldCatalogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wandur-catalog-" + Guid.NewGuid());
    private string CachePath => Path.Combine(_dir, "directory.json");
    private static string Snapshot(DateTimeOffset fetched) => JsonSerializer.Serialize(new
    {
        schema_version = 1, fetched_at = fetched, games = new object[]
        {
            new { id = 1, name = "Lantern Forest", intro = "A quiet world", description = "Wizards explore an ancient forest.", connection = new { host = "mud.example.org", port = 4000, tls_port = 4001 }, urls = new { mudverse = "https://www.mudverse.com/game/1" }, tags = new { custom = new[] { new { name = "Roleplay" } } } },
            new { id = 2, name = "Space Station", intro = "The stars await", description = "Lanterns illuminate the corridors.", connection = new { host = "mud.example.org", port = 5000 }, urls = new { mudverse = "https://www.mudverse.com/game/2" } },
            new { id = 3, name = "Web Garden", status = new { web_only = true } }
        }
    });
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { Calls++; return Task.FromResult(respond(request)); }
    }
    [Fact]
    public async Task ServerContractFixtureAndLegacyImportProduceTheSameClientCache()
    {
        var fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var expected = File.ReadAllText(Path.Combine(fixtureDirectory, "wandur-directory.json"));
        var legacy = System.Text.Json.Nodes.JsonNode.Parse(expected)!.AsObject();
        legacy.Remove("format"); legacy.Remove("worlds"); legacy["schema_version"] = 1;
        legacy["games"] = new System.Text.Json.Nodes.JsonArray(System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(fixtureDirectory, "mudverse-listing.json"))));
        foreach (var input in new[] { expected, legacy.ToJsonString() })
        {
            using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(input) }));
            using var catalog = new WorldCatalog(CachePath, http: http);
            await catalog.LoadAsync(force: true);
            Assert.Null(catalog.Warning);
            var world = Assert.Single(catalog.Search("english exploration"));
            Assert.Equal(0, world.Population.LatestCount);
            Assert.Equal(4001, world.ToProfile(tls: true).Port);
            Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(expected),
                System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(CachePath))));
        }
    }
    [Fact]
    public async Task DownloadIsStoredAsWandurDataAndReopensOffline()
    {
        var fetched = DateTimeOffset.UtcNow;
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(Snapshot(fetched)) }));
        using var catalog = new WorldCatalog(CachePath, http: http);
        await catalog.LoadAsync();
        using var json = JsonDocument.Parse(File.ReadAllText(CachePath));
        var root = json.RootElement;
        Assert.True(root.TryGetProperty("format", out var format), "Client cache must use a Wandur envelope.");
        Assert.Equal("wandur.directory", format.GetString());
        Assert.False(root.TryGetProperty("games", out _));
        Assert.Equal(fetched, root.GetProperty("fetched_at").GetDateTimeOffset());
        var forest = root.GetProperty("worlds").EnumerateArray().Single(w => w.GetProperty("name").GetString() == "Lantern Forest");
        Assert.Equal("mudverse:1", forest.GetProperty("id").GetString());
        Assert.Equal("A quiet world", forest.GetProperty("summary").GetString());
        using var offline = new HttpClient(new Handler(_ => throw new HttpRequestException("offline")));
        using var reopened = new WorldCatalog(CachePath, http: offline);
        await reopened.LoadAsync();
        Assert.NotNull(reopened.Warning);
        Assert.Equal("Lantern Forest", Assert.Single(reopened.Search("roleplay forest")).Name);
        Assert.Equal(4000, reopened.Search("lantern")[0].ToProfile().Port);
    }

    [Fact]
    public void LegacyCacheMigratesWithoutRefreshingItsAge()
    {
        Directory.CreateDirectory(_dir);
        var fetched = DateTimeOffset.UtcNow.AddDays(-2);
        File.WriteAllText(CachePath, Snapshot(fetched));
        using var catalog = new WorldCatalog(CachePath);
        Assert.Equal(3, catalog.Worlds.Count);
        using var json = JsonDocument.Parse(File.ReadAllText(CachePath));
        Assert.True(json.RootElement.TryGetProperty("worlds", out _), "Legacy cache should be migrated locally.");
        Assert.Equal(fetched, json.RootElement.GetProperty("fetched_at").GetDateTimeOffset());
    }

    [Fact]
    public async Task MappingPreservesPopulationMeaningAndUsefulMetadata()
    {
        var snapshot = System.Text.Json.Nodes.JsonNode.Parse(Snapshot(DateTimeOffset.UtcNow))!;
        var game = snapshot["games"]![0]!;
        game["name"] = "Lantern &amp; Forest";
        game["description"] = "<p>A wizard\\'s forest.</p><p>Visit &amp; explore.<br>Stay awhile.</p>";
        game["connection"]!["host"] = " MUD.EXAMPLE.ORG ";
        game["status"] = System.Text.Json.Nodes.JsonNode.Parse("""
            {"confirmed_online":true,"latest_players":0,"last_crawled":"2026-09-15T20:00:00Z",
             "last_successful_connect":"2026-09-15T20:00:00Z","mssp_collected_at":"2026-09-14T12:00:00Z"}
            """);
        game["tags"]!["categories"] = System.Text.Json.Nodes.JsonNode.Parse("""
            {"play_count":{"name":"75-100"},"theme":{"name":"Fantasy"},"type":{"name":"MUD"},
             "language":{"name":"English"},"roleplaying":{"name":"Suggested"},
             "player_killing":{"name":"Restricted"},"game_status":{"name":"Beta"},
             "game_size":{"name":"10000+"},"codebase":{"name":"Custom"},"location":{"name":"USA"}}
            """);
        game["urls"]!["website"] = "javascript:alert(1)";
        game["urls"]!["discord"] = "https://discord.gg/example";
        game["ranking"] = System.Text.Json.Nodes.JsonNode.Parse("""{"monthly_votes":123}""");
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(snapshot.ToJsonString()) }));
        using var catalog = new WorldCatalog(CachePath, http: http);
        await catalog.LoadAsync();
        using var json = JsonDocument.Parse(File.ReadAllText(CachePath));
        Assert.True(json.RootElement.TryGetProperty("worlds", out var worlds));
        var mapped = worlds.EnumerateArray().Single(w => w.GetProperty("name").GetString() == "Lantern & Forest");
        Assert.Equal("mud.example.org", mapped.GetProperty("host").GetString());
        Assert.Equal("A wizard's forest.\n\nVisit & explore.\nStay awhile.", mapped.GetProperty("description").GetString());
        var population = mapped.GetProperty("population");
        Assert.Equal(0, population.GetProperty("latest_count").GetInt32());
        Assert.Equal("75-100", population.GetProperty("reported_range").GetString());
        Assert.Equal(JsonValueKind.Null, population.GetProperty("average_count").ValueKind);
        Assert.Equal("2026-09-14T12:00:00+00:00", population.GetProperty("observed_at").GetString());
        Assert.Equal("Beta", mapped.GetProperty("features").GetProperty("development_status").GetString());
        Assert.Equal("Suggested", mapped.GetProperty("features").GetProperty("roleplaying").GetString());
        Assert.Equal("", mapped.GetProperty("website_url").GetString());
        Assert.Equal("https://discord.gg/example", mapped.GetProperty("discord_url").GetString());
        Assert.False(mapped.TryGetProperty("ranking", out _));
        Assert.Equal("Lantern & Forest", Assert.Single(catalog.Search("english beta fantasy")).Name);
        var missing = worlds.EnumerateArray().Single(w => w.GetProperty("name").GetString() == "Space Station");
        Assert.Equal(JsonValueKind.Null, missing.GetProperty("population").GetProperty("latest_count").ValueKind);
        Assert.Equal(JsonValueKind.Null, missing.GetProperty("availability").GetProperty("online").ValueKind);
    }
    [Fact]
    public async Task SearchIsLocalRanksNamesAndAcceptsTypos()
    {
        using var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(Snapshot(DateTimeOffset.UtcNow)) });
        using var http = new HttpClient(handler);
        using var catalog = new WorldCatalog(CachePath, new Uri("http://localhost/"), http);
        await catalog.LoadAsync();
        Assert.Equal("Lantern Forest", catalog.Search("lantern")[0].Name);
        Assert.Equal("Lantern Forest", Assert.Single(catalog.Search("lantren")).Name);
        Assert.Equal("Lantern Forest", Assert.Single(catalog.Search("roleplay forest")).Name);
        Assert.Empty(catalog.Search("not-a-world"));
        Assert.Equal(1, handler.Calls);
        Assert.Equal("Space Station", (await catalog.LookupAsync("MUD.EXAMPLE.ORG", 5000))!.Name);
        Assert.Equal("Lantern Forest", Assert.Single(catalog.Search("mud.example.org:4000")).Name);
        Assert.Equal("Lantern Forest", Assert.Single(catalog.Search("mud.example.org:4001")).Name);
        Assert.Null(await catalog.LookupAsync("mud.example.org", 6000));
        Assert.Equal(1, handler.Calls);
    }
    [Fact]
    public async Task FreshAndExpiredCachesSurviveFailedStartupRefresh()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(CachePath, Snapshot(DateTimeOffset.UtcNow));
        using var handler = new Handler(_ => new(HttpStatusCode.ServiceUnavailable));
        using var http = new HttpClient(handler);
        using (var fresh = new WorldCatalog(CachePath, http: http)) { await fresh.LoadAsync(); Assert.Equal(3, fresh.Worlds.Count); }
        Assert.Equal(1, handler.Calls);
        var stale = Snapshot(DateTimeOffset.UtcNow.AddDays(-2));
        File.WriteAllText(CachePath, stale);
        using var catalog = new WorldCatalog(CachePath, http: http);
        var normalized = File.ReadAllText(CachePath);
        await catalog.LoadAsync();
        Assert.Equal(3, catalog.Worlds.Count);
        Assert.NotNull(catalog.Warning);
        Assert.Equal(normalized, File.ReadAllText(CachePath));
        Assert.Equal(2, handler.Calls);
    }
    [Fact]
    public async Task BadResponseDoesNotOverwriteGoodCacheAndWebOnlyCannotConnect()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(CachePath, Snapshot(DateTimeOffset.UtcNow.AddDays(-2)));
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent("{}") }));
        using var catalog = new WorldCatalog(CachePath, http: http);
        await catalog.LoadAsync();
        Assert.NotNull(catalog.Warning);
        Assert.Equal(3, catalog.Worlds.Count);
        var web = catalog.Worlds.Single(w => w.Id == "mudverse:3");
        Assert.False(web.CanConnect);
        Assert.Throws<ArgumentException>(() => web.ToProfile());
        var mud = catalog.Worlds.Single(w => w.Id == "mudverse:1");
        var tls = mud.ToProfile(true);
        Assert.True(tls.UseTls);
        Assert.Equal(4001, tls.Port);
        Assert.NotEqual(mud.ArtKey, (mud with { Description = "Changed" }).ArtKey);
    }
    [Fact]
    public async Task SuppliedArtworkBypassesGeneratedCacheAndIsCachedLocally()
    {
        Directory.CreateDirectory(_dir);
        var snapshot = Snapshot(DateTimeOffset.UtcNow);
        File.WriteAllText(CachePath, snapshot);
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal("https://assets.mudverse.com/listings/forest.jpg", request.RequestUri!.AbsoluteUri);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        }));
        using var original = new WorldCatalog(CachePath, http: http);
        var previous = original.Worlds.Single(w => w.Id == "mudverse:1");
        var artDirectory = Path.Combine(_dir, "directory-art");
        Directory.CreateDirectory(artDirectory);
        File.WriteAllBytes(Path.Combine(artDirectory, previous.ArtKey + ".png"), [9, 9]);
        var node = System.Text.Json.Nodes.JsonNode.Parse(snapshot)!;
        node["games"]![0]!["urls"]!["banner"] = "https://assets.mudverse.com/listings/forest.jpg";
        File.WriteAllText(CachePath, node.ToJsonString());
        using var catalog = new WorldCatalog(CachePath, http: http);
        var world = catalog.Worlds.Single(w => w.Id == "mudverse:1");
        Assert.True(world.HasSuppliedArtwork);
        Assert.NotEqual(previous.ArtKey, world.ArtKey);
        Assert.Equal(new byte[] { 1, 2, 3 }, await catalog.GetArtAsync(world));
        Assert.Equal(world.ArtKey, (world with { Description = "Changed description" }).ArtKey);
        Assert.NotEqual(world.ArtKey, (world with { BannerUrl = "https://assets.mudverse.com/listings/new.jpg" }).ArtKey);
        using var offline = new HttpClient(new Handler(_ => throw new HttpRequestException("offline")));
        using var restarted = new WorldCatalog(CachePath, http: offline);
        Assert.Equal(new byte[] { 1, 2, 3 }, await restarted.GetArtAsync(world));
    }

    [Fact]
    public async Task SharedPlaceholderDoesNotOverrideGeneratedArt()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(CachePath, Snapshot(DateTimeOffset.UtcNow));
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(CachePath))!;
        node["games"]![0]!["urls"]!["banner"] = "https://assets.mudverse.com/listings/default_connectbanner.png";
        File.WriteAllText(CachePath, node.ToJsonString());
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal("http://localhost/games/1/art", request.RequestUri!.AbsoluteUri);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent([4, 5, 6]) };
        }));
        using var catalog = new WorldCatalog(CachePath, new Uri("http://localhost/"), http);
        var world = catalog.Worlds.Single(w => w.Id == "mudverse:1");
        Assert.False(world.HasSuppliedArtwork);
        Assert.Equal("", world.BannerUrl);
        Assert.Equal(new byte[] { 4, 5, 6 }, await catalog.GetArtAsync(world));
    }

    [Fact]
    public async Task NativeWandurSchemaSupportsOtherSourcesAndMeasuredAverages()
    {
        var json = """
            {"format":"wandur.directory","schema_version":2,"fetched_at":"2026-09-15T20:00:00Z",
             "worlds":[{"id":"community:forest","name":"Forest","host":"forest.example.org","tls_port":4443,
               "source":{"provider":"community","name":"Community directory","record_id":"forest"},
               "availability":{"online":false,"archived":true,"archive_reason":"Owner retired the game"},
               "population":{"average_count":12.5,"latest_count":0,"reported_range":"10-20"},
               "features":{"language":"French"},"tags":["Exploration"]}]}
            """;
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(json) }));
        using var catalog = new WorldCatalog(CachePath, http: http);
        await catalog.LoadAsync();
        var world = Assert.Single(catalog.Search("french exploration"));
        Assert.Equal("community:forest", world.Id);
        Assert.Equal(12.5m, world.Population.AverageCount);
        Assert.Equal("12.5 players on average", world.PopulationSummary);
        Assert.Equal("Archived listing", world.StatusText);
        Assert.True(world.ToProfile().UseTls);
        Assert.Equal(4443, world.ToProfile().Port);
        using var reopened = new WorldCatalog(CachePath, http: http);
        Assert.Equal(12.5m, Assert.Single(reopened.Worlds).Population.AverageCount);
    }

    [Theory]
    [InlineData("{\"schema_version\":99,\"format\":\"wandur.directory\",\"worlds\":[]}")]
    [InlineData("{\"schema_version\":2,\"format\":\"wandur.directory\",\"fetched_at\":\"2026-09-15T20:00:00Z\",\"worlds\":[{\"id\":\"one\",\"name\":\"First\"},{\"id\":\"one\",\"name\":\"Second\"}]}")]
    public async Task UnsupportedOrDuplicateSnapshotDoesNotReplaceSavedDirectory(string response)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(CachePath, Snapshot(DateTimeOffset.UtcNow.AddDays(-2)));
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(response) }));
        using var catalog = new WorldCatalog(CachePath, http: http);
        var saved = File.ReadAllText(CachePath);
        await catalog.LoadAsync();
        Assert.NotNull(catalog.Warning);
        Assert.Equal(3, catalog.Worlds.Count);
        Assert.Equal(saved, File.ReadAllText(CachePath));
    }

    [Fact]
    public async Task StartupFetchesNewMetadataDespiteUnchangedFreshProviderTimestamp()
    {
        Directory.CreateDirectory(_dir);
        var fetched = DateTimeOffset.UtcNow;
        File.WriteAllText(CachePath, Snapshot(fetched));
        var updated = Snapshot(fetched).Replace("A quiet world", "New server metadata");
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(updated) }));
        using var catalog = new WorldCatalog(CachePath, http: http);
        await catalog.LoadAsync();
        Assert.Equal("New server metadata", catalog.Worlds[0].Summary);
        Assert.Equal(fetched, catalog.FetchedAt);
    }

    [Fact]
    public async Task FailedFetchIsThrottledWithoutDiscardingSavedMetadata()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(CachePath, Snapshot(DateTimeOffset.UtcNow.AddDays(-2)));
        using var handler = new Handler(_ => new(HttpStatusCode.ServiceUnavailable));
        using var http = new HttpClient(handler);
        using var catalog = new WorldCatalog(CachePath, http: http);
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => catalog.LoadAsync()));
        Assert.Equal(1, handler.Calls);
        Assert.Equal(3, catalog.Worlds.Count);
        Assert.NotNull(catalog.Warning);
    }

    private sealed class CatalogClock : TimeProvider
    {
        public long Seconds;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Seconds;
    }

    [Fact]
    public async Task LaterFetchFindsChangedMetadataWithSameProviderTimestamp()
    {
        var clock = new CatalogClock();
        var fetched = DateTimeOffset.UtcNow;
        var snapshot = Snapshot(fetched);
        using var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(snapshot) });
        using var http = new HttpClient(handler);
        using var catalog = new WorldCatalog(CachePath, http: http, timeProvider: clock);
        await catalog.LoadAsync();
        snapshot = snapshot.Replace("A quiet world", "Changed while open");
        clock.Seconds = 299;
        await catalog.LoadAsync();
        Assert.Equal("A quiet world", catalog.Worlds[0].Summary);
        clock.Seconds = 300;
        await catalog.LoadAsync();
        Assert.Equal("Changed while open", catalog.Worlds[0].Summary);
        Assert.Equal(fetched, catalog.FetchedAt);
    }

    [Fact]
    public async Task OpeningAListedWorldRefreshesASnapshotOlderThanAMinuteAndLeavesAnUnlistedOneAlone()
    {
        var clock = new CatalogClock();
        var snapshot = Snapshot(DateTimeOffset.UtcNow);
        using var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(snapshot) });
        using var http = new HttpClient(handler);
        using var catalog = new WorldCatalog(CachePath, http: http, timeProvider: clock);
        // Nothing is listed yet, so an open does not wait for the directory.
        await catalog.RefreshBeforeOpenAsync("mud.example.org", 4000, false);
        Assert.Equal(0, handler.Calls);
        await catalog.LoadAsync();
        Assert.Equal(1, handler.Calls);
        Assert.Equal(TimeSpan.Zero, catalog.SnapshotAge);
        clock.Seconds = 59;
        await catalog.RefreshBeforeOpenAsync("mud.example.org", 4000, false);
        Assert.Equal(1, handler.Calls);
        // Older than a minute, well inside the five minute cadence: an open refreshes, and the cadence restarts from it.
        clock.Seconds = 90;
        snapshot = snapshot.Replace("A quiet world", "Changed while open");
        await catalog.RefreshBeforeOpenAsync("mud.example.org", 4000, false);
        Assert.Equal(2, handler.Calls);
        Assert.Equal("Changed while open", catalog.Worlds[0].Summary);
        Assert.Equal(TimeSpan.Zero, catalog.SnapshotAge);
        clock.Seconds = 200;
        await catalog.LoadAsync();
        Assert.Equal(2, handler.Calls);
        // A world the snapshot does not list never waits, however old the snapshot is.
        await catalog.RefreshBeforeOpenAsync("private.example.org", 4000, false);
        Assert.Equal(2, handler.Calls);
        // A snapshot from disk is of unknown age, so the first open after launch refreshes; a failure keeps it.
        using var failing = new HttpClient(new Handler(_ => new(HttpStatusCode.ServiceUnavailable)));
        using var reopened = new WorldCatalog(CachePath, http: failing, timeProvider: clock);
        Assert.Null(reopened.SnapshotAge);
        await reopened.RefreshBeforeOpenAsync("mud.example.org", 4000, false);
        Assert.Equal("Changed while open", reopened.Worlds[0].Summary);
        Assert.NotNull(reopened.Warning);
    }

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
}
