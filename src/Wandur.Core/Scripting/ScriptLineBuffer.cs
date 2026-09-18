using Wandur.Core.Terminal;

namespace Wandur.Core.Scripting;

/// <summary>Completed server lines only; network chunks and ANSI fragments are not events.</summary>
public sealed class ScriptLineBuffer
{
    private readonly AnsiTerminal _terminal = new(2);
    private TerminalLine? _lastCompleted;
    public IReadOnlyList<string> Feed(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            _terminal.Append(text[start..(i + 1)]);
            // LF inside OSC/DCS/unfinished CSI is payload, not a completed text line.
            var completed = _terminal.Lines.Count > 1 ? _terminal.Lines[^2] : null;
            if (completed is not null && !ReferenceEquals(completed, _lastCompleted))
            {
                lines.Add(completed.Text);
                _lastCompleted = completed;
            }
            start = i + 1;
            if (lines.Count > 128) { Clear(); return lines; }
        }
        if (start < text.Length) _terminal.Append(text[start..]);
        return lines;
    }
    public void Clear() { _terminal.Clear(); _lastCompleted = null; }
}
