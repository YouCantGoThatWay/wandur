using System.Globalization;
using Microsoft.Data.Sqlite;
using Wandur.Core.Settings;
using Wandur.Core.Storage;

namespace Wandur.Core.Tests;

/// <summary>Connections counted per world in the shared database, and the usage order the saved worlds list shows.</summary>
public sealed class WorldUsageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wandur-usage-" + Guid.NewGuid());
    private string DatabasePath => Path.Combine(_directory, "wandur.db");
    private ClientDatabase Database => new(DatabasePath);
    private SqliteSettingsStore Settings => new(Database, Path.Combine(_directory, "settings.json"));

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void MigrationFromVersionFourKeepsExistingRowsAndAddsTheUsageTables()
    {
        Directory.CreateDirectory(_directory);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE worlds(id TEXT PRIMARY KEY NOT NULL);
                CREATE TABLE endpoints(endpoint_key TEXT PRIMARY KEY NOT NULL,world_id TEXT NOT NULL REFERENCES worlds(id));
                CREATE TABLE profiles(id TEXT PRIMARY KEY NOT NULL,world_id TEXT REFERENCES worlds(id),payload TEXT NOT NULL,position INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE scripts(world_id TEXT NOT NULL REFERENCES worlds(id),id TEXT NOT NULL,name TEXT NOT NULL,source TEXT NOT NULL,enabled INTEGER NOT NULL,macro_json TEXT,pack_json TEXT,PRIMARY KEY(world_id,id));
                INSERT INTO worlds(id) VALUES('w1');
                INSERT INTO endpoints(endpoint_key,world_id) VALUES('old.example:4000','w1');
                INSERT INTO profiles(id,world_id,payload,position) VALUES('p1','w1','{}',0);
                PRAGMA user_version=4;
                """;
            command.ExecuteNonQuery();
        }
        var database = Database;
        var (version, profiles, usageRows, logRows) = database.Read(connection =>
        {
            long Scalar(string sql) { using var command = connection.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
            return (Scalar("PRAGMA user_version"), Scalar("SELECT COUNT(*) FROM profiles WHERE world_id='w1'"),
                Scalar("SELECT COUNT(*) FROM world_usage"), Scalar("SELECT COUNT(*) FROM world_connections"));
        });
        Assert.Equal(5, version);
        Assert.Equal(1, profiles);
        Assert.Equal(0, usageRows); Assert.Equal(0, logRows);
        // The pre-existing world takes a count without any further migration.
        new SqliteWorldUsageStore(database).RecordConnection("old.example", 4000);
        Assert.Equal(1, Count("world_usage")); Assert.Equal(1, Count("world_connections"));
    }

    [Fact]
    public void RecordingAConnectionIncrementsTheCountStampsTheTimeAndFollowsTheWorldThroughAnAddressEdit()
    {
        var first = new ConnectionProfile { Name = "First", Host = "first.example", Port = 4000 };
        var second = new ConnectionProfile { Name = "Second", Host = "second.example", Port = 5000 };
        Settings.Save(new ClientSettings { Profiles = [first, second] });
        var clock = new Clock();
        var store = new SqliteWorldUsageStore(Database, clock);
        Assert.Empty(store.Load());
        store.RecordConnection("second.example", 5000);
        clock.Now = clock.Now.AddHours(3);
        store.RecordConnection("second.example", 5000);
        var usage = store.Load();
        Assert.False(usage.ContainsKey(first.Id));
        Assert.Equal(2, usage[second.Id].Connections);
        Assert.Equal(clock.Now, usage[second.Id].LastConnectedAt);
        var (count, stamp) = Database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT connections, last_connected_at FROM world_usage";
            using var rows = command.ExecuteReader(); Assert.True(rows.Read());
            return (rows.GetInt64(0), rows.GetString(1));
        });
        Assert.Equal(2, count);
        Assert.Equal(clock.Now, DateTimeOffset.ParseExact(stamp, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
        // The count belongs to the world, which an address edit keeps.
        Settings.Save(Settings.Load().Settings with { Profiles = [first, second with { Host = "moved.example" }] });
        Assert.Equal(2, store.Load()[second.Id].Connections);
        // A world that is no longer saved has no profile to hang the usage on.
        Settings.Save(Settings.Load().Settings with { Profiles = [first] });
        Assert.Empty(store.Load());
    }

    [Fact]
    public void OrderWeighsRecentConnectionsAndKeepsNeverConnectedWorldsInTheirManualOrder()
    {
        var now = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        var a = new ConnectionProfile { Name = "Alpha", Host = "a.example" };
        var b = new ConnectionProfile { Name = "Bravo", Host = "b.example" };
        var c = new ConnectionProfile { Name = "Charlie", Host = "c.example" };
        var d = new ConnectionProfile { Name = "Delta", Host = "d.example" };
        var e = new ConnectionProfile { Name = "Echo", Host = "e.example" };
        var usage = new Dictionary<Guid, WorldUsage>
        {
            // Three connections in the spring weigh 0.75 together: less than one played this week.
            [a.Id] = new([now.AddDays(-120), now.AddDays(-150), now.AddDays(-200)]),
            [b.Id] = new([now.AddDays(-1)]),
            // Two fading connections weigh 1.0, the same as Bravo's one; Bravo's is more recent and wins the tie.
            [d.Id] = new([now.AddDays(-40), now.AddDays(-50)]),
            [e.Id] = new([])
        };
        Assert.Equal(0.75, usage[a.Id].Score(now)); Assert.Equal(1.0, usage[b.Id].Score(now)); Assert.Equal(1.0, usage[d.Id].Score(now));
        var ordered = WorldUsage.Order([e, a, c, d, b], usage, now);
        Assert.Equal(["Bravo", "Delta", "Alpha", "Echo", "Charlie"], ordered.Select(p => p.Name));
        // No usage at all leaves the manual order untouched, as the same list.
        var manual = new[] { c, a };
        Assert.Same(manual, WorldUsage.Order(manual, new Dictionary<Guid, WorldUsage>(), now));
        // Equal score and time fall back to the name.
        var tie = new Dictionary<Guid, WorldUsage> { [c.Id] = new([now]), [a.Id] = new([now]) };
        Assert.Equal(["Alpha", "Charlie"], WorldUsage.Order([c, a], tie, now).Select(p => p.Name));
    }

    private long Count(string table) => Database.Read(connection =>
    { using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM " + table; return Convert.ToInt64(command.ExecuteScalar()); });
    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
