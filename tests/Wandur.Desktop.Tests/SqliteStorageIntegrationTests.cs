using Avalonia.Headless.XUnit;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Wandur.Core.Discovery;
using Wandur.Core.Mapping;
using Wandur.Core.Scripting;
using Wandur.Core.Settings;
using Wandur.Core.Storage;
using Wandur.Desktop.Services;
using Wandur.Desktop.Security;

namespace Wandur.Desktop.Tests;

public sealed class SqliteStorageIntegrationTests
{
    [AvaloniaFact]
    public async Task ProductionStorageRegistrationsShareOneDatabaseAndWorldSurvivesAddressEdit()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wandur-db-ui-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var profile = new ConnectionProfile { Name = "World", Host = "old.example.test", Port = 4000 };
        new SettingsStore(Path.Combine(directory, "settings.json")).Save(new ClientSettings { Profiles = [profile] });
        var services = new ServiceCollection(); services.AddClientStorage(directory);
        services.AddSingleton<IPasswordVault>(new MemoryPasswordVault());
        services.AddSingleton<IScriptRuntimeFactory>(new RecordingScriptFactory());
        services.AddSingleton<Wandur.Desktop.Terminal.ITranscriptDisplayFactory, Wandur.Desktop.Terminal.TranscriptDisplayFactory>();
        services.AddSingleton<MainWindow>();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        MainWindow? window = null;
        try
        {
            var settings = provider.GetRequiredService<ISettingsStore>();
            Assert.IsType<SqliteSettingsStore>(settings);
            Assert.IsType<SqliteRoomMapStore>(provider.GetRequiredService<IRoomMapStore>());
            Assert.IsType<SqliteWorldScriptLibraryStore>(provider.GetRequiredService<IWorldScriptLibraryStore>());
            Assert.IsType<SqliteWorldCatalogCache>(provider.GetRequiredService<IWorldCatalogCache>());
            var loaded = settings.Load(); Assert.Null(loaded.Warning);
            var maps = provider.GetRequiredService<IRoomMapStore>();
            maps.Save(profile.Host, profile.Port, new MapSnapshot([new("room", "Old inn", "Known room", "Village", 0, 0, 0, false)], [], [], null, MapTrackingState.Unknown, RoomDataSource.Text, 0));
            var scripts = provider.GetRequiredService<IWorldScriptLibraryStore>();
            var script = new WorldScriptDefinition(Guid.NewGuid(), "My script", "mud.echo('saved');", true);
            scripts.Upsert("old.example.test:4000:False", script);
            window = provider.GetRequiredService<MainWindow>(); window.Show();
            Assert.Same(provider.GetRequiredService<WorldCatalog>(), window.Catalog);
            await window.Controller.SaveWorldAsync(profile with { Host = "new.example.test" }, "", false);
            Assert.Equal("Old inn", Assert.Single(maps.Load("new.example.test", 4000)!.Rooms).Name);
            Assert.Contains(scripts.Load("new.example.test:4000:False"), s => s.Id == script.Id && s.Enabled && s.Source == script.Source);
            Assert.Equal(Wandur.Core.Protocol.TelnetOptionState.Unknown, window.Controller.ProtocolEvidence.Gmcp);
            Assert.True(File.Exists(Path.Combine(directory, "wandur.db")));
            Assert.True(File.Exists(Path.Combine(directory, "settings.json"))); // Original retained.
            Assert.False(Directory.Exists(Path.Combine(directory, "maps")));
            Assert.False(Directory.Exists(Path.Combine(directory, "scripts")));
        }
        finally
        {
            if (window is not null) { await window.Sessions.DisposeAsync(); window.Close(); }
            provider.Dispose(); SqliteConnection.ClearAllPools(); Directory.Delete(directory, true);
        }
    }
}
