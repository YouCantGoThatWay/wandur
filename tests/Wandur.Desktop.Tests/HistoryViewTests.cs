using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.History;
using Wandur.Core.Localization;
using Wandur.Core.Settings;
using Wandur.Core.Storage;
using Wandur.Desktop.ViewModels;
using Wandur.Desktop.Views;

namespace Wandur.Desktop.Tests;

[Collection(UiLanguageCollection.Name)]
public sealed class HistoryViewTests
{
    private static readonly DateTimeOffset Time = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static HistorySession Session(string id = "one") => new(id, "harbor.test:4000", "Lantern Harbor", "Rowan", Time);

    [Fact]
    public async Task PagesAreBoundedAndSearchPreservesLiteralQueryAndFilters()
    {
        var store = new MemoryHistory();
        for (var i = 0; i < 125; i++) store.Append(Session(i.ToString()), [new(i, Time, "received", "lantern")]);
        using var model = new HistoryViewModel(store);
        await model.RefreshAsync();
        Assert.Equal(50, model.Sessions.Count);
        Assert.True(model.HasNextSessions);
        await model.NextSessionsAsync();
        Assert.Equal("50", model.Sessions[0].Id);
        await model.PreviousSessionsAsync();
        Assert.Equal("0", model.Sessions[0].Id);

        model.Query = "lantern \"quiet pier\" OR *";
        model.World = "harbor.test:4000";
        model.Character = "Rowan";
        model.From = Time;
        model.Until = Time.AddDays(1);
        await model.RefreshAsync();
        Assert.Equal(model.Query, store.LastQuery);
        Assert.Equal(new HistoryFilter(model.World, model.Character, model.From, model.Until), store.LastFilter);
        Assert.Equal(50, model.Results.Count);
        Assert.True(model.HasNextResults);
        await model.NextResultsAsync();
        Assert.Equal("50", model.Results[0].Session.Id);
        Assert.True(store.MaximumLimit <= 101);
        model.From = Time.AddDays(2);
        await model.RefreshAsync();
        Assert.NotEmpty(model.Error);
        Assert.Empty(model.Results);
    }

    [Fact]
    public async Task SlowSearchCannotOverwriteNewQueryOrPublishAfterDispose()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var store = new MemoryHistory { OnSearch = query =>
        {
            if (query == "old") { entered.SetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); }
            return [new(Session(), new(1, Time, "received", query))];
        } };
        using var model = new HistoryViewModel(store) { Query = "old" };
        var old = model.RefreshAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        model.Query = "new";
        await model.RefreshAsync();
        Assert.Equal("new", Assert.Single(model.Results).Entry.Text);
        release.Set();
        await old;
        Assert.Equal("new", Assert.Single(model.Results).Entry.Text);
        model.Dispose();
        await model.RefreshAsync();
        Assert.Equal("new", Assert.Single(model.Results).Entry.Text);
    }

    [Fact]
    public async Task ContextOpensAroundHitAndPagesChronologicallyAcrossSparseSequences()
    {
        var store = new MemoryHistory();
        store.Append(Session(), Enumerable.Range(1, 350).Select(i => new HistoryEntry(i * 10, Time.AddSeconds(i), "received", $"Line {i}")).ToArray());
        using var model = new HistoryViewModel(store);
        await model.OpenHitAsync(new(Session(), new(2200, Time, "received", "Line 220")));
        Assert.Contains(model.Transcript, e => e.Sequence == 2200);
        var first = model.Transcript[0].Sequence;
        await model.PreviousContextAsync();
        Assert.True(model.Transcript[^1].Sequence < first);
        Assert.Equal(100, model.Transcript.Count);
        await model.NextContextAsync();
        Assert.Equal(first, model.Transcript[0].Sequence);
        Assert.Equal(model.Transcript.OrderBy(e => e.Sequence), model.Transcript);
        Assert.True(store.MaximumLimit <= 101);
    }

    [Fact]
    public async Task NewContextAndDisposalRejectLateTranscriptLoads()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var store = new MemoryHistory { OnEntries = (id, _) =>
        {
            if (id == "slow") { entered.TrySetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); }
            return [new(1, Time, "received", id)];
        } };
        using var model = new HistoryViewModel(store);
        var old = model.OpenSessionAsync(Session("slow"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await model.OpenSessionAsync(Session("new"));
        Assert.Equal("new", Assert.Single(model.Transcript).Text);
        model.Dispose();
        release.Set();
        await old;
        Assert.Equal("new", Assert.Single(model.Transcript).Text);
    }

    [Fact]
    public async Task PreviousContextNearTheEndDoesNotRescanAContiguousSession()
    {
        var reads = 0;
        var store = new MemoryHistory { OnEntries = (_, from) =>
        {
            reads++;
            return Enumerable.Range((int)Math.Max(1, from), 101).Where(i => i <= 10000)
                .Select(i => new HistoryEntry(i, Time, "received", $"Line {i}")).ToArray();
        } };
        using var model = new HistoryViewModel(store);
        await model.OpenHitAsync(new(Session(), new(9000, Time, "received", "Line 9000")));
        reads = 0;
        await model.PreviousContextAsync();
        Assert.Equal(8850, model.Transcript[0].Sequence);
        Assert.Equal(8949, model.Transcript[^1].Sequence);
        Assert.True(reads <= 3, $"Previous context used {reads} reads for one page.");
    }

    [AvaloniaFact]
    public async Task OpenedSearchHitIsSelectedInsideItsSurroundingTranscript()
    {
        var store = new MemoryHistory();
        store.Append(Session(), Enumerable.Range(1, 350).Select(i => new HistoryEntry(i, Time, "received", $"Line {i}")).ToArray());
        var window = new HistoryWindow(store);
        try
        {
            window.Show();
            await window.Model.OpenHitAsync(new(Session(), new(220, Time, "received", "Line 220")));
            Layout(window);
            var transcript = Named<TextBox>(window, "HistoryTranscript");
            Assert.Equal("Line 220", transcript.SelectedText);
            Assert.Contains("Line 219", transcript.Text);
            Assert.Contains("Line 221", transcript.Text);
        }
        finally { window.Close(); }
    }

    [Fact]
    public async Task RealTemporaryStoreFiltersAndDeletesThroughTheViewModel()
    {
        var path = Path.Combine(Path.GetTempPath(), "wandur-history-ui-" + Guid.NewGuid(), "fixture.db");
        var store = new SqliteHistoryStore(new ClientDatabase(path));
        try
        {
            await Task.Run(() =>
            {
                store.Append(Session(), [new(1, Time, "received", "The copper lantern glows."), new(2, Time.AddMinutes(1), "received", "The lantern is made of copper.")]);
                store.Append(Session("two") with { CharacterName = "Vale" }, [new(1, Time, "received", "A copper lantern.")]);
            }, TestContext.Current.CancellationToken);
            using var model = new HistoryViewModel(store) { Query = "\"copper lantern\"", Character = "Rowan", From = Time, Until = Time.AddDays(1) };
            await model.RefreshAsync();
            Assert.Equal("The copper lantern glows.", Assert.Single(model.Results).Entry.Text);
            await model.OpenHitAsync(model.Results[0]);
            Assert.Equal(2, model.Transcript.Count);
            model.RequestDelete();
            await model.DeleteAsync();
            Assert.Empty(model.Results);
            Assert.Empty(model.Sessions);
            Assert.Equal("two", Assert.Single(store.Sessions(new())).Id);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [AvaloniaFact]
    public async Task SelectingAResultAgainAfterBrowsingASessionRestoresItsContext()
    {
        var store = new MemoryHistory();
        store.Append(Session(), [new(1, Time, "received", "one")]);
        store.Append(Session("two"), [new(1, Time, "received", "two")]);
        var window = new HistoryWindow(store);
        try
        {
            window.Show();
            window.Model.Query = "one";
            await window.Model.RefreshAsync();
            Layout(window);
            Named<ListBox>(window, "HistoryResults").SelectedIndex = 0;
            await WaitUntil(() => !window.Model.IsContextBusy && window.Model.ActiveSession?.Id == "one");
            Named<TabControl>(window, "HistoryBrowseTabs").SelectedIndex = 0;
            Layout(window);
            Named<ListBox>(window, "HistorySessions").SelectedIndex = 1;
            await WaitUntil(() => !window.Model.IsContextBusy && window.Model.ActiveSession?.Id == "two");
            Named<TabControl>(window, "HistoryBrowseTabs").SelectedIndex = 1;
            Layout(window);
            Assert.Null(Named<ListBox>(window, "HistoryResults").SelectedItem);
            Named<ListBox>(window, "HistoryResults").SelectedIndex = 0;
            await WaitUntil(() => !window.Model.IsContextBusy && window.Model.ActiveSession?.Id == "one");
            Assert.Equal("one", Assert.Single(window.Model.Transcript).Text);
        }
        finally { window.Close(); }
    }

    [Fact]
    public async Task DeletionRequiresConfirmationAndRefreshesSessionsResultsAndContext()
    {
        var store = new MemoryHistory();
        store.Append(Session(), [new(1, Time, "received", "lantern")]);
        store.Append(Session("two"), [new(1, Time, "received", "lantern")]);
        using var model = new HistoryViewModel(store) { Query = "lantern" };
        await model.RefreshAsync();
        await model.OpenSessionAsync(model.Sessions[0]);
        model.RequestDelete();
        Assert.True(model.ConfirmDelete);
        Assert.Equal(2, store.Sessions(new()).Count);
        model.CancelDelete();
        await model.DeleteAsync();
        Assert.Equal(2, store.Sessions(new()).Count);
        model.RequestDelete();
        await model.DeleteAsync();
        Assert.Equal("two", Assert.Single(model.Sessions).Id);
        Assert.Equal("two", Assert.Single(model.Results).Session.Id);
        Assert.Empty(model.Transcript);
        Assert.Null(model.ActiveSession);
    }

    [AvaloniaFact]
    public async Task StoreOperationsRunOffTheUiThreadAndFailuresAreVisible()
    {
        var store = new MemoryHistory { OnSearch = _ =>
        {
            Assert.False(Dispatcher.UIThread.CheckAccess());
            throw new IOException("database error with sensitive details");
        } };
        using var model = new HistoryViewModel(store) { Query = "lantern" };
        await model.RefreshAsync();
        Assert.NotEmpty(model.Error);
        Assert.DoesNotContain("sensitive", model.Error);
        Assert.False(model.IsBusy);
    }

    [AvaloniaTheory]
    [InlineData("Slate", "en", 1100)]
    [InlineData("Paper", "en", 860)]
    [InlineData("Slate", "de", 860)]
    [InlineData("Slate", "es", 860)]
    [InlineData("Slate", "fr", 860)]
    [InlineData("Slate", "pt-BR", 860)]
    public async Task WindowSearchOpenConfirmAndReadOnlyTranscriptRender(string theme, string language, int width)
    {
        var store = new MemoryHistory();
        store.Append(Session(), Enumerable.Range(1, 125).Select(i => new HistoryEntry(i, Time.AddSeconds(i), i % 5 == 0 ? "sent" : "received",
            i % 5 == 0 ? "look" : "A lantern lights the quiet pier. The ferry bell sounds across the water.")).ToArray());
        UiLanguage.Apply(language);
        ThemeService.Apply(new ClientSettings { Theme = theme });
        var window = new HistoryWindow(store) { Width = width, Height = 800 };
        try
        {
            window.Show();
            await window.Model.RefreshAsync();
            Named<TextBox>(window, "HistoryQuery").Text = "lantern";
            Click(window, "HistorySearch");
            await WaitUntil(() => window.Model.Results.Count > 0 && !window.Model.IsBusy);
            Named<ListBox>(window, "HistoryResults").SelectedIndex = 0;
            await WaitUntil(() => window.Model.Transcript.Count > 0 && !window.Model.IsContextBusy);
            var transcript = Named<TextBox>(window, "HistoryTranscript");
            Assert.True(transcript.IsReadOnly);
            Assert.Contains("quiet pier", transcript.Text);
            Assert.NotEmpty(Named<TextBlock>(window, "HistoryLocalNotice").Text!);
            Layout(window);
            Assert.Empty(ContrastProbe.Scan(window));
            Assert.True(transcript.Bounds.Width > 250);
            Assert.True(transcript.Bounds.Height > 100);
            Capture(window, $"history-{theme}-{language}.png");
            Click(window, "HistoryDelete");
            Layout(window);
            Assert.True(Named<Border>(window, "HistoryDeleteConfirm").IsVisible);
            Capture(window, $"history-confirm-{theme}-{language}.png");
            Click(window, "HistoryCancelDelete");
            Assert.False(window.Model.ConfirmDelete);
        }
        finally { window.Close(); UiLanguage.Apply(""); ThemeService.Apply(new ClientSettings()); }
    }

    private static T Named<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
    private static void Click(Window window, string name) => Named<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task WaitUntil(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }
    private static void Layout(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2); }
    private static void Capture(Window window, string file)
    {
        if (Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(directory, file), new PngBitmapEncoderOptions());
    }

    private sealed class MemoryHistory : IHistoryStore
    {
        private readonly Dictionary<string, (HistorySession Session, List<HistoryEntry> Entries)> _items = [];
        public Func<string, IReadOnlyList<HistoryHit>>? OnSearch { get; init; }
        public Func<string, long, IReadOnlyList<HistoryEntry>>? OnEntries { get; init; }
        public string? LastQuery { get; private set; }
        public HistoryFilter? LastFilter { get; private set; }
        public int MaximumLimit { get; private set; }
        public void Append(HistorySession session, IReadOnlyList<HistoryEntry> entries) => _items[session.Id] = (session, entries.ToList());
        public IReadOnlyList<HistorySession> Sessions(HistoryFilter filter, int offset = 0, int limit = 100)
        {
            MaximumLimit = Math.Max(MaximumLimit, limit);
            return _items.Values.Select(i => i.Session).Skip(offset).Take(limit).ToArray();
        }
        public IReadOnlyList<HistoryHit> Search(string query, HistoryFilter filter, int offset = 0, int limit = 100)
        {
            LastQuery = query; LastFilter = filter; MaximumLimit = Math.Max(MaximumLimit, limit);
            return OnSearch?.Invoke(query) ?? _items.Values.SelectMany(i => i.Entries.Select(e => new HistoryHit(i.Session, e))).Skip(offset).Take(limit).ToArray();
        }
        public IReadOnlyList<HistoryEntry> Entries(string sessionId, long fromSequence = 0, int limit = 200)
        {
            MaximumLimit = Math.Max(MaximumLimit, limit);
            return OnEntries?.Invoke(sessionId, fromSequence).Take(limit).ToArray() ?? (_items.TryGetValue(sessionId, out var item) ? item.Entries.Where(e => e.Sequence >= fromSequence).Take(limit).ToArray() : []);
        }
        public void Delete(string sessionId) => _items.Remove(sessionId);
        public void Prune(DateTimeOffset before) => throw new NotSupportedException();
    }
}
