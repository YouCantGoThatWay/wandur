using Microsoft.Data.Sqlite;
using Wandur.Core.History;
using Wandur.Core.Storage;

namespace Wandur.Core.Tests;

public sealed class SessionHistoryStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wandur-history-" + Guid.NewGuid());
    private string DatabasePath => Path.Combine(_directory, "history.db");
    private ClientDatabase Database => new(DatabasePath);
    private IHistoryStore Store => new SqliteHistoryStore(Database);
    private static readonly DateTimeOffset Noon = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static HistorySession Session(string id = "one") => new(id, "fixture.example:4000", "Copper Harbor", "Mira", Noon);
    private static HistoryEntry Entry(long sequence, string text = "copper lantern") => new(sequence, Noon.AddTicks(sequence), "received", text);

    [Fact]
    public void AppendReopensInSequenceOrderAndRetriesDoNotOverwriteOrDuplicateText()
    {
        Store.Append(Session(), [Entry(2, "second"), Entry(0, "first")]);
        Store.Append(Session(), [Entry(2, "replacement"), Entry(1, "middle")]);
        SqliteConnection.ClearAllPools();
        Assert.Equal(["first", "middle", "second"], Store.Entries("one").Select(e => e.Text));
        Assert.Equal([1L, 2L], Store.Entries("one", 1).Select(e => e.Sequence));
        Assert.Equal(Noon.AddTicks(2), Store.Entries("one")[2].At);
        Assert.Empty(Store.Search("replacement", new()));
        Assert.Single(Store.Search("second", new()));
        Assert.Equal(Session(), Assert.Single(Store.Sessions(new())));
        CheckIndex();
    }

    [Fact]
    public void EmptyAppendUpdatesCharacterAndEndTimeAndAnOlderOpenBatchCannotClearTheEnd()
    {
        Store.Append(Session(), [Entry(0)]);
        var closed = Session() with { CharacterName = "Mira Vale", EndedAt = Noon.AddHours(2) };
        Store.Append(closed, []);
        Assert.Equal(closed, Assert.Single(Store.Sessions(new())));
        Store.Append(Session(), [Entry(1)]);
        Assert.Equal(closed, Assert.Single(Store.Sessions(new())));
        Store.Append(closed with { EndedAt = Noon.AddHours(1) }, []);
        Assert.Equal(closed, Assert.Single(Store.Sessions(new())));
        Assert.Equal(2, Store.Entries("one").Count);
    }

    [Theory]
    [InlineData("LANTERN copper", new long[] { 3, 2, 1, 0 })]
    [InlineData("\"copper lantern\"", new long[] { 2, 0 })]
    [InlineData("\"copper lantern\" gleams", new long[] { 2 })]
    [InlineData("copper OR lantern", new long[] { 3 })]
    [InlineData("copp*", new long[] { })]
    [InlineData("text:copper", new long[] { })]
    [InlineData("NEAR(copper,lantern)", new long[] { })]
    [InlineData("\"copper lantern", new long[] { 2, 0 })]
    [InlineData("\"\"copper\"\" lantern", new long[] { 3, 2, 1, 0 })]
    [InlineData("copper -lantern", new long[] { 3, 2, 1, 0 })]
    [InlineData("copper\" OR 1=1; --", new long[] { })]
    [InlineData("", new long[] { })]
    [InlineData(" \t\n", new long[] { })]
    [InlineData("\"\"", new long[] { })]
    [InlineData("* : () -", new long[] { })]
    public void QueryWordsAreAndedAndFtsSyntaxIsAlwaysLiteral(string query, long[] expected)
    {
        Store.Append(Session(), [Entry(0, "copper lantern"), Entry(1, "lantern beside copper"),
            Entry(2, "a copper lantern gleams"), Entry(3, "copper or distant lantern")]);
        Assert.Equal(expected, Store.Search(query, new()).Select(hit => hit.Entry.Sequence));
        Assert.Equal(4, Store.Entries("one").Count);
    }

    [Fact]
    public void SearchSupportsUnicodeAndDoesNotTreatEmbeddedNullAsTheEndOfAQuery()
    {
        Store.Append(Session(), [Entry(0, "éclat 漢字"), Entry(1, "copper")]);
        Assert.Equal(0, Assert.Single(Store.Search("ÉCLAT 漢字", new())).Entry.Sequence);
        Assert.Empty(Store.Search("copper\0missing", new()));
    }

    [Fact]
    public void DoubledQuotesInsideAPhraseStillRequireAdjacentWords()
    {
        Store.Append(Session(), [Entry(0, "copper \"lantern\" gleams"), Entry(1, "copper then lantern gleams")]);
        Assert.Equal(0, Assert.Single(Store.Search("\"copper \"\"lantern\"\" gleams\"", new())).Entry.Sequence);
    }

    [Fact]
    public void SupplementaryUnicodeLettersAreSearchable()
    {
        Store.Append(Session(), [Entry(0, "𐐀𐐁")]);
        Assert.Single(Store.Search("𐐀𐐁", new()));
    }

    [Fact]
    public void MetadataFiltersMatchUnicodeWithoutInterpretingWildcards()
    {
        Store.Append(Session() with { WorldName = "Éclat_100%", CharacterName = "Ária" }, [Entry(0)]);
        Assert.Single(Store.Search("lantern", new(World: "éCLAT_100%", Character: "ária")));
        Assert.Empty(Store.Sessions(new(World: "Éclat_200%")));
    }

    [Fact]
    public void MaximumLengthQueryAndManyWordsRemainBoundedAndValid()
    {
        Store.Append(Session(), [Entry(0, "a")]);
        Assert.Single(Store.Search("a" + new string(' ', 4095), new()));
        Assert.Single(Store.Search(string.Join(' ', Enumerable.Repeat("a", 2048)), new()));
    }

    [Fact]
    public void DatabaseFailureRollsBackMetadataEntriesAndFtsTogether()
    {
        Store.Append(Session(), [Entry(0)]);
        Scalar("CREATE TRIGGER reject_fixture_entry BEFORE INSERT ON history_entries WHEN new.sequence=2 BEGIN SELECT RAISE(ABORT,'fixture failure'); END");
        Assert.Throws<IOException>(() => Store.Append(Session() with { CharacterName = "Changed" }, [Entry(1, "rollbackmarker"), Entry(2)]));
        Assert.Equal("Mira", Assert.Single(Store.Sessions(new())).CharacterName);
        Assert.Equal(0, Assert.Single(Store.Entries("one")).Sequence);
        Assert.Empty(Store.Search("rollbackmarker", new()));
        CheckIndex();
    }

    [Fact]
    public async Task ConcurrentFirstOpenMigrationAndAppendsPreserveEverySession()
    {
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => Store.Append(Session("session" + i), [Entry(0)]))));
        Assert.Equal(8, Store.Sessions(new()).Count);
        Assert.Equal(8, Store.Search("lantern", new()).Count);
        CheckIndex();
    }

    [Fact]
    public void AllKindsAndMultilineTextRoundTripWithDuplicateSequencesInOneBatch()
    {
        Store.Append(Session(), [Entry(0) with { Kind = "sent" }, Entry(0, "duplicate"),
            Entry(1, "first\nsecond\tthird") with { Kind = "script" }, Entry(2, "[private]") with { Kind = "private" }]);
        Assert.Equal(["sent", "script", "private"], Store.Entries("one").Select(e => e.Kind));
        Assert.Equal("first\nsecond\tthird", Store.Entries("one")[1].Text);
        Assert.Single(Store.Search("\"first second\"", new()));
        Assert.Empty(Store.Search("duplicate", new()));
        CheckIndex();
    }

    [Fact]
    public void MetadataFiltersAreLiteralSubstringsAndSearchDatesAreHalfOpenEntryTimes()
    {
        Store.Append(Session(), [Entry(0), Entry(1), Entry(2)]);
        Store.Append(Session("other") with { WorldKey = "other.example:23", WorldName = "Blue Harbor", CharacterName = "Sol" }, [Entry(0)]);
        Assert.Equal(3, Store.Search("lantern", new(World: "FIXTURE.EXAMPLE")).Count);
        Assert.Equal(3, Store.Search("lantern", new(World: "COPPER", Character: "ir")).Count);
        Assert.Empty(Store.Search("lantern", new(World: "%")));
        Assert.Empty(Store.Search("lantern", new(Character: "_")));
        Assert.Empty(Store.Search("lantern", new(World: "' OR 1=1 --")));
        var filter = new HistoryFilter(From: Noon.AddTicks(1).ToOffset(TimeSpan.FromHours(5)), Until: Noon.AddTicks(2));
        Assert.Equal(1, Assert.Single(Store.Search("lantern", filter)).Entry.Sequence);
        Assert.Empty(Store.Search("lantern", new(From: Noon, Until: Noon)));
    }

    [Fact]
    public void SessionsUseStartTimeFiltersAndStableNewestFirstPagination()
    {
        Store.Append(Session("a"), []);
        Store.Append(Session("b"), []);
        Store.Append(Session("c") with { StartedAt = Noon.AddDays(1) }, []);
        Assert.Equal(["c", "a", "b"], Store.Sessions(new()).Select(s => s.Id));
        Assert.Equal("a", Assert.Single(Store.Sessions(new(), 1, 1)).Id);
        Assert.Equal(["a", "b"], Store.Sessions(new(From: Noon, Until: Noon.AddDays(1))).Select(s => s.Id));
        Assert.Equal(3, Store.Sessions(new(World: "fixture", Character: "MIR")).Count);
        Assert.Empty(Store.Sessions(new(Character: "%")));
    }

    [Fact]
    public void SearchPaginationIsStableAcrossTimestampTiesAndEntriesIncludeTheStartingSequence()
    {
        Store.Append(Session("a"), [Entry(0), Entry(1)]);
        Store.Append(Session("b"), [Entry(0)]);
        var hits = Store.Search("lantern", new());
        Assert.Equal(["a", "b", "a"], hits.Select(h => h.Session.Id));
        Assert.Equal(hits.Skip(1).Take(1), Store.Search("lantern", new(), 1, 1));
        Assert.Equal(1, Assert.Single(Store.Entries("a", 1, 1)).Sequence);
        Assert.Empty(Store.Entries("a", long.MaxValue));
        Assert.Empty(Store.Sessions(new(), int.MaxValue));
    }

    [Fact]
    public void PageSizesAreCappedForAllReadMethods()
    {
        Store.Append(Session(), Enumerable.Range(0, 510).Select(i => Entry(i)).ToArray());
        Assert.Equal(500, Store.Entries("one", limit: int.MaxValue).Count);
        Assert.Equal(500, Store.Search("lantern", new(), limit: int.MaxValue).Count);
        for (var i = 0; i < 501; i++) Store.Append(Session("session" + i), []);
        Assert.Equal(500, Store.Sessions(new(), limit: int.MaxValue).Count);
    }

    [Fact]
    public void DeleteRemovesEntriesAndFtsAndPersistsATombstoneAcrossReopen()
    {
        Store.Append(Session(), [Entry(0, "forgottenmarker")]);
        Store.Append(Session("keep"), [Entry(0, "retainedmarker")]);
        Store.Delete("one");
        Store.Delete("one");
        Store.Append(Session(), [Entry(1, "forgottenmarker")]);
        Store.Append(Session() with { EndedAt = Noon.AddHours(1) }, []);
        Assert.Empty(Store.Entries("one"));
        Assert.Equal("keep", Assert.Single(Store.Sessions(new())).Id);
        Assert.Empty(Store.Search("forgottenmarker", new()));
        Assert.Single(Store.Search("retainedmarker", new()));
        Assert.Equal(0L, Scalar("SELECT count(*) FROM history_entries_fts WHERE history_entries_fts MATCH 'forgottenmarker'"));
        CheckIndex();
    }

    [Fact]
    public void DeletionBeforeTheFirstQueuedAppendAlsoPreventsResurrection()
    {
        Store.Delete("one");
        Store.Append(Session(), [Entry(0)]);
        Assert.Empty(Store.Sessions(new()));
        Assert.Empty(Store.Search("lantern", new()));
    }

    [Fact]
    public async Task ConcurrentAppendsAndDeletionAcrossDatabaseInstancesNeverResurrectTheSession()
    {
        Store.Append(Session(), [Entry(0)]);
        await Task.WhenAll(Enumerable.Range(1, 16).Select(i => Task.Run(() =>
        {
            if (i == 8) Store.Delete("one");
            else Store.Append(Session(), [Entry(i)]);
        })));
        Assert.Empty(Store.Sessions(new()));
        Assert.Empty(Store.Entries("one"));
        CheckIndex();
    }

    [Fact]
    public void RetentionRemovesAbandonedEmptyMetadataButRecordingCanResume()
    {
        var abandoned = Session() with { StartedAt = Noon.AddDays(-60) };
        Store.Append(abandoned, [Entry(0) with { At = Noon.AddDays(-50) }]);
        Store.Prune(Noon.AddDays(-30));
        Assert.Empty(Store.Sessions(new()));
        Assert.Empty(Store.Search("copper", new()));
        // An idle but still running connection can reintroduce its metadata with new output.
        Store.Append(abandoned, [Entry(1, "new public output")]);
        Assert.Single(Store.Sessions(new()));
        Assert.Single(Store.Search("public", new()));
    }

    [Fact]
    public void PruneUsesEntryTimesIncludingActiveSessionsAndKeepsRecentEntriesInOldSessions()
    {
        var old = Session() with { StartedAt = Noon.AddDays(-60) };
        Store.Append(old, [Entry(0, "expiredmarker") with { At = Noon.AddDays(-31) }, Entry(1, "boundarymarker") with { At = Noon.AddDays(-30) }, Entry(2)]);
        Store.Append(old with { Id = "closed", EndedAt = Noon.AddDays(-1) }, [Entry(0, "expiredmarker") with { At = Noon.AddDays(-31) }, Entry(1)]);
        Store.Append(old with { Id = "expired", EndedAt = Noon.AddDays(-40) }, [Entry(0, "expiredmarker") with { At = Noon.AddDays(-41) }]);
        Store.Append(old with { Id = "old-end-recent-content", EndedAt = Noon.AddDays(-40) }, [Entry(0, "recentmarker")]);
        Store.Prune(Noon.AddDays(-30));
        Assert.Equal([1L, 2L], Store.Entries("one").Select(e => e.Sequence));
        Assert.Equal(1, Assert.Single(Store.Entries("closed")).Sequence);
        Assert.Equal(3, Store.Sessions(new()).Count);
        Assert.Single(Store.Search("recentmarker", new()));
        Assert.Empty(Store.Search("expiredmarker", new()));
        Assert.Single(Store.Search("boundarymarker", new()));
        Store.Append(old, [Entry(3, "stillrecording")]);
        Assert.Single(Store.Search("stillrecording", new()));
        CheckIndex();
    }

    [Fact]
    public void ExternalContentTriggersKeepUpdatesAndCascadedDeletesConsistent()
    {
        Store.Append(Session(), [Entry(0, "oldmarker")]);
        Scalar("UPDATE history_entries SET text='newmarker'");
        Assert.Empty(Store.Search("oldmarker", new()));
        Assert.Single(Store.Search("newmarker", new()));
        CheckIndex();
        Scalar("DELETE FROM history_sessions");
        Assert.Empty(Store.Search("newmarker", new()));
        CheckIndex();
    }

    [Fact]
    public void InvalidArgumentsFailBeforeWritingAnyPartOfABatch()
    {
        Assert.ThrowsAny<ArgumentException>(() => Store.Append(Session(), [Entry(0), Entry(-1)]));
        Assert.ThrowsAny<ArgumentException>(() => Store.Append(Session(), [Entry(0) with { Kind = "unknown" }]));
        Assert.ThrowsAny<ArgumentException>(() => Store.Append(Session() with { Id = " " }, []));
        Assert.ThrowsAny<ArgumentException>(() => Store.Append(Session() with { WorldKey = "" }, []));
        Assert.ThrowsAny<ArgumentException>(() => Store.Append(Session(), [Entry(0) with { Text = null! }]));
        Assert.ThrowsAny<ArgumentException>(() => Store.Search(new string('a', 4097), new()));
        Assert.ThrowsAny<ArgumentException>(() => Store.Search("a", new(), -1));
        Assert.ThrowsAny<ArgumentException>(() => Store.Sessions(new(), limit: 0));
        Assert.ThrowsAny<ArgumentException>(() => Store.Search("a", new(), limit: -1));
        Assert.ThrowsAny<ArgumentException>(() => Store.Entries("one", -1));
        Assert.ThrowsAny<ArgumentException>(() => Store.Entries("one", limit: 0));
        Assert.ThrowsAny<ArgumentException>(() => Store.Delete(""));
        Assert.ThrowsAny<ArgumentException>(() => Store.Entries(""));
        Assert.ThrowsAny<ArgumentException>(() => Store.Sessions(new(From: Noon.AddDays(1), Until: Noon)));
        Assert.Empty(Store.Sessions(new()));
    }

    private void CheckIndex() => Scalar("INSERT INTO history_entries_fts(history_entries_fts,rank) VALUES('integrity-check',1)");

    [Fact]
    public void VersionSixMigratesWithoutLosingExistingDataAndReopensWithFtsTriggers()
    {
        Directory.CreateDirectory(_directory);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE worlds(id TEXT PRIMARY KEY NOT NULL);
                CREATE TABLE scripts(world_id TEXT NOT NULL,id TEXT NOT NULL,name TEXT NOT NULL,source TEXT NOT NULL,enabled INTEGER NOT NULL,macro_json TEXT,pack_json TEXT,PRIMARY KEY(world_id,id));
                CREATE TABLE world_usage(world_id TEXT PRIMARY KEY NOT NULL,connections INTEGER NOT NULL,last_connected_at TEXT NOT NULL,last_character TEXT);
                INSERT INTO worlds VALUES('fixture-world');
                INSERT INTO world_usage VALUES('fixture-world',2,'2026-09-24T12:00:00Z','Fixture');
                PRAGMA user_version=6;
                """;
            command.ExecuteNonQuery();
        }
        Assert.Equal(7L, Scalar("PRAGMA user_version"));
        Assert.Equal("Fixture", Scalar("SELECT last_character FROM world_usage"));
        Assert.Equal(3L, Scalar("SELECT count(*) FROM sqlite_master WHERE type='trigger' AND tbl_name='history_entries'"));
        Assert.Equal(0L, Scalar("SELECT count(*) FROM history_entries_fts WHERE history_entries_fts MATCH 'fixture'"));
        Assert.Equal(7L, Scalar("PRAGMA user_version"));
    }

    private object? Scalar(string sql) => Database.Read(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    });

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (!Directory.Exists(_directory)) return;
        foreach (var file in Directory.EnumerateFiles(_directory)) File.Delete(file);
        Directory.Delete(_directory);
    }
}
