using System.Text;
using Wandur.Core.Terminal;

namespace Wandur.Core.Channels;

/// <summary>What one received line turned out to be. A continuation extends the message before it.</summary>
public readonly record struct ChannelLine(ChannelMessage? Message, bool IsContinuation)
{
    public static readonly ChannelLine None = new(null, false);
}

/// <summary>
/// Recognizes channel traffic in the received text. It is a mirror, not a filter: the caller keeps writing
/// every line to the transcript and only copies what this returns into the Channels panel.
/// </summary>
public sealed class ChannelClassifier(ChannelRuleSet rules)
{
    /// <summary>A world that sends a structured message and then prints it too must not be shown twice.</summary>
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(1);
    private readonly AnsiTerminal _terminal = new(2);
    private readonly StringBuilder _pending = new();
    private ChannelMessage? _last;
    private TerminalLine? _lastCompleted;
    private string? _structuredText;
    private DateTimeOffset _structuredAt;

    /// <summary>The rules in force. A rule taught mid session replaces them without losing the line in flight.</summary>
    public ChannelRuleSet Rules { get; set; } = rules;

    /// <summary>The agent feed and the panel agree on what chat is because both ask this.</summary>
    public bool IsChannelLine(string plainLine) => Rules.IsChannelLine(plainLine);

    /// <summary>Complete server lines only; a network chunk that ends mid line waits for the rest.</summary>
    public IReadOnlyList<ChannelLine> Feed(string text, DateTimeOffset timestamp)
    {
        var results = new List<ChannelLine>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            _pending.Append(text, start, i + 1 - start);
            start = i + 1;
            var line = Take();
            if (line is not null && Classify(line, timestamp) is { Message: not null } classified) results.Add(classified);
            if (results.Count > 128) { Reset(); return results; }
        }
        if (start < text.Length) _pending.Append(text, start, text.Length - start);
        // A line nobody ever terminates must not grow without bound.
        if (_pending.Length > 16_384) _pending.Clear();
        return results;
    }

    /// <summary>Feeds the buffered raw line through the parser so the panel keeps the transcript's colors.</summary>
    private TerminalLine? Take()
    {
        var raw = _pending.ToString();
        _pending.Clear();
        _terminal.Append(raw);
        // An LF inside an unfinished escape sequence is payload, not the end of a displayed line.
        var completed = _terminal.Lines.Count > 1 ? _terminal.Lines[^2] : null;
        if (completed is null || ReferenceEquals(completed, _lastCompleted)) return null;
        _lastCompleted = completed;
        return completed;
    }

    /// <summary>Classifies one complete line, joining it to the message above when it is a wrapped tail.</summary>
    public ChannelLine Classify(TerminalLine line, DateTimeOffset timestamp)
    {
        var plain = ChannelRuleSet.Strip(line.Text);
        if (_structuredText is { } duplicate && timestamp - _structuredAt <= DuplicateWindow)
        {
            _structuredText = null;
            if (string.Equals(duplicate, plain, StringComparison.Ordinal)) return ChannelLine.None;
        }
        if (Rules.Match(plain, timestamp) is { } message)
        {
            _last = message with { Runs = Body(line.Runs, plain, message.Text) };
            return new(_last, false);
        }
        // A wrapped channel line is indented; anything flush with the margin has left the conversation.
        if (_last is not null && plain.Length > 0 && char.IsWhiteSpace(plain[0]))
        {
            var tail = plain.Trim();
            _last = _last with { Text = _last.Text + " " + tail, RawLine = _last.RawLine + "\n" + plain,
                Runs = [.. _last.Runs, new TextRun(" ", new TextStyle()), .. Body(line.Runs, plain, tail)] };
            return new(_last, true);
        }
        _last = null;
        return ChannelLine.None;
    }

    /// <summary>
    /// The colors the world chose for the words themselves. The panel shows its own timestamp and speaker,
    /// so the runs it renders are the message body cut out of the line the transcript already holds.
    /// </summary>
    public static IReadOnlyList<TextRun> Body(IReadOnlyList<TextRun> runs, string plain, string text)
    {
        var start = text.Length == 0 ? -1 : plain.LastIndexOf(text, StringComparison.Ordinal);
        if (start < 0) return runs;
        var slice = new List<TextRun>();
        var end = start + text.Length;
        var position = 0;
        foreach (var run in runs)
        {
            var from = Math.Max(start, position);
            var to = Math.Min(end, position + run.Text.Length);
            if (to > from)
            {
                slice.Add(new(run.Text[(from - position)..(to - position)], run.Style));
            }
            position += run.Text.Length;
            if (position >= end) break;
        }
        return slice.Count > 0 ? slice : runs;
    }

    /// <summary>
    /// A structured message the world also prints. The printed copy arrives within a moment and carries the
    /// same text, so it is dropped rather than mirrored twice.
    /// </summary>
    public void ExpectPrintedCopy(string plainText, DateTimeOffset timestamp)
    { _structuredText = ChannelRuleSet.Strip(plainText); _structuredAt = timestamp; _last = null; }

    /// <summary>A privacy interval or a cleared transcript ends whatever line was in flight.</summary>
    public void Reset() { _pending.Clear(); _terminal.Clear(); _last = null; _lastCompleted = null; _structuredText = null; }
}
