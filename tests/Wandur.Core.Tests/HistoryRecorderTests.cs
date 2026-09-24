using Wandur.Core.History;

namespace Wandur.Core.Tests;

public sealed class HistoryRecorderTests
{
    private static HistorySession Session() => new(Guid.NewGuid().ToString("N"), "example.test:4000", "Starfall", "Mira", DateTimeOffset.UtcNow);

    [Fact]
    public async Task ACommandBoundaryDoesNotExposeTheTwoHalvesOfAKnownSecret()
    {
        var store = new RecordingHistoryStore();
        var recorder = new HistoryRecorder(store, Session(), 0);
        recorder.Received("echo hun", false, ["hunter2"]);
        recorder.Sent("look", false, ["hunter2"]);
        recorder.Received("\u001b[32mter2\u001b[0m\n", false, ["hunter2"]);
        await recorder.CompleteAsync();
        Assert.DoesNotContain(store.Records, e => e.Text.Contains("hun") || e.Text.Contains("ter2"));
        Assert.Contains(store.Records, e => e.Kind == "sent" && e.Text == "look");
    }

    [Fact]
    public async Task PrivateLineContinuationsStayHiddenEvenWithoutAKnownSecret()
    {
        var store = new RecordingHistoryStore();
        var recorder = new HistoryRecorder(store, Session(), 0);
        recorder.Received("echo hun", true, []);
        recorder.Received("ter2\nA safe new line\n", false, []);
        await recorder.CompleteAsync();
        Assert.DoesNotContain(store.Records, e => e.Text.Contains("ter2"));
        Assert.Contains(store.Records, e => e.Text == "A safe new line");
    }

    [Fact]
    public async Task ErasingTheEndOfATruncatedLineDoesNotMakeItsSecretPrefixSafe()
    {
        var store = new RecordingHistoryStore();
        var recorder = new HistoryRecorder(store, Session(), 0);
        var secret = new string('x', 5000);
        recorder.Received(secret + "\r" + new string('x', 4000) + "\u001b[K\n", false, [secret]);
        await recorder.CompleteAsync();
        Assert.DoesNotContain(store.Records, e => e.Text.Contains(new string('x', 20)));
    }

    [Fact]
    public async Task DoesNotPersistATruncatedPrefixOfALongSecret()
    {
        var store = new RecordingHistoryStore();
        var recorder = new HistoryRecorder(store, Session(), 0);
        var secret = new string('x', 5000);
        recorder.Received(secret + "\n", false, [secret]);
        recorder.Received(secret, false, [secret]);
        await recorder.CompleteAsync();
        Assert.DoesNotContain(store.Records, entry => entry.Text.Contains(new string('x', 20)));
    }

    [Fact]
    public async Task JoinsNetworkFragmentsAndStripsAnsiBeforeMaskingKnownSecrets()
    {
        var store = new RecordingHistoryStore();
        var recorder = new HistoryRecorder(store, Session(), 30);
        recorder.Received("A silver fre", false, []);
        recorder.Received("ighter\r\nEcho: hun\u001b[3", false, ["hunter2"]);
        recorder.Received("2mter2\u001b[0m is hidden.\r\n", false, ["hunter2"]);
        recorder.Sent("look", false, []);
        recorder.Received("A quiet deck > ", false, []);
        await recorder.CompleteAsync();
        Assert.Equal(new[] { "A silver freighter", "Echo: [redacted] is hidden.", "look", "A quiet deck > " }, store.Records.Select(e => e.Text));
        Assert.Equal(new[] { "received", "received", "sent", "received" }, store.Records.Select(e => e.Kind));
        Assert.Equal(new long[] { 1, 2, 3, 4 }, store.Records.Select(e => e.Sequence));
        Assert.DoesNotContain(store.Records, e => e.Text.Contains('\u001b') || e.Text.Contains("hunter2"));
    }

    [Fact]
    public async Task PrivateBoundariesDiscardPartialTextAndNeverQueueSecrets()
    {
        var store = new RecordingHistoryStore();
        var recorder = new HistoryRecorder(store, Session(), 0);
        recorder.Received("hun", false, []);
        recorder.Received("ter2\r\n", true, ["hunter2"]);
        recorder.Sent("hunter2", true, ["hunter2"]);
        recorder.Received("Checking credentials\r\n", true, ["hunter2"]);
        recorder.Received("Possible private continuation.\r\nWelcome aboard.\r\n", false, ["hunter2"]);
        await recorder.CompleteAsync();
        Assert.Equal(new[] { "private", "received" }, store.Records.Select(e => e.Kind));
        Assert.Equal("Welcome aboard.", store.Records.Last().Text);
        Assert.DoesNotContain(store.Records, e => e.Text.Contains("hun") || e.Text.Contains("credentials"));
        Assert.Equal(0, store.Prunes);
    }

    [Fact]
    public async Task StorageFailuresPauseRecordingWithoutThrowingIntoTheCaller()
    {
        var store = new RecordingHistoryStore { Fail = true };
        var recorder = new HistoryRecorder(store, Session(), 30);
        var warned = 0;
        recorder.Failed += () => Interlocked.Increment(ref warned);
        recorder.Received("public\n", false, []);
        await recorder.FlushAsync();
        recorder.Sent("look", false, []);
        await recorder.CompleteAsync();
        Assert.True(recorder.IsFaulted);
        Assert.Equal(1, warned);
    }

    [Fact]
    public async Task SlowStorageUsesABoundedQueueAndSignalsOverflow()
    {
        var store = new RecordingHistoryStore { Gate = new(false) };
        var recorder = new HistoryRecorder(store, Session(), 0);
        try
        {
            for (var i = 0; i < 10_000 && !recorder.IsFaulted; i++)
            {
                recorder.Received("line " + i + "\n", false, []);
                recorder.Flush();
            }
            Assert.True(recorder.IsFaulted);
        }
        finally { store.Gate.Set(); await recorder.CompleteAsync(); store.Gate.Dispose(); }
        Assert.True(store.Records.Count < 1000);
    }

    [Fact]
    public async Task FlushesBatchesAndUpdatesCharacterAndRetentionWithoutRewritingHistory()
    {
        var store = new RecordingHistoryStore();
        var session = Session();
        var recorder = new HistoryRecorder(store, session, 30);
        for (var i = 0; i < 100; i++) recorder.Received($"Room {i}\n", false, []);
        recorder.UpdateCharacter("Tarin");
        await recorder.FlushAsync();
        Assert.Equal(100, store.Records.Count);
        await recorder.CompleteAsync();
        Assert.InRange(store.Appends, 1, 10);
        Assert.Equal("Tarin", store.LastSession!.CharacterName);
        Assert.NotNull(store.LastSession.EndedAt);
        Assert.True(store.Prunes > 0);
    }

    private sealed class RecordingHistoryStore : IHistoryStore
    {
        public List<HistoryEntry> Records { get; } = [];
        public HistorySession? LastSession;
        public int Appends, Prunes;
        public bool Fail;
        public ManualResetEventSlim? Gate;
        public void Append(HistorySession session, IReadOnlyList<HistoryEntry> entries)
        { Gate?.Wait(); if (Fail) throw new IOException("unavailable"); Records.AddRange(entries); LastSession = session; Appends++; }
        public void Prune(DateTimeOffset before) { Prunes++; }
        public void Delete(string sessionId) => throw new NotSupportedException();
        public IReadOnlyList<HistorySession> Sessions(HistoryFilter filter, int offset = 0, int limit = 100) => throw new NotSupportedException();
        public IReadOnlyList<HistoryHit> Search(string query, HistoryFilter filter, int offset = 0, int limit = 100) => throw new NotSupportedException();
        public IReadOnlyList<HistoryEntry> Entries(string sessionId, long fromSequence = 0, int limit = 200) => throw new NotSupportedException();
    }
}
