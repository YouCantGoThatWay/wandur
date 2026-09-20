using Microsoft.Data.Sqlite;
using Wandur.Core.Agents;
using Wandur.Core.Storage;

namespace Wandur.Core.Tests;

public sealed class AgentProfileStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wandur-agent-tests-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "client.db");

    [Fact]
    public void OlderMultipleDefaultsLoadAsOneWithoutLosingAnyGoals()
    {
        var database = new ClientDatabase(DatabasePath); var store = new SqliteAgentProfileStore(database);
        var old = store.Load("world") with { Goals = [new(Guid.NewGuid(), "One", true), new(Guid.NewGuid(), "Two", true)] };
        database.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE world_agent_profiles SET payload=$payload";
            command.Parameters.AddWithValue("$payload", System.Text.Json.JsonSerializer.Serialize(old)); command.ExecuteNonQuery();
            return true;
        });
        var loaded = store.Load("world");
        Assert.Equal(2, loaded.Goals.Count); Assert.True(loaded.Goals[0].Enabled); Assert.False(loaded.Goals[1].Enabled);
        Assert.Throws<ArgumentException>(() => store.Save("world", old));
    }

    [Fact]
    public void GoalsRejectCombiningObjectivesAndPersistDescriptionsAndRules()
    {
        var first = new AgentGoal(Guid.NewGuid(), "Explore carefully", true) { Name = "Explore", Rules = "- Do not fight" };
        var second = new AgentGoal(Guid.NewGuid(), "Observe the room", true);
        Assert.Throws<ArgumentException>(() => AgentGoals.Compose([first, second]));
        var store = new SqliteAgentProfileStore(new ClientDatabase(DatabasePath));
        var profile = store.Load("world") with { Goals = [first, second with { Enabled = false }] };
        store.Save("world", profile);
        var loaded = store.Load("world");
        Assert.Equal("Explore", loaded.Goals[0].Name);
        Assert.Equal("Explore carefully", loaded.Goals[0].Text);
        Assert.Equal("- Do not fight", loaded.Goals[0].Rules);
    }

    [Fact]
    public void DefaultIdentityPersistsAndCanonicalWorldAliasesShareProfile()
    {
        var store = new SqliteAgentProfileStore(new ClientDatabase(DatabasePath));
        var original = store.Load("MUD.Example.:4000:False");
        Assert.Equal(original.Id, new SqliteAgentProfileStore(new ClientDatabase(DatabasePath)).Load("mud.example:4000:True").Id);
        var profile = original with { Model = "gemma-12b", CredentialId = Guid.NewGuid(), Goals = [new(Guid.NewGuid(), "Explore", true), new(Guid.NewGuid(), "Rest", false)] };
        Guid? saved = null;
        store.Saved += id => saved = id;
        store.Save("mud.example:4000", profile);
        Assert.Equal(profile.Id, saved);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(profile), System.Text.Json.JsonSerializer.Serialize(store.Load("mud.example:4000")));
    }

    [Fact]
    public void InvalidSaveDoesNotPublishEventOrReplaceProfile()
    {
        var store = new SqliteAgentProfileStore(new ClientDatabase(DatabasePath));
        var original = store.Load("demo");
        var events = 0;
        store.Saved += _ => events++;
        Assert.Throws<ArgumentException>(() => store.Save("demo", original with { MaxDecisions = 0 }));
        Assert.Equal(0, events);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(original), System.Text.Json.JsonSerializer.Serialize(store.Load("demo")));
    }

    [Fact]
    public void VersionTwoMigratesWithoutLosingExistingMacroPayload()
    {
        Directory.CreateDirectory(_directory);
        using (var connection = new SqliteConnection($"Data Source={DatabasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE worlds(id TEXT PRIMARY KEY NOT NULL);
                CREATE TABLE scripts(world_id TEXT NOT NULL REFERENCES worlds(id),id TEXT NOT NULL,name TEXT NOT NULL,source TEXT NOT NULL,enabled INTEGER NOT NULL,macro_json TEXT,PRIMARY KEY(world_id,id));
                INSERT INTO worlds(id) VALUES('old');
                INSERT INTO scripts VALUES('old','script','macro','',1,'{"steps":[]}');
                PRAGMA user_version=2;
                """;
            command.ExecuteNonQuery();
        }
        var database = new ClientDatabase(DatabasePath);
        new SqliteAgentProfileStore(database).Load("demo");
        database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version";
            Assert.Equal(6L, command.ExecuteScalar());
            command.CommandText = "SELECT macro_json FROM scripts WHERE id='script'";
            Assert.Equal("{\"steps\":[]}", command.ExecuteScalar());
            // Version four adds the supplied-script column without touching existing rows.
            command.CommandText = "SELECT pack_json FROM scripts WHERE id='script'";
            Assert.Equal(DBNull.Value, command.ExecuteScalar());
            return true;
        });
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
