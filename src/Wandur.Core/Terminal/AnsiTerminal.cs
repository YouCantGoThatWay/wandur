using System.Text;

namespace Wandur.Core.Terminal;

public sealed record TextStyle(string? Foreground = null, string? Background = null, bool Bold = false, bool Italic = false, bool Underline = false)
{
    public int? ForegroundIndex { get; init; }
    public int? BackgroundIndex { get; init; }
}
public sealed record TextRun(string Text, TextStyle Style);
public sealed record TerminalLine(IReadOnlyList<TextRun> Runs)
{
    public string Text => string.Concat(Runs.Select(r => r.Text));
}

public sealed class AnsiTerminal
{
    private enum ParseState { Text, Escape, Csi, Osc, OscEscape, DiscardCsi }
    private readonly record struct Cell(char Character, TextStyle Style);
    private readonly List<TerminalLine> _completed = [];
    private readonly List<Cell> _current = [];
    private readonly int _maxLines;
    private readonly StringBuilder _sequence = new();
    private TextStyle _style = new();
    private int? _basicForegroundIndex;
    private ParseState _state;
    private int _cursor;
    private int _completedCharacters;
    private IReadOnlyList<TerminalLine>? _snapshot;

    public AnsiTerminal(int maxLines = 2000) => _maxLines = Math.Clamp(maxLines, 2, 10_000);
    public IReadOnlyList<TerminalLine> Lines => _snapshot ??= [.. _completed, CurrentLine()];
    public event Action<string, bool>? OutputAppended;
    public event Action? Cleared;
    public string PlainText => string.Join('\n', Lines.Select(l => l.Text));

    public void AppendLocalText(string text)
    {
        // Local text must not consume a pending server escape sequence or execute controls.
        _snapshot = null;
        var serverStyle = _style;
        _style = new(Foreground: "#808080") { ForegroundIndex = 8 };
        foreach (var ch in text)
        {
            if (ch == '\n') NewLine();
            else if (!char.IsControl(ch)) Put(ch);
        }
        _style = serverStyle;
        OutputAppended?.Invoke(text, true);
    }

    public void Clear()
    {
        ClearState();
        Cleared?.Invoke();
    }

    private void ClearState()
    {
        _completed.Clear(); _current.Clear(); _sequence.Clear(); _cursor = 0;
        _completedCharacters = 0; _style = new(); _basicForegroundIndex = null; _state = ParseState.Text; _snapshot = null;
    }

    public void Append(string text)
    {
        _snapshot = null;
        foreach (char ch in text)
        {
            switch (_state)
            {
                case ParseState.Text:
                    if (ch == '\u001b') _state = ParseState.Escape;
                    else if (ch == '\n') NewLine();
                    else if (ch == '\r') _cursor = 0;
                    else if (ch == '\b') _cursor = Math.Max(0, _cursor - 1);
                    else if (ch == '\t') { int count = 4 - _cursor % 4; for (int n = 0; n < count; n++) Put(' '); }
                    else if (!char.IsControl(ch)) Put(ch);
                    break;
                case ParseState.Escape:
                    if (ch == '[') { _sequence.Clear(); _state = ParseState.Csi; }
                    else if (ch is ']' or 'P' or '^' or '_') _state = ParseState.Osc;
                    else _state = ParseState.Text;
                    break;
                case ParseState.Csi:
                    if (ch is >= '@' and <= '~') { ApplyCsi(ch); _state = ParseState.Text; }
                    else if (_sequence.Length < 128) _sequence.Append(ch);
                    else _state = ParseState.DiscardCsi;
                    break;
                case ParseState.DiscardCsi:
                    if (ch is >= '@' and <= '~') _state = ParseState.Text;
                    break;
                case ParseState.Osc:
                    if (ch == '\u0007') _state = ParseState.Text;
                    else if (ch == '\u001b') _state = ParseState.OscEscape;
                    break;
                case ParseState.OscEscape:
                    _state = ch == '\\' ? ParseState.Text : ParseState.Osc;
                    break;
            }
        }
        OutputAppended?.Invoke(text, false);
    }

    private void Put(char ch)
    {
        if (_cursor >= 4096) return;
        var cell = new Cell(ch, _style);
        if (_cursor < _current.Count) _current[_cursor] = cell;
        else _current.Add(cell);
        _cursor++;
    }

    private void NewLine()
    {
        var line = CurrentLine();
        _completed.Add(line);
        _completedCharacters += _current.Count;
        _current.Clear(); _cursor = 0;
        while (_completed.Count >= _maxLines || _completedCharacters > 200_000)
        {
            _completedCharacters -= _completed[0].Text.Length;
            _completed.RemoveAt(0);
        }
    }

    private TerminalLine CurrentLine()
    {
        var runs = new List<TextRun>();
        var text = new StringBuilder();
        TextStyle? style = null;
        foreach (var cell in _current)
        {
            if (style is not null && style != cell.Style)
            {
                runs.Add(new(text.ToString(), style)); text.Clear();
            }
            style = cell.Style; text.Append(cell.Character);
        }
        if (style is not null) runs.Add(new(text.ToString(), style));
        return new(runs);
    }

    private void ApplyCsi(char command)
    {
        var values = _sequence.ToString().Split(';').Select(x => int.TryParse(x, out var n) ? n : 0).ToArray();
        if (command == 'K')
        {
            if (values[0] == 0 && _cursor < _current.Count) _current.RemoveRange(_cursor, _current.Count - _cursor);
            else if (values[0] == 2) { _current.Clear(); _cursor = 0; }
        }
        else if (command == 'J' && values[0] is 2 or 3) ClearState();
        else if (command == 'm')
        {
            for (var i = 0; i < values.Length; i++)
            {
                var n = values[i];
                if (n is >= 30 and <= 37) _basicForegroundIndex = n - 30;
                else if (n is 0 or 39 or >= 90 and <= 97) _basicForegroundIndex = null;
                _style = n switch
                {
                    0 => new(), 1 => _style with { Bold = true }, 22 => _style with { Bold = false },
                    3 => _style with { Italic = true }, 23 => _style with { Italic = false },
                    4 => _style with { Underline = true }, 24 => _style with { Underline = false },
                    39 => _style with { Foreground = null, ForegroundIndex = null }, 49 => _style with { Background = null, BackgroundIndex = null },
                    >= 30 and <= 37 => _style with { Foreground = IndexedColor(n - 30), ForegroundIndex = n - 30 },
                    >= 90 and <= 97 => _style with { Foreground = IndexedColor(n - 90 + 8), ForegroundIndex = n - 90 + 8 },
                    >= 40 and <= 47 => _style with { Background = IndexedColor(n - 40), BackgroundIndex = n - 40 },
                    >= 100 and <= 107 => _style with { Background = IndexedColor(n - 100 + 8), BackgroundIndex = n - 100 + 8 },
                    _ => _style
                };
                if (n is not (38 or 48) || i + 1 >= values.Length) continue;
                string? color = null;
                int? paletteIndex = null;
                if (values[i + 1] == 5 && i + 2 < values.Length)
                { var index = Math.Clamp(values[i + 2], 0, 255); color = IndexedColor(index); paletteIndex = index < 16 ? index : null; i += 2; }
                else if (values[i + 1] == 2 && i + 4 < values.Length)
                { color = $"#{Math.Clamp(values[i + 2], 0, 255):X2}{Math.Clamp(values[i + 3], 0, 255):X2}{Math.Clamp(values[i + 4], 0, 255):X2}"; i += 4; }
                if (color is not null)
                {
                    if (n == 38) { _basicForegroundIndex = null; _style = _style with { Foreground = color, ForegroundIndex = paletteIndex }; }
                    else _style = _style with { Background = color, BackgroundIndex = paletteIndex };
                }
            }
            // Legacy MUDs use SGR 1 + colors 30–37 for the bright palette. Resolve
            // after the whole sequence so 1;30, 30;1 and separate sequences agree.
            if (_basicForegroundIndex is { } basic)
                _style = _style with { Foreground = IndexedColor(basic + (_style.Bold ? 8 : 0)), ForegroundIndex = basic + (_style.Bold ? 8 : 0) };
        }
    }

    private static string IndexedColor(int n) => AnsiPalette.Indexed(n);
}
