using System.Threading.Channels;
using Wandur.Core.Diagnostics;
using Wandur.Core.Terminal;

namespace Wandur.Core.History;

/// <summary>
/// Single-producer capture of readable lines. Only sanitized, bounded batches
/// cross to the database worker. Callers serialize capture on their session thread.
/// </summary>
public sealed class HistoryRecorder
{
    private sealed record Batch(HistorySession Session, HistoryEntry[] Entries, TaskCompletionSource? Completion = null);
    private readonly IHistoryStore _store;
    private readonly Channel<Batch> _queue = Channel.CreateBounded<Batch>(new BoundedChannelOptions(32)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly AnsiTerminal _parser = new(2);
    private readonly List<HistoryEntry> _pending = [];
    private readonly Task _worker;
    private HistorySession _session;
    private IReadOnlyList<string> _secrets = [];
    private long _sequence;
    private int _pendingCharacters, _failed, _retentionDays;
    private bool _private, _closed;
    private bool _skipReceivedLine, _leadingFragment;
    private string _kind = "received";

    public HistoryRecorder(IHistoryStore store, HistorySession session, int retentionDays)
    {
        _store = store; _session = session; _retentionDays = retentionDays;
        _parser.LineCompleted += line => SaveLine(line.Text);
        _worker = Task.Run(WriteAsync);
    }

    public bool IsFaulted => Volatile.Read(ref _failed) != 0;
    public event Action? Failed;

    public void UpdateCharacter(string name) => _session = _session with { CharacterName = name };
    public void UpdateRetention(int days) => Volatile.Write(ref _retentionDays, days);

    public void Received(string text, bool hidden, IReadOnlyList<string> secrets)
    {
        if (_closed || IsFaulted) return;
        _secrets = secrets;
        if (hidden)
        {
            Hide();
            // Raw private output is never parsed. Resume only after a complete
            // public newline so a split private line cannot leak its suffix.
            if (text.Length > 0) _skipReceivedLine = true;
            return;
        }
        _private = false;
        _kind = "received";
        _parser.Append(text);
    }

    public void Sent(string text, bool hidden, IReadOnlyList<string> secrets) => Local("sent", text, hidden, secrets);
    public void Script(string text, bool hidden, IReadOnlyList<string> secrets) => Local("script", text, hidden, secrets);

    private void Local(string kind, string text, bool hidden, IReadOnlyList<string> secrets)
    {
        if (_closed || IsFaulted) return;
        _secrets = secrets;
        if (hidden) { Hide(); return; }
        FinishPartial();
        _private = false;
        _kind = kind;
        _parser.AppendLocalText(text + "\n");
        _parser.Clear();
        _kind = "received";
    }

    private void Hide()
    {
        // Incomplete public text can be the start of a secret split at the boundary.
        _parser.Clear();
        if (!_private) Add("private", "");
        _private = true;
        _leadingFragment = true;
    }

    private void FinishPartial()
    {
        var text = _parser.Lines[^1].Text;
        if (text.Length > 0) SaveLine(text, partial: true);
        _parser.Clear();
    }

    private void SaveLine(string text, bool partial = false)
    {
        if (_kind == "received" && _skipReceivedLine)
        {
            if (!partial) { _skipReceivedLine = false; _leadingFragment = false; }
            return;
        }
        // At the parser's line limit a remembered secret may itself be truncated,
        // preventing exact redaction. Omit the line rather than persist that prefix.
        if (_parser.CurrentLineTruncated || text.Length >= 4096)
        {
            Add("private", "");
            if (partial && _kind == "received") _skipReceivedLine = true;
        }
        else
        {
            text = ConsoleLog.Mask(text, _secrets);
            if (_kind == "received" && _leadingFragment) text = MaskBoundary(text, leading: true);
            if (partial) text = MaskBoundary(text, leading: false);
            Add(_kind, text);
        }
        if (_kind == "received") _leadingFragment = partial;
    }

    private string MaskBoundary(string text, bool leading)
    {
        var matched = 0;
        foreach (var secret in _secrets)
        {
            for (var length = Math.Min(secret.Length - 1, text.Length); length > matched; length--)
            {
                var fragment = leading ? secret.AsSpan(secret.Length - length) : secret.AsSpan(0, length);
                var edge = leading ? text.AsSpan(0, length) : text.AsSpan(text.Length - length);
                if (!edge.SequenceEqual(fragment)) continue;
                matched = length;
                break;
            }
        }
        if (matched == 0) return text;
        return leading ? "[redacted]" + text[matched..] : text[..^matched] + "[redacted]";
    }

    private void Add(string kind, string text)
    {
        if (IsFaulted) return;
        _pending.Add(new(++_sequence, DateTimeOffset.UtcNow, kind, text));
        _pendingCharacters += text.Length;
        if (_pending.Count >= 32 || _pendingCharacters >= 32_768) Flush();
    }

    public void Flush()
    {
        if (_pending.Count == 0 || _closed) return;
        Enqueue(null);
    }

    public Task FlushAsync()
    {
        if (_closed) return _worker;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(completion);
        return completion.Task;
    }

    private void Enqueue(TaskCompletionSource? completion)
    {
        if (IsFaulted || !_queue.Writer.TryWrite(new(_session, [.. _pending], completion)))
        { Fail(); completion?.TrySetResult(); }
        _pending.Clear(); _pendingCharacters = 0;
    }

    public Task CompleteAsync()
    {
        if (!_closed)
        {
            FinishPartial();
            _session = _session with { EndedAt = DateTimeOffset.UtcNow };
            Enqueue(null);
            _closed = true;
            _queue.Writer.TryComplete();
        }
        return _worker;
    }

    private void Fail()
    {
        if (Interlocked.Exchange(ref _failed, 1) == 0) Failed?.Invoke();
    }

    private async Task WriteAsync()
    {
        var nextPrune = DateTimeOffset.MinValue;
        var lastRetention = -1;
        await foreach (var batch in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                if (IsFaulted) continue;
                var days = Volatile.Read(ref _retentionDays);
                var now = DateTimeOffset.UtcNow;
                if (days > 0 && (now >= nextPrune || days != lastRetention))
                { _store.Prune(now.AddDays(-days)); nextPrune = now.AddHours(1); }
                lastRetention = days;
                _store.Append(batch.Session, batch.Entries);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Data.Common.DbException or InvalidOperationException)
            { Fail(); }
            finally { batch.Completion?.TrySetResult(); }
        }
    }
}
