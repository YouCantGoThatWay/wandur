using System.Text;
using Wandur.Core.Terminal;

namespace Wandur.Core.Input;

/// <summary>
/// Feeds a session's <see cref="CompletionTrie"/> from what the reader sees and sends. Received text is
/// buffered into complete server lines through a two-line <see cref="AnsiTerminal"/>, the same way the
/// channel classifier does it, so a network chunk that ends mid word waits for the rest and escape
/// sequences never become words. The caller applies the privacy gate: nothing private reaches this.
/// </summary>
public sealed class CompletionLearner
{
    public const int MinimumWordLength = 3;
    public const int MaximumWordLength = 32;

    private readonly AnsiTerminal _terminal = new(2);
    private readonly StringBuilder _pending = new();
    private TerminalLine? _lastCompleted;
    private long _sequence;

    public CompletionLearner(CompletionTrie? words = null) => Words = words ?? new CompletionTrie();

    public CompletionTrie Words { get; }

    /// <summary>Lines learned so far; also the recency stamp the next line gets.</summary>
    public long Sequence => _sequence;

    /// <summary>
    /// Public received text, escape sequences and all. Complete lines are learned; a line for which
    /// <paramref name="skip"/> answers true (one that carries a remembered secret) is not.
    /// </summary>
    public void Observe(string text, Func<string, bool>? skip = null)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            _pending.Append(text, start, i + 1 - start);
            start = i + 1;
            if (Take() is { } line && skip?.Invoke(line) != true) Learn(line);
        }
        if (start < text.Length) _pending.Append(text, start, text.Length - start);
        // A line nobody ever terminates must not grow without bound.
        if (_pending.Length > 16_384) _pending.Clear();
    }

    /// <summary>Feeds the buffered raw line through the parser so escape sequences and carriage returns are resolved.</summary>
    private string? Take()
    {
        var raw = _pending.ToString();
        _pending.Clear();
        _terminal.Append(raw);
        // An LF inside an unfinished escape sequence is payload, not the end of a displayed line.
        var completed = _terminal.Lines.Count > 1 ? _terminal.Lines[^2] : null;
        if (completed is null || ReferenceEquals(completed, _lastCompleted)) return null;
        _lastCompleted = completed;
        return completed.Text;
    }

    /// <summary>
    /// One plain line, received or sent by the reader. Its words share one recency stamp, so among the words
    /// of a line the one seen most often ranks first.
    /// </summary>
    public void Learn(string line)
    {
        var sequence = ++_sequence;
        foreach (var token in Tokens(line)) Words.Insert(token, sequence);
    }

    /// <summary>
    /// Runs of letters, digits, apostrophes and hyphens, with the punctuation trimmed from both ends, three to
    /// thirty-two characters long and not made of digits alone.
    /// </summary>
    public static IEnumerable<string> Tokens(string line)
    {
        if (string.IsNullOrEmpty(line)) yield break;
        var start = -1;
        for (var i = 0; i <= line.Length; i++)
        {
            var inside = i < line.Length && IsWordCharacter(line[i]);
            if (inside) { if (start < 0) start = i; continue; }
            if (start < 0) continue;
            var token = Trim(line.AsSpan(start, i - start));
            start = -1;
            if (token.Length is < MinimumWordLength or > MaximumWordLength) continue;
            var digitsOnly = true;
            foreach (var character in token) if (!char.IsDigit(character)) { digitsOnly = false; break; }
            if (!digitsOnly) yield return token.ToString();
        }
    }

    private static bool IsWordCharacter(char character) => char.IsLetterOrDigit(character) || character is '\'' or '-' or '’';

    private static ReadOnlySpan<char> Trim(ReadOnlySpan<char> token)
    {
        var first = 0;
        var last = token.Length;
        while (first < last && !char.IsLetterOrDigit(token[first])) first++;
        while (last > first && !char.IsLetterOrDigit(token[last - 1])) last--;
        return token[first..last];
    }
}
