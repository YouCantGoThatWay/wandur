using System.Text.Json;
using System.Text.Json.Nodes;
using Wandur.Core.Discovery;
using Wandur.Core.Settings;
using Wandur.Core.Storage;

namespace Wandur.Core.Tests;

public sealed class WorldThemeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wandur-theme-" + Guid.NewGuid());
    private string Cache => Path.Combine(_directory, "directory.json");
    private static JsonNode Theme => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "world-theme.json")))!;
    private void Snapshot(JsonNode? theme, bool duplicate = false)
    {
        Directory.CreateDirectory(_directory);
        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "wandur-directory.json")))!;
        root["worlds"]![0]!["theme"] = theme;
        if (duplicate) { var other = root["worlds"]![0]!.DeepClone(); other["id"] = "duplicate"; root["worlds"]!.AsArray().Add(other); }
        File.WriteAllText(Cache, root.ToJsonString());
    }

    [Fact]
    public void ServerThemeFlowsThroughListingProfileAndSqliteWithoutChangingArtIdentity()
    {
        Snapshot(Theme);
        using var catalog = new WorldCatalog(Cache);
        var world = Assert.Single(catalog.Worlds);
        Assert.Null(catalog.Warning);
        Assert.Equal("lotj-navy-cyan-gold", world.Theme!.Id);
        Assert.Equal((world with { Theme = null }).ArtKey, world.ArtKey);
        Assert.Same(world, catalog.FindEndpoint(world.Host.ToUpperInvariant() + ".", world.Port!.Value, false));
        Assert.Same(world, catalog.FindEndpoint(world.Host, world.TlsPort!.Value, true));
        Assert.Null(catalog.FindEndpoint(world.Host + ".unrelated", world.Port.Value, false));
        Assert.Null(catalog.FindEndpoint(world.Host, world.Port.Value, true));
        var profile = world.ToProfile() with { Username = "pilot", PasswordId = Guid.NewGuid() };
        var store = new SqliteSettingsStore(new ClientDatabase(Path.Combine(_directory, "wandur.db")), Path.Combine(_directory, "old.json"));
        store.Save(new ClientSettings { Profiles = [profile] });
        Assert.Equal(profile, Assert.Single(store.Load().Settings.Profiles));
    }

    [Theory]
    [InlineData("unsupported")]
    [InlineData("color")]
    [InlineData("shape")]
    [InlineData("missing")]
    [InlineData("radius")]
    [InlineData("absent")]
    public void BadOptionalThemeNeverRemovesUsableListing(string problem)
    {
        JsonNode? theme = Theme;
        switch (problem)
        {
            case "unsupported": theme["version"] = 99; break;
            case "color": theme["colors"]!["panel"] = "url(executable)"; break;
            case "shape": theme = JsonValue.Create("wrong type"); break;
            case "missing": theme.AsObject().Remove("colors"); break;
            case "radius": theme["corner_radius"] = -1; break;
            case "absent": theme = null; break;
        }
        Snapshot(theme);
        using var catalog = new WorldCatalog(Cache);
        Assert.Null(catalog.Warning);
        var world = Assert.Single(catalog.Worlds);
        Assert.True(world.CanConnect); Assert.Null(world.Theme);
    }

    [Fact]
    public void AmbiguousEndpointDoesNotSelectAnArbitraryTheme()
    {
        Snapshot(Theme, duplicate: true);
        using var catalog = new WorldCatalog(Cache);
        var world = catalog.Worlds[0];
        Assert.Null(catalog.FindEndpoint(world.Host, world.Port!.Value, false));
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
