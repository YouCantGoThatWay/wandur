using System.Text;

namespace Wandur.Desktop.Terminal;

/// <summary>Keeps incomplete server controls out of the emulator while local echo is inserted.</summary>
internal sealed class TerminalStreamFramer
{
    private enum State { Text, Escape, Csi, String, StringEscape, Intermediate, Discard, DiscardString }
    private State _state;
    private readonly StringBuilder _pending = new();
    public void Reset() { _state = State.Text; _pending.Clear(); }
    public string Feed(string input)
    {
        var output = new StringBuilder(input.Length);
        foreach (var ch in input)
        {
            if (_state == State.Text)
            {
                if (ch == '\x1b') { _pending.Append(ch); _state = State.Escape; }
                else if (ch == '\x9b') { _pending.Append("\x1b["); _state = State.Csi; }
                else output.Append(ch);
                continue;
            }
            if (ch is '\x18' or '\x1a') { Reset(); continue; }
            if (_state == State.Discard) { if (ch is >= '@' and <= '~') Reset(); continue; }
            if (_state == State.DiscardString)
            {
                if (ch == '\a' || (_pending.Length > 0 && _pending[0] == '\x1b' && ch == '\\')) Reset();
                else { _pending.Clear(); if (ch == '\x1b') _pending.Append(ch); }
                continue;
            }
            _pending.Append(ch);
            bool complete = false;
            switch (_state)
            {
                case State.Escape:
                    if (ch == '[') _state = State.Csi;
                    else if (ch is ']' or 'P' or '^' or '_' or 'X') _state = State.String;
                    else if (ch is >= ' ' and <= '/') _state = State.Intermediate;
                    else complete = true;
                    break;
                case State.Csi: complete = ch is >= '@' and <= '~'; break;
                case State.Intermediate: complete = ch is >= '0' and <= '~'; break;
                case State.String: if (ch == '\a') complete = true; else if (ch == '\x1b') _state = State.StringEscape; break;
                case State.StringEscape: if (ch == '\\') complete = true; else _state = State.String; break;
            }
            if (complete) { output.Append(_pending); Reset(); }
            else if (_pending.Length > 8192) { _pending.Clear(); _state = _state is State.String or State.StringEscape ? State.DiscardString : State.Discard; }
        }
        return output.ToString();
    }
}
