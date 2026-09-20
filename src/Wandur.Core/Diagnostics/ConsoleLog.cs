using System.Text;
using System.Text.Json;
using Wandur.Core.Protocol;

namespace Wandur.Core.Diagnostics;

public enum ConsoleEntryKind { Received, Sent, Script, Private }

/// <summary>One chunk of the raw text stream: a received packet's text, a sent command, a script echo, or a marker for text hidden while input was private.</summary>
public sealed record ConsoleEntry(long Sequence, DateTimeOffset At, ConsoleEntryKind Kind, string Text)
{
    public const string PrivateMarker = "[private]";
    public string Marker => Kind switch
    {
        ConsoleEntryKind.Received => "<<",
        ConsoleEntryKind.Sent => ">>",
        ConsoleEntryKind.Script => "[script]",
        _ => PrivateMarker
    };

    /// <summary>The entry as one or more display lines: timestamp, direction marker, then the text with controls made visible.</summary>
    public string Render()
    {
        var prefix = $"{At.ToLocalTime():HH:mm:ss.fff} {Marker}";
        return Kind == ConsoleEntryKind.Private ? prefix : prefix + " " + ConsoleLog.Visible(Text, prefix.Length + 1);
    }
}

/// <summary>
/// Bounded, session-local ring of the text stream the transcript receives, in the order it was written, plus the
/// commands sent back. Appending is cheap and thread-safe; nothing is rendered until a view asks for it, and nothing
/// is written to disk. Text received or sent while input was private is replaced by one <c>[private]</c> marker, and
/// remembered diagnostic secrets are masked by the same redactor the protocol diagnostics use.
/// </summary>
public sealed class ConsoleLog
{
    public const int MaximumEntries = 2000;
    public const int MaximumCharacters = 524_288;
    private readonly Queue<ConsoleEntry> _entries = new();
    private readonly object _lock = new();
    private ConsoleEntry? _last;
    private long _sequence;
    private int _characters;

    /// <summary>Raised on the appending thread after every change. Subscribers do the cheapest thing possible when not visible.</summary>
    public event Action? Changed;
    public int Count { get { lock (_lock) return _entries.Count; } }
    public int Characters { get { lock (_lock) return _characters; } }

    public void Append(ConsoleEntryKind kind, string text, bool hidden, IReadOnlyList<string>? secrets = null, DateTimeOffset? at = null)
    {
        if (!hidden && text.Length == 0) return;
        lock (_lock)
        {
            if (hidden)
            {
                // Consecutive hidden chunks collapse into one marker so a password exchange reads as one line.
                if (_last?.Kind == ConsoleEntryKind.Private) return;
                kind = ConsoleEntryKind.Private; text = "";
            }
            else text = Mask(text, secrets);
            var entry = new ConsoleEntry(++_sequence, at ?? DateTimeOffset.UtcNow, kind, text);
            _entries.Enqueue(entry); _last = entry; _characters += text.Length;
            while (_entries.Count > 1 && (_entries.Count > MaximumEntries || _characters > MaximumCharacters))
                _characters -= _entries.Dequeue().Text.Length;
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_lock) { _entries.Clear(); _last = null; _characters = 0; }
        Changed?.Invoke();
    }

    public IReadOnlyList<ConsoleEntry> Snapshot() { lock (_lock) return [.. _entries]; }

    public string Render() => string.Join("\n", Snapshot().Select(entry => entry.Render()));

    /// <summary>
    /// Remembered secrets are replaced with the protocol diagnostics' <c>[redacted]</c> token by the same routine,
    /// applied to the text as a JSON string so the redactor's scrubbing is reused rather than reimplemented.
    /// </summary>
    public static string Mask(string text, IReadOnlyList<string>? secrets)
    {
        if (secrets is null || secrets.Count == 0 || !secrets.Any(secret => secret.Length > 0 && text.Contains(secret, StringComparison.Ordinal))) return text;
        var scrubbed = ProtocolDiagnosticRedactor.Apply(0, "", JsonSerializer.Serialize(text), false, false, false, secrets).Body;
        return JsonSerializer.Deserialize<string>(scrubbed) ?? text;
    }

    /// <summary>
    /// Makes control characters visible: ESC as ␛, CR as ␍, LF as ␊ followed by a real line break, DEL as ^?, and
    /// the other C0 controls as ^X. Continuation lines are indented by <paramref name="indent"/> so they sit under
    /// the entry's text; a chunk that ends with a line feed does not leave an empty continuation line.
    /// </summary>
    public static string Visible(string text, int indent = 0)
    {
        var builder = new StringBuilder(text.Length + 16);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '\u001B': builder.Append('␛'); break;
                case '\r': builder.Append('␍'); break;
                case '\n': builder.Append('␊').Append('\n').Append(' ', indent); break;
                case '\u007F': builder.Append("^?"); break;
                case < ' ': builder.Append('^').Append((char)(ch + 64)); break;
                default: builder.Append(ch); break;
            }
        }
        if (text.EndsWith('\n')) builder.Length -= indent + 1;
        return builder.ToString();
    }
}
