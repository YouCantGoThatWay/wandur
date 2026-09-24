using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Wandur.Core.History;
using Wandur.Core.Localization;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.ViewModels;

/// <summary>Read-only, bounded pages over the synchronous history store. The window owns this model, not the store.</summary>
public sealed partial class HistoryViewModel : ObservableObject, IDisposable
{
    public const int PageSize = 50;
    public const int ContextPageSize = 100;
    private readonly IHistoryStore _store;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _browse;
    private CancellationTokenSource? _context;
    private int _browseGeneration;
    private int _contextGeneration;
    private int _sessionOffset;
    private int _resultOffset;
    private HistorySession? _pendingDelete;
    private long? _anchorSequence;
    private bool _disposed;

    public HistoryViewModel(IHistoryStore store)
    {
        _store = store;
        UiLanguage.Changed += LanguageChanged;
    }

    public ObservableCollection<HistorySession> Sessions { get; } = [];
    public ObservableCollection<HistoryHit> Results { get; } = [];
    public ObservableCollection<HistoryEntry> Transcript { get; } = [];
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private string _world = "";
    [ObservableProperty] private string _character = "";
    [ObservableProperty] private DateTimeOffset? _from;
    [ObservableProperty] private DateTimeOffset? _until;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isContextBusy;
    [ObservableProperty] private bool _isDeleting;
    [ObservableProperty] private bool _hasNextSessions;
    [ObservableProperty] private bool _hasNextResults;
    [ObservableProperty] private bool _hasPreviousContext;
    [ObservableProperty] private bool _hasNextContext;
    [ObservableProperty] private bool _confirmDelete;
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private HistorySession? _activeSession;

    public bool HasPreviousSessions => _sessionOffset > 0;
    public bool HasPreviousResults => _resultOffset > 0;
    public string SessionsLabel => L.Format(L.HistoryPage, _sessionOffset / PageSize + 1, Sessions.Count);
    public string ResultsLabel => L.Format(L.HistoryPage, _resultOffset / PageSize + 1, Results.Count);
    public string ContextLabel => ActiveSession is { } session ? SessionLabel(session) : L.HistorySelect;
    public string DeletePrompt => L.Format(L.HistoryDeletePrompt, _pendingDelete is { } session ? SessionLabel(session) : "");
    public string TranscriptText => string.Join("\n\n", Transcript.Select(e => $"{e.At.ToLocalTime():g}  {KindLabel(e.Kind)}\n{e.Text}"));
    public int TranscriptSelectionStart
    {
        get
        {
            var offset = 0;
            foreach (var entry in Transcript)
            {
                var heading = $"{entry.At.ToLocalTime():g}  {KindLabel(entry.Kind)}\n";
                if (entry.Sequence == _anchorSequence) return offset + heading.Length;
                offset += heading.Length + entry.Text.Length + 2;
            }
            return 0;
        }
    }
    public int TranscriptSelectionEnd => TranscriptSelectionStart + (Transcript.FirstOrDefault(e => e.Sequence == _anchorSequence)?.Text.Length ?? 0);
    public bool CanDelete => ActiveSession is not null && !IsDeleting && !IsContextBusy;
    public bool SessionsEmpty => Sessions.Count == 0 && !IsBusy;
    public bool ResultsEmpty => Results.Count == 0 && !IsBusy;

    public static string SessionLabel(HistorySession session) => $"{session.WorldName} · {session.CharacterName} · {session.StartedAt.ToLocalTime():g}";
    public static string KindLabel(string kind) => kind switch
    {
        "sent" => L.HistorySent, "script" => L.HistoryScript, "private" => L.HistoryPrivate, _ => L.HistoryReceived
    };

    partial void OnQueryChanged(string value) => FiltersChanged();
    partial void OnWorldChanged(string value) => FiltersChanged();
    partial void OnCharacterChanged(string value) => FiltersChanged();
    partial void OnFromChanged(DateTimeOffset? value) => FiltersChanged();
    partial void OnUntilChanged(DateTimeOffset? value) => FiltersChanged();
    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(SessionsEmpty)); OnPropertyChanged(nameof(ResultsEmpty)); }
    partial void OnIsContextBusyChanged(bool value) => OnPropertyChanged(nameof(CanDelete));
    partial void OnIsDeletingChanged(bool value) => OnPropertyChanged(nameof(CanDelete));
    partial void OnActiveSessionChanged(HistorySession? value) { OnPropertyChanged(nameof(ContextLabel)); OnPropertyChanged(nameof(CanDelete)); }

    private void FiltersChanged()
    {
        if (_disposed) return;
        CancelBrowse();
        _sessionOffset = _resultOffset = 0;
        Sessions.Clear(); Results.Clear();
        HasNextSessions = HasNextResults = false;
        ClearContext();
        Error = "";
        NotifyPages();
    }

    public Task RefreshAsync() => LoadPagesAsync();
    public Task NextSessionsAsync() => HasNextSessions && !IsBusy ? LoadPagesAsync(_sessionOffset + PageSize, _resultOffset) : Task.CompletedTask;
    public Task PreviousSessionsAsync() => HasPreviousSessions && !IsBusy ? LoadPagesAsync(_sessionOffset - PageSize, _resultOffset) : Task.CompletedTask;
    public Task NextResultsAsync() => HasNextResults && !IsBusy ? LoadPagesAsync(_sessionOffset, _resultOffset + PageSize) : Task.CompletedTask;
    public Task PreviousResultsAsync() => HasPreviousResults && !IsBusy ? LoadPagesAsync(_sessionOffset, _resultOffset - PageSize) : Task.CompletedTask;

    private async Task LoadPagesAsync(int? sessionOffset = null, int? resultOffset = null)
    {
        if (_disposed || IsDeleting) return;
        CancelBrowse();
        if (From is { } from && Until is { } until && from >= until)
        {
            Error = L.HistoryInvalidDates;
            return;
        }
        var generation = _browseGeneration;
        _browse = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _browse.Token;
        var filter = new HistoryFilter(EmptyToNull(World), EmptyToNull(Character), From, Until);
        var query = Query;
        var sessionsAt = sessionOffset ?? _sessionOffset;
        var resultsAt = resultOffset ?? _resultOffset;
        IsBusy = true; Error = "";
        try
        {
            var page = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var sessions = _store.Sessions(filter, sessionsAt, PageSize + 1);
                token.ThrowIfCancellationRequested();
                var hits = string.IsNullOrWhiteSpace(query) ? Array.Empty<HistoryHit>() : _store.Search(query, filter, resultsAt, PageSize + 1);
                return (sessions, hits);
            }, token);
            if (_disposed || generation != _browseGeneration) return;
            _sessionOffset = sessionsAt; _resultOffset = resultsAt;
            Replace(Sessions, page.sessions.Take(PageSize));
            Replace(Results, page.hits.Take(PageSize));
            HasNextSessions = page.sessions.Count > PageSize;
            HasNextResults = page.hits.Count > PageSize;
            NotifyPages();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { if (!_disposed && generation == _browseGeneration) Error = L.HistoryLoadFailed; }
        finally { if (!_disposed && generation == _browseGeneration) IsBusy = false; }
    }

    public Task OpenSessionAsync(HistorySession session) => OpenAsync(session, 0, null);
    public Task OpenHitAsync(HistoryHit hit) => OpenAsync(hit.Session, Math.Max(0, hit.Entry.Sequence - ContextPageSize / 2), hit.Entry.Sequence);
    private Task OpenAsync(HistorySession session, long start, long? anchor)
    {
        if (_disposed || IsDeleting) return Task.CompletedTask;
        ClearContext();
        ActiveSession = session;
        _anchorSequence = anchor;
        return LoadContextAsync(session, start, false);
    }
    public Task NextContextAsync() => ActiveSession is { } session && HasNextContext && !IsContextBusy && Transcript.Count > 0
        ? LoadContextAsync(session, Transcript[^1].Sequence + 1, false) : Task.CompletedTask;
    public Task PreviousContextAsync() => ActiveSession is { } session && HasPreviousContext && !IsContextBusy && Transcript.Count > 0
        ? LoadContextAsync(session, Transcript[0].Sequence, true) : Task.CompletedTask;

    private async Task LoadContextAsync(HistorySession session, long start, bool previous)
    {
        if (_disposed || IsDeleting) return;
        CancelContext();
        var generation = _contextGeneration;
        _context = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _context.Token;
        IsContextBusy = true; Error = "";
        try
        {
            var page = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                if (previous) return ReadPrevious(session.Id, start, token);
                var entries = _store.Entries(session.Id, start, ContextPageSize + 1);
                token.ThrowIfCancellationRequested();
                var first = _store.Entries(session.Id, 0, 1).FirstOrDefault();
                return (Entries: entries.Take(ContextPageSize).ToArray(), Previous: entries.Count > 0 && first is not null && first.Sequence < entries[0].Sequence,
                    Next: entries.Count > ContextPageSize);
            }, token);
            if (_disposed || generation != _contextGeneration) return;
            Replace(Transcript, page.Entries);
            HasPreviousContext = page.Previous; HasNextContext = page.Next;
            NotifyTranscript();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { if (!_disposed && generation == _contextGeneration) Error = L.HistoryLoadFailed; }
        finally { if (!_disposed && generation == _contextGeneration) IsContextBusy = false; }
    }

    private (HistoryEntry[] Entries, bool Previous, bool Next) ReadPrevious(string sessionId, long before, CancellationToken token)
    {
        // The store exposes forward cursors only. Start one page back, widen across gaps,
        // and retain just the last page. Contiguous histories need only two reads.
        long span = ContextPageSize;
        while (true)
        {
            var retained = new Queue<HistoryEntry>();
            var beginning = Math.Max(0, before - span);
            var cursor = beginning;
            var earlier = false;
            while (cursor < before)
            {
                token.ThrowIfCancellationRequested();
                var batch = _store.Entries(sessionId, cursor, ContextPageSize);
                if (batch.Count == 0) break;
                foreach (var entry in batch)
                {
                    if (entry.Sequence >= before) break;
                    retained.Enqueue(entry);
                    if (retained.Count > ContextPageSize) { retained.Dequeue(); earlier = true; }
                }
                if (batch[^1].Sequence >= before || batch[^1].Sequence < cursor || batch[^1].Sequence == long.MaxValue) break;
                cursor = batch[^1].Sequence + 1;
            }
            if (retained.Count == ContextPageSize || beginning == 0)
            {
                token.ThrowIfCancellationRequested();
                var first = _store.Entries(sessionId, 0, 1).FirstOrDefault();
                earlier |= retained.Count > 0 && first is not null && first.Sequence < retained.Peek().Sequence;
                return (retained.ToArray(), earlier, true);
            }
            span = span > long.MaxValue / 2 ? long.MaxValue : span * 2;
        }
    }

    public void RequestDelete()
    {
        if (_disposed || !CanDelete) return;
        _pendingDelete = ActiveSession;
        OnPropertyChanged(nameof(DeletePrompt));
        ConfirmDelete = true;
    }
    public void CancelDelete() { _pendingDelete = null; ConfirmDelete = false; }
    public async Task DeleteAsync()
    {
        if (_disposed || IsDeleting || !ConfirmDelete || _pendingDelete is not { } session) return;
        CancelBrowse(); CancelContext();
        IsDeleting = true; Error = "";
        var token = _lifetime.Token;
        try
        {
            await Task.Run(() => { token.ThrowIfCancellationRequested(); _store.Delete(session.Id); }, token);
            if (_disposed) return;
            ClearContext();
            _sessionOffset = _resultOffset = 0;
            Sessions.Clear(); Results.Clear();
            HasNextSessions = HasNextResults = false;
            NotifyPages();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { if (!_disposed) Error = L.HistoryDeleteFailed; }
        finally { if (!_disposed) IsDeleting = false; }
        if (!_disposed && Error.Length == 0) await RefreshAsync();
    }

    private void ClearContext()
    {
        CancelContext(); CancelDelete();
        _anchorSequence = null;
        ActiveSession = null; Transcript.Clear();
        HasPreviousContext = HasNextContext = false;
        NotifyTranscript();
    }
    private void CancelBrowse() { _browseGeneration++; _browse?.Cancel(); _browse?.Dispose(); _browse = null; IsBusy = false; }
    private void CancelContext() { _contextGeneration++; _context?.Cancel(); _context?.Dispose(); _context = null; IsContextBusy = false; }
    private void NotifyPages()
    {
        OnPropertyChanged(nameof(SessionsLabel)); OnPropertyChanged(nameof(ResultsLabel));
        OnPropertyChanged(nameof(HasPreviousSessions)); OnPropertyChanged(nameof(HasPreviousResults));
        OnPropertyChanged(nameof(SessionsEmpty)); OnPropertyChanged(nameof(ResultsEmpty));
    }
    private void LanguageChanged()
    {
        NotifyPages(); OnPropertyChanged(nameof(ContextLabel)); OnPropertyChanged(nameof(DeletePrompt)); NotifyTranscript();
    }
    private void NotifyTranscript()
    {
        OnPropertyChanged(nameof(TranscriptText));
        OnPropertyChanged(nameof(TranscriptSelectionStart));
        OnPropertyChanged(nameof(TranscriptSelectionEnd));
    }
    private static string? EmptyToNull(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source) { target.Clear(); foreach (var item in source) target.Add(item); }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UiLanguage.Changed -= LanguageChanged;
        _lifetime.Cancel(); CancelBrowse(); CancelContext(); _lifetime.Dispose();
    }
}
