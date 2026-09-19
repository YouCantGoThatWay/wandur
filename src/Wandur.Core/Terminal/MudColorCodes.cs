using System.Text;

namespace Wandur.Core.Terminal;

/// <summary>
/// Turns the SMAUG and SWR style color codes that MSDP strings still carry into styled runs, so a script
/// panel shows "A Vicious Womprat" in yellow instead of "&amp;228A Vicious Womprat&amp;D" literally.
/// <c>&amp;</c> introduces a foreground and <c>^</c> a background: one SMAUG letter, or exactly three digits
/// naming an xterm 256 color (the Legends of the Jedi extension). <c>&amp;&amp;</c> and <c>^^</c> are the literal
/// characters; anything else after a marker stays as text. ANSI SGR sequences are applied the way the
/// transcript applies them and every other escape sequence is dropped. Every string starts from the
/// default style, so a code never leaks from one widget into the next.
/// </summary>
public static class MudColorCodes
{
    private const char Escape = '\u001b';
    private static readonly char[] Markers = ['&', '^', Escape];

    /// <summary>True when the text could carry a code, so callers can keep the plain path for ordinary strings.</summary>
    public static bool HasCodes(string text) => text.IndexOfAny(Markers) >= 0;

    /// <summary>The text without any codes or escape sequences, for dock tab titles and automation names.</summary>
    public static string Strip(string text) => HasCodes(text) ? string.Concat(Parse(text).Select(run => run.Text)) : text;

    /// <summary>The styled runs of one string. Plain text is one run with the default style; an empty string has none.</summary>
    public static IReadOnlyList<TextRun> Parse(string text)
    {
        if (text.Length == 0) return [];
        if (!HasCodes(text)) return [new TextRun(text, new())];
        var parser = new Parser();
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '&' || ch == '^')
            {
                if (i + 1 < text.Length && text[i + 1] == ch) { parser.Put(ch); i++; continue; }
                if (i + 1 < text.Length && Letter(text[i + 1]) is { } index)
                { parser.Set(ch == '&', index); i++; continue; }
                if (ch == '&' && i + 1 < text.Length && text[i + 1] is 'D' or 'd') { parser.Reset(); i++; continue; }
                if (i + 3 < text.Length && char.IsAsciiDigit(text[i + 1]) && char.IsAsciiDigit(text[i + 2]) && char.IsAsciiDigit(text[i + 3]))
                {
                    var number = (text[i + 1] - '0') * 100 + (text[i + 2] - '0') * 10 + (text[i + 3] - '0');
                    if (number <= 255) { parser.Set(ch == '&', number); i += 3; continue; }
                }
                parser.Put(ch);
            }
            else if (ch == Escape)
            {
                if (i + 1 >= text.Length || text[i + 1] != '[') continue;
                var end = i + 2;
                while (end < text.Length && (char.IsAsciiDigit(text[end]) || text[end] == ';')) end++;
                if (end >= text.Length) break;
                if (text[end] == 'm') parser.Sgr(text.AsSpan(i + 2, end - i - 2));
                i = end;
            }
            else parser.Put(ch);
        }
        return parser.Finish();
    }

    /// <summary>The SMAUG letter set, mapped onto the sixteen palette entries the transcript uses.</summary>
    private static int? Letter(char c) => c switch
    {
        'x' => 0, 'r' => 1, 'g' => 2, 'O' => 3, 'b' => 4, 'p' => 5, 'c' => 6, 'w' => 7,
        'z' => 8, 'R' => 9, 'G' => 10, 'Y' => 11, 'B' => 12, 'P' => 13, 'C' => 14, 'W' => 15,
        _ => null
    };

    private sealed class Parser
    {
        private readonly List<TextRun> _runs = [];
        private readonly StringBuilder _text = new();
        private TextStyle _style = new();
        private int? _basicForeground;

        public void Put(char ch) => _text.Append(ch);

        public void Set(bool foreground, int index)
        {
            Flush();
            if (foreground) { _basicForeground = null; _style = _style with { Foreground = AnsiPalette.Indexed(index), ForegroundIndex = index < 16 ? index : null }; }
            else _style = _style with { Background = AnsiPalette.Indexed(index), BackgroundIndex = index < 16 ? index : null };
        }

        public void Reset()
        {
            Flush();
            _basicForeground = null;
            _style = new();
        }

        /// <summary>The same SGR rules as the transcript: bold with a basic color selects the bright entry.</summary>
        public void Sgr(ReadOnlySpan<char> parameters)
        {
            Flush();
            var values = new List<int>();
            foreach (var part in parameters.ToString().Split(';')) values.Add(part.Length == 0 ? 0 : int.TryParse(part, out var v) ? v : -1);
            for (var i = 0; i < values.Count; i++)
            {
                var n = values[i];
                if (n is >= 30 and <= 37) _basicForeground = n - 30;
                else if (n is 0 or 39 or >= 90 and <= 97) _basicForeground = null;
                _style = n switch
                {
                    0 => new(), 1 => _style with { Bold = true }, 22 => _style with { Bold = false },
                    3 => _style with { Italic = true }, 23 => _style with { Italic = false },
                    4 => _style with { Underline = true }, 24 => _style with { Underline = false },
                    39 => _style with { Foreground = null, ForegroundIndex = null }, 49 => _style with { Background = null, BackgroundIndex = null },
                    >= 30 and <= 37 => _style with { Foreground = AnsiPalette.Indexed(n - 30), ForegroundIndex = n - 30 },
                    >= 90 and <= 97 => _style with { Foreground = AnsiPalette.Indexed(n - 90 + 8), ForegroundIndex = n - 90 + 8 },
                    >= 40 and <= 47 => _style with { Background = AnsiPalette.Indexed(n - 40), BackgroundIndex = n - 40 },
                    >= 100 and <= 107 => _style with { Background = AnsiPalette.Indexed(n - 100 + 8), BackgroundIndex = n - 100 + 8 },
                    _ => _style
                };
                if (n is not (38 or 48) || i + 1 >= values.Count) continue;
                string? color = null;
                int? index = null;
                if (values[i + 1] == 5 && i + 2 < values.Count)
                { var extended = Math.Clamp(values[i + 2], 0, 255); color = AnsiPalette.Indexed(extended); index = extended < 16 ? extended : null; i += 2; }
                else if (values[i + 1] == 2 && i + 4 < values.Count)
                { color = $"#{Math.Clamp(values[i + 2], 0, 255):X2}{Math.Clamp(values[i + 3], 0, 255):X2}{Math.Clamp(values[i + 4], 0, 255):X2}"; i += 4; }
                if (color is null) continue;
                if (n == 38) { _basicForeground = null; _style = _style with { Foreground = color, ForegroundIndex = index }; }
                else _style = _style with { Background = color, BackgroundIndex = index };
            }
            if (_basicForeground is { } basic)
                _style = _style with { Foreground = AnsiPalette.Indexed(basic + (_style.Bold ? 8 : 0)), ForegroundIndex = basic + (_style.Bold ? 8 : 0) };
        }

        private void Flush()
        {
            if (_text.Length == 0) return;
            _runs.Add(new(_text.ToString(), _style));
            _text.Clear();
        }

        public IReadOnlyList<TextRun> Finish()
        {
            Flush();
            return _runs;
        }
    }
}
