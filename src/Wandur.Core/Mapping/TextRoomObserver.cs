using System.Text.RegularExpressions;
using Wandur.Core.Terminal;

namespace Wandur.Core.Mapping;

/// <summary>Conservative English room-block fallback. Never treats arbitrary transcript text as identity.</summary>
public sealed class TextRoomObserver
{
    private readonly AnsiTerminal _terminal = new(256);
    private readonly List<TerminalLine> _block = [];
    private TerminalLine? _lastLine;
    private Dictionary<string, string?>? _exits;
    private static readonly Regex ExitHeader = Pattern(@"^\s*(?:obvious\s+)?exits\s*:\s*(.*)$");
    private static readonly Regex DirectionLine = Pattern(@"^\s*(north(?:east|west)?|south(?:east|west)?|east|west|up|down|in|out)\s*[-–:]\s*.+$");
    private static readonly Regex DirectionToken = Pattern(@"\b(north(?:east|west)?|south(?:east|west)?|east|west|up|down|in|out|ne|nw|se|sw|n|s|e|w|u|d)\b");
    private static readonly Regex Prompt = Pattern(@"^(?:>\s*$|(?:HP|Health)\s*[:=]\s*\d|\*\(\()");
    private static readonly Regex Failure = Pattern(@"(?:you (?:can't|cannot|can not) go (?:that way|there)|(?:door|gate) is (?:closed|locked)|you (?:fail|are unable) to (?:move|go)|alas, you cannot go)");
    private static readonly Regex Occupant = Pattern(@"(?:\b(?:is|are) (?:standing|sitting|resting|sleeping|here)\b|\b(?:stands|sits|lies) here\b|^you see\b|^\[.*\]|\bsays[:,])");
    private static Regex Pattern(string expression) => new(expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50));
    public static bool IsMovementFailure(string text) => text.Length <= 32768 && Failure.IsMatch(text);

    public bool MovementFailed { get; private set; }

    public void Reset()
    {
        MovementFailed = false;
        _terminal.Clear();
        _block.Clear();
        _lastLine = null;
        _exits = null;
    }

    public IReadOnlyList<RoomObservation> Feed(string text)
    {
        MovementFailed = false;
        var result = new List<RoomObservation>();
        _terminal.Append(text);
        var lines = _terminal.Lines;
        var start = 0;
        if (_lastLine is not null)
        {
            for (var i = lines.Count - 2; i >= 0; i--)
                if (ReferenceEquals(lines[i], _lastLine)) { start = i + 1; break; }
        }
        // Leave the incomplete final line untouched, including split exit headings.
        for (var i = start; i < lines.Count - 1; i++)
        {
            Consume(lines[i], result);
            _lastLine = lines[i];
        }
        // LOTJ often finishes the exits with an unterminated HP prompt.
        if (_exits is { Count: > 0 } && Prompt.IsMatch(lines[^1].Text.TrimStart())) Complete(result);
        return result;
    }

    private void Consume(TerminalLine line, List<RoomObservation> result)
    {
        var text = line.Text.Trim();
        if (IsMovementFailure(text))
        {
            MovementFailed = true;
            _block.Clear();
            _exits = null;
            return;
        }
        if (_exits is not null)
        {
            var direction = DirectionLine.Match(text);
            if (direction.Success)
            {
                _exits[RoomMapTracker.NormalizeDirection(direction.Groups[1].Value)!] = null;
                return;
            }
            Complete(result);
        }
        var header = ExitHeader.Match(text);
        if (header.Success)
        {
            _exits = [];
            foreach (Match token in DirectionToken.Matches(header.Groups[1].Value))
                _exits[RoomMapTracker.NormalizeDirection(token.Value)!] = null;
            if (header.Groups[1].Value.Length > 0) Complete(result);
            return;
        }
        if (_block.Count >= 128) _block.RemoveAt(0);
        _block.Add(line);
    }

    private void Complete(List<RoomObservation> result)
    {
        var candidates = _block.Select((line, index) => (line, index))
            .Where(x => IsTitle(x.line.Text)).ToArray();
        // Prefer a styled title; otherwise the first plausible line after the last command/room.
        var styled = candidates.Where(x => x.line.Runs.Any(r => r.Style.Bold)).ToArray();
        var chosen = styled.Length > 0 ? styled[0] : candidates.FirstOrDefault();
        if (chosen.line is not null && _exits is not null)
        {
            var description = string.Join(" ", _block.Skip(chosen.index + 1).Select(l => l.Text.Trim())
                .Where(t => t.Length > 0 && !t.All(c => c is '─' or '-' or '=') && !Occupant.IsMatch(t)));
            if (description.Length > 0 && description.Length <= 16000)
                result.Add(new(null, chosen.line.Text.Trim(), description, new Dictionary<string, string?>(_exits)));
        }
        _block.Clear();
        _exits = null;
    }

    private static bool IsTitle(string value)
    {
        var text = value.Trim();
        return text.Length is > 1 and <= 110 && char.IsLetter(text[0]) && !text.EndsWith('.') && !text.EndsWith(':') && !text.EndsWith('!') &&
            !text.Contains("says", StringComparison.OrdinalIgnoreCase) && !text.Contains("password", StringComparison.OrdinalIgnoreCase) &&
            !text.StartsWith("Exits", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("You ", StringComparison.OrdinalIgnoreCase);
    }
}
