using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Wandur.Core.Settings;
using Wandur.Core.Mapping;
using Wandur.Core.Scripting;
using Wandur.Core.Storage;

namespace Wandur.Core.Tests;

public sealed class SqliteSettingsScriptTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wandur-sqlite-settings-" + Guid.NewGuid());
    private string DatabasePath => Path.Combine(_directory, "wandur.db");
    private string LegacySettings => Path.Combine(_directory, "settings.json");
    private string ScriptsDirectory => Path.Combine(_directory, "scripts");
    private ClientDatabase Database => new(DatabasePath);
    private SqliteSettingsStore Settings => new(Database, LegacySettings);
    private SqliteWorldScriptLibraryStore Scripts => new(Database, ScriptsDirectory);

    [Fact]
    public void MalformedPersistedPaletteReportsLoadWarningInsteadOfAbortingStartup()
    {
        var theme = UserTheme.FromPreset("Ember") with { Name = "Custom" };
        Settings.Save(new() { Theme = theme.Id, CustomThemes = [theme] });
        Database.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE client_settings SET payload = $payload WHERE id = 1";
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(new ClientSettings { Theme = theme.Id, CustomThemes = [theme with { Id = null! }] }));
            return command.ExecuteNonQuery();
        });
        Assert.NotNull(Settings.Load().Warning);
        Assert.Throws<IOException>(() => Settings.Save(new()));
    }

    [Fact]
    public void CustomPalettesRoundTripWithProfilesAndRejectInvalidEditsWithoutDataLoss()
    {
        var theme = UserTheme.FromPreset("Forest") with { Name = "Endor" };
        theme.Colors["Chrome"] = "#445566";
        theme.AnsiColors[1] = "#ED5566";
        var profile = new ConnectionProfile { Name = "Kept", Host = "mud.example" };
        var original = new ClientSettings { Theme = theme.Id, CustomThemes = [theme], UseWorldThemes = false, Profiles = [profile] };
        Settings.Save(original);
        var restored = Settings.Load();
        Assert.Null(restored.Warning);
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(restored.Settings));
        var invalid = theme with { Colors = new(theme.Colors) { ["Chrome"] = "broken" } };
        Assert.Throws<ArgumentException>(() => Settings.Save(original with { CustomThemes = [invalid] }));
        Assert.Equal("#445566", Settings.Load().Settings.CustomThemes[0].Colors["Chrome"]);
        Assert.Throws<ArgumentException>(() => (original with { CustomThemes = [] }).Validate());
        Assert.Throws<ArgumentException>(() => (original with { CustomThemes = [theme, theme with { Id = "custom-other" }] }).Validate());
        Assert.Equal(profile, Assert.Single(Settings.Load().Settings.Profiles));
    }

    [Fact]
    public void SettingsImportPreservesAllProfileFieldsOrderAndOriginalThenRunsOnce()
    {
        var first = new ConnectionProfile
        {
            Name = "TLS world", Host = "mud.example", Port = 4443, UseTls = true,
            Username = "player", PasswordId = Guid.NewGuid(), AutoLogin = true, Encoding = "latin1",
            UsernamePrompt = "^Who\\?", PasswordPrompt = "^Secret\\?"
        };
        var second = new ConnectionProfile { Name = "Other", Host = "other.example", Port = 23 };
        var original = new ClientSettings { Theme = "Paper", Language = "fr", FontSize = 18, Foreground = "#112233", Background = "#F0F0F0", LocalEcho = true, Profiles = [first, second] };
        new SettingsStore(LegacySettings).Save(original);
        var bytes = File.ReadAllBytes(LegacySettings);
        var store = Settings; var loaded = store.Load();
        Assert.Null(loaded.Warning); Assert.Equal(DatabasePath, store.FilePath);
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(loaded.Settings));
        Assert.Equal(bytes, File.ReadAllBytes(LegacySettings));
        store.Save(loaded.Settings with { Theme = "Forest", Profiles = [second, first] });
        File.WriteAllText(LegacySettings, "broken backup must never be reread");
        var reopened = Settings.Load();
        Assert.Null(reopened.Warning); Assert.Equal("Forest", reopened.Settings.Theme);
        Assert.Equal(new[] { second.Id, first.Id }, reopened.Settings.Profiles.Select(p => p.Id));
        Assert.Equal(first.PasswordId, reopened.Settings.Profiles[1].PasswordId);
    }

    [Fact]
    public void InvalidSettingsImportWarnsAndRollsBackWithoutOverwritingTheOriginal()
    {
        Directory.CreateDirectory(_directory); File.WriteAllText(LegacySettings, "{broken");
        var store = Settings;
        Assert.NotNull(store.Load().Warning);
        Assert.Equal("{broken", File.ReadAllText(LegacySettings));
        Assert.Equal(0, Count("client_settings")); Assert.Equal(0, Count("profiles")); Assert.Equal(0, Count("imports"));
        Assert.Throws<IOException>(() => store.Save(new ClientSettings()));
        Assert.Equal(0, Count("client_settings"));
        new SettingsStore(LegacySettings).Save(new() { Theme = "Moonlight" });
        Assert.Equal("Moonlight", Settings.Load().Settings.Theme);
    }

    [Fact]
    public void ProfileAddressEditsPreserveWorldAndScriptIdentityAndSharedEndpoints()
    {
        var profile = new ConnectionProfile { Name = "World", Host = "old.example", Port = 4000 };
        var store = Settings; store.Save(new() { Profiles = [profile] });
        var originalWorld = World("old.example:4000");
        var maps = new SqliteRoomMapStore(Database, Path.Combine(_directory, "maps"));
        maps.Save("old.example", 4000, new MapSnapshot(
            [new("room", "Original room", "Mapped before the address edit", "City", 2, 3, 0, false) { Notes = "Retained" }],
            [], [], null, MapTrackingState.Unknown, RoomDataSource.Gmcp, 1));
        var script = new WorldScriptDefinition(Guid.NewGuid(), "Travel", "mud.send('north');", true);
        Scripts.Upsert("old.example:4000:False", script);
        var changed = profile with { Host = "new.example", Port = 5000, UseTls = true };
        var duplicate = changed with { Id = Guid.NewGuid(), Name = "Second profile", UseTls = false };
        store.Save(new() { Profiles = [duplicate, changed] });
        Assert.Equal(originalWorld, World("new.example:5000:True"));
        Assert.Equal(originalWorld, World("old.example:4000"));
        Assert.Equal("Retained", Assert.Single(maps.Load("new.example", 5000)!.Rooms).Notes);
        Assert.Contains(script, Scripts.Load("new.example:5000:False"));
        Assert.Equal(1, Count("worlds")); Assert.Equal(2, Count("profiles"));
        Assert.Equal(changed.Id, Settings.Load().Settings.Profiles[1].Id);
    }

    [Fact]
    public void ConflictingEndpointEditRollsBackProfilesSettingsAndNewAliases()
    {
        var first = new ConnectionProfile { Name = "First", Host = "first.example" };
        var second = new ConnectionProfile { Name = "Second", Host = "second.example" };
        var store = Settings; var settings = new ClientSettings { Profiles = [first, second] }; store.Save(settings);
        Assert.Throws<IOException>(() => store.Save(settings with
        {
            Theme = "Paper", Profiles = [first with { Host = "unused.example" }, second with { Host = "first.example" }]
        }));
        Assert.Equal(JsonSerializer.Serialize(settings), JsonSerializer.Serialize(Settings.Load().Settings));
        Assert.Equal(2, Count("endpoints")); Assert.Equal(2, Count("worlds"));
    }

    [Fact]
    public void ProfileRemovalLeavesWorldScriptsAvailableForAReplacementProfile()
    {
        var profile = new ConnectionProfile { Name = "World", Host = "saved.example" };
        Settings.Save(new() { Profiles = [profile] });
        var id = World("saved.example:4000");
        var script = new WorldScriptDefinition(Guid.NewGuid(), "Kept", "mud.echo('kept');", true);
        Scripts.Upsert("saved.example:4000:False", script);
        Settings.Save(new());
        Settings.Save(new() { Profiles = [profile with { Id = Guid.NewGuid() }] });
        Assert.Equal(id, World("saved.example:4000"));
        Assert.Contains(script, Scripts.Load("saved.example:4000:True"));
    }

    [Fact]
    public void ScriptLibrariesMergeTlsVariantsAndPreserveNamesSourcesAndEnabledFlags()
    {
        var plain = new WorldScriptDefinition(Guid.NewGuid(), "Gather", "mud.alias(/^g$/, () => mud.send('get all'));", true);
        var tls = new WorldScriptDefinition(Guid.NewGuid(), "Notes", "mud.echo('olá');", false);
        WriteLibrary("mud.example:4000:False", [plain]); WriteLibrary("mud.example:4000:True", [tls]);
        var before = Directory.GetFiles(ScriptsDirectory).ToDictionary(p => p, File.ReadAllBytes);
        var loaded = Scripts.Load("MUD.EXAMPLE:4000:True");
        Assert.Equal(2, loaded.Count); Assert.Contains(plain, loaded); Assert.Contains(tls, loaded);
        Scripts.Upsert("mud.example:4000:False", plain with { Source = "mud.echo('updated');", Enabled = false });
        var reopened = Scripts.Load("mud.example:4000:True");
        Assert.Equal("mud.echo('updated');", reopened.Single(s => s.Id == plain.Id).Source);
        Assert.False(reopened.Single(s => s.Id == plain.Id).Enabled);
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(ScriptsDirectory).Order());
    }

    [Fact]
    public void DeletingBeforeFirstLoadImportsOnceAndNeverResurrectsTheDeletedScript()
    {
        var script = new WorldScriptDefinition(Guid.NewGuid(), "Delete", "mud.echo('old');", true);
        WriteLibrary("mud.example:4000:False", [script]);
        Scripts.Delete("mud.example:4000:True", script.Id);
        Assert.Empty(Scripts.Load("mud.example:4000:False"));
        Assert.Empty(Scripts.Load("mud.example:4000:True"));
        Assert.True(File.Exists(LegacyPath("mud.example:4000:False", ".scripts.json")));
    }

    [Fact]
    public void UpsertBeforeFirstLoadPreservesOtherLegacyScripts()
    {
        var retained = new WorldScriptDefinition(Guid.NewGuid(), "Keep", "mud.echo('keep');", true);
        var edited = new WorldScriptDefinition(Guid.NewGuid(), "Edit", "mud.echo('before');");
        WriteLibrary("mud.example:4000:False", [retained, edited]);
        Scripts.Upsert("mud.example:4000:False", edited with { Source = "mud.echo('after');" });
        var loaded = Scripts.Load("mud.example:4000:False");
        Assert.Equal(2, loaded.Count); Assert.Contains(retained, loaded);
        Assert.Equal("mud.echo('after');", loaded.Single(s => s.Id == edited.Id).Source);
    }

    [Fact]
    public void InvalidScriptImportRollsBackEveryVariantAndRetriesAfterRepair()
    {
        var valid = new WorldScriptDefinition(Guid.NewGuid(), "Valid", "mud.echo('valid');", true);
        WriteLibrary("mud.example:4000:False", [valid]);
        Directory.CreateDirectory(ScriptsDirectory); var broken = LegacyPath("mud.example:4000:True", ".scripts.json");
        File.WriteAllText(broken, "{broken");
        Assert.Throws<IOException>(() => Scripts.Load("mud.example:4000:False"));
        Assert.Equal(0, Count("scripts")); Assert.Equal(0, Count("imports")); Assert.Equal(0, Count("worlds"));
        Assert.Equal("{broken", File.ReadAllText(broken));
        File.WriteAllText(broken, "[]");
        Assert.Equal(valid, Assert.Single(Scripts.Load("mud.example:4000:True")));
    }

    [Fact]
    public void ADeletedLegacyLibraryStaysEmptyAndOrphanFilesAreUntouched()
    {
        WriteLibrary("mud.example:4000:False", []);
        new WorldScriptStore(ScriptsDirectory).Save("mud.example:4000:False", "must not return");
        var orphan = Path.Combine(ScriptsDirectory, "unidentified.js"); File.WriteAllText(orphan, "orphan source");
        Assert.Empty(Scripts.Load("mud.example:4000:True"));
        Assert.Equal("orphan source", File.ReadAllText(orphan));
    }

    [Fact]
    public void LegacySingleSourceImportsFromThePreviousAddressWithoutWritingLibraryFiles()
    {
        var profile = new ConnectionProfile { Name = "World", Host = "old.example", Port = 4000 };
        Settings.Save(new() { Profiles = [profile] });
        new WorldScriptStore(ScriptsDirectory).Save("old.example:4000:False", "mud.echo('legacy source');");
        Settings.Save(new() { Profiles = [profile with { Host = "new.example", UseTls = true }] });
        var scripts = Scripts.Load("new.example:4000:True");
        Assert.Equal("mud.echo('legacy source');", Assert.Single(scripts).Source);
        Assert.False(scripts[0].Enabled);
        Assert.Equal(scripts[0], Assert.Single(Scripts.Load("old.example:4000:False")));
        Assert.Single(Directory.GetFiles(ScriptsDirectory));
    }

    [Fact]
    public void StarterIsPersistedOnceAndInvalidMutationsCannotChangeExistingState()
    {
        var initial = Assert.Single(Scripts.Load("demo"));
        Assert.Equal(initial, Assert.Single(Scripts.Load("demo")));
        Assert.Throws<ArgumentException>(() => Scripts.Upsert("demo", initial with { Name = "\ninvalid" }));
        Assert.Equal(initial, Assert.Single(Scripts.Load("demo")));
        Scripts.Delete("demo", initial.Id); Assert.Empty(Scripts.Load("demo"));
        Assert.False(Directory.Exists(ScriptsDirectory));
    }

    [Fact]
    public void NewlyUsedAliasesImportOrphansWithoutOverwritingNewerScriptsOrResurrectingDeletions()
    {
        var profile = new ConnectionProfile { Name = "World", Host = "old.example" };
        Settings.Save(new() { Profiles = [profile] });
        var existing = new WorldScriptDefinition(Guid.NewGuid(), "Updated", "new source", true);
        var deleted = new WorldScriptDefinition(Guid.NewGuid(), "Deleted", "old source", true);
        Scripts.Upsert("old.example:4000:False", existing); Scripts.Upsert("old.example:4000:False", deleted);
        Scripts.Delete("old.example:4000:False", deleted.Id);
        var orphan = new WorldScriptDefinition(Guid.NewGuid(), "Orphan", "orphan source");
        WriteLibrary("new.example:4000:False", [existing with { Source = "stale source" }, deleted, orphan]);
        Settings.Save(new() { Profiles = [profile with { Host = "new.example" }] });
        var loaded = Scripts.Load("new.example:4000:True");
        Assert.Contains(existing, loaded); Assert.Contains(orphan, loaded);
        Assert.DoesNotContain(loaded, script => script.Id == deleted.Id);
        Scripts.Delete("new.example:4000:False", orphan.Id);
        Assert.DoesNotContain(Scripts.Load("old.example:4000:True"), script => script.Id == orphan.Id);
    }

    [Fact]
    public void RawLegacyHostHashesRemainDiscoverableAfterCanonicalizationAndAddressEdit()
    {
        var profile = new ConnectionProfile { Name = "World", Host = "OLD.EXAMPLE." };
        Settings.Save(new() { Profiles = [profile] });
        var source = new WorldScriptDefinition(Guid.NewGuid(), "Exact hash", "mud.echo('raw hostname');", true);
        WriteLibrary("old.example.:4000:False", [source]);
        Settings.Save(new() { Profiles = [profile with { Host = "new.example" }] });
        Assert.Equal(source, Assert.Single(Scripts.Load("new.example:4000:True")));
    }

    [Fact]
    public void CorruptDatabaseSettingsWarnAndCannotBeSilentlyOverwritten()
    {
        Settings.Save(new() { Theme = "Forest" });
        Database.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE client_settings SET payload = '{broken' WHERE id = 1";
            return command.ExecuteNonQuery();
        });
        Assert.NotNull(Settings.Load().Warning);
        Assert.Throws<IOException>(() => Settings.Save(new()));
        var payload = Database.Read(connection =>
        { using var command = connection.CreateCommand(); command.CommandText = "SELECT payload FROM client_settings WHERE id = 1"; return (string)command.ExecuteScalar()!; });
        Assert.Equal("{broken", payload);
    }

    [Fact]
    public void ACorruptProfileWorldAssociationWarnsAndBlocksSave()
    {
        var first = new ConnectionProfile { Name = "First", Host = "first.example" };
        var second = new ConnectionProfile { Name = "Second", Host = "second.example" };
        Settings.Save(new() { Profiles = [first, second] });
        var secondWorld = World("second.example:4000");
        Database.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE profiles SET world_id = $world WHERE id = $id";
            command.Parameters.AddWithValue("$world", secondWorld); command.Parameters.AddWithValue("$id", first.Id.ToString("D"));
            return command.ExecuteNonQuery();
        });
        Assert.NotNull(Settings.Load().Warning);
        Assert.Throws<IOException>(() => Settings.Save(new()));
        Assert.Equal(2, Count("profiles"));
    }

    private string World(string endpoint) => Database.Write((connection, transaction) => Database.ResolveWorld(connection, transaction, endpoint));
    private long Count(string table) => Database.Read(connection =>
    { using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM " + table; return Convert.ToInt64(command.ExecuteScalar()); });
    private string LegacyPath(string key, string extension) => Path.Combine(ScriptsDirectory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))) + extension;
    private void WriteLibrary(string key, IReadOnlyList<WorldScriptDefinition> scripts)
    { Directory.CreateDirectory(ScriptsDirectory); File.WriteAllBytes(LegacyPath(key, ".scripts.json"), JsonSerializer.SerializeToUtf8Bytes(scripts)); }
    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
