namespace Wandur.Core.Sessions;

public sealed class CommandHistory
{
    private readonly List<string> _entries = [];
    private int _position;
    private string _draft = "";

    /// <summary>Public commands, oldest first. Private input never enters, so completion may read this directly.</summary>
    public IReadOnlyList<string> Entries => _entries;

    public void Add(string command, bool isPrivate = false)
    {
        if (!isPrivate && !string.IsNullOrWhiteSpace(command) && _entries.LastOrDefault() != command)
        {
            _entries.Add(command);
            if (_entries.Count > 200) _entries.RemoveAt(0);
        }
        ResetNavigation();
    }

    public string Previous(string draft)
    {
        if (_entries.Count == 0) return draft;
        if (_position == _entries.Count) _draft = draft;
        _position = Math.Max(0, _position - 1);
        return _entries[_position];
    }

    public string Next()
    {
        _position = Math.Min(_entries.Count, _position + 1);
        return _position == _entries.Count ? _draft : _entries[_position];
    }

    public void ResetNavigation() { _position = _entries.Count; _draft = ""; }
}
