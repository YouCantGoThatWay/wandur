namespace Wandur.Core.Input;

/// <summary>
/// A case-insensitive prefix trie over the words a session has seen. Keys are lowercased UTF-16 characters;
/// each terminal node keeps the casing the word was last seen with, how often it was seen and a recency
/// stamp supplied by the caller. Bounded in entries and nodes: when a bound is crossed the oldest entries
/// are swept out in one pass, so a single insert never pays for eviction. Pure and thread-agnostic; the
/// caller serializes access.
/// </summary>
public sealed class CompletionTrie
{
    public const int MinimumPrefixLength = 2;
    public const int MaximumWordLength = 64;

    private sealed class Node
    {
        public Dictionary<char, Node>? Children;
        public Entry? Entry;
    }

    private sealed class Entry(string text, long sequence)
    {
        public string Text = text;
        public int Count = 1;
        public long Sequence = sequence;
    }

    private readonly int _maxEntries;
    private readonly int _maxNodes;
    private Node _root = new();
    private int _nodes = 1;

    public CompletionTrie(int maxEntries = 20_000, int maxNodes = 400_000)
    {
        _maxEntries = Math.Max(1, maxEntries);
        _maxNodes = Math.Max(2, maxNodes);
    }

    /// <summary>Distinct words held.</summary>
    public int Count { get; private set; }

    /// <summary>Nodes allocated, the root included.</summary>
    public int NodeCount => _nodes;

    /// <summary>
    /// Records one sighting of <paramref name="word"/>. Words seen together may share a sequence, in which
    /// case frequency decides between them; a later sequence always wins over an earlier one.
    /// </summary>
    public void Insert(string word, long sequence)
    {
        if (string.IsNullOrEmpty(word) || word.Length > MaximumWordLength) return;
        var node = _root;
        foreach (var character in word)
        {
            var key = char.ToLowerInvariant(character);
            node.Children ??= new Dictionary<char, Node>();
            if (!node.Children.TryGetValue(key, out var next))
            {
                next = new Node();
                node.Children[key] = next;
                _nodes++;
            }
            node = next;
        }
        if (node.Entry is { } entry)
        {
            entry.Count++;
            if (sequence >= entry.Sequence) { entry.Sequence = sequence; entry.Text = word; }
        }
        else
        {
            node.Entry = new Entry(word, sequence);
            Count++;
        }
        if (Count > _maxEntries || _nodes > _maxNodes) Sweep();
    }

    /// <summary>
    /// Words that extend <paramref name="prefix"/>, most recent first, then most frequent, then shortest.
    /// The prefix itself is never a candidate, and a prefix shorter than two characters yields nothing.
    /// </summary>
    public IReadOnlyList<string> Suggest(string? prefix, int limit = 8)
    {
        if (prefix is null || prefix.Length < MinimumPrefixLength || prefix.Length > MaximumWordLength || limit <= 0) return [];
        var node = _root;
        foreach (var character in prefix)
        {
            if (node.Children is null || !node.Children.TryGetValue(char.ToLowerInvariant(character), out var next)) return [];
            node = next;
        }
        var found = new List<Entry>();
        var stack = new Stack<Node>();
        if (node.Children is not null) foreach (var child in node.Children.Values) stack.Push(child);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current.Entry is { } entry) found.Add(entry);
            if (current.Children is not null) foreach (var child in current.Children.Values) stack.Push(child);
        }
        return found
            .OrderByDescending(entry => entry.Sequence)
            .ThenByDescending(entry => entry.Count)
            .ThenBy(entry => entry.Text.Length)
            .ThenBy(entry => entry.Text, StringComparer.Ordinal)
            .Take(limit)
            .Select(entry => entry.Text)
            .ToList();
    }

    /// <summary>Every word held, in no particular order. For tests and diagnostics.</summary>
    public IReadOnlyList<string> Words()
    {
        var words = new List<string>(Count);
        foreach (var entry in Entries()) words.Add(entry.Text);
        return words;
    }

    private IEnumerable<Entry> Entries()
    {
        var stack = new Stack<Node>();
        stack.Push(_root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current.Entry is { } entry) yield return entry;
            if (current.Children is not null) foreach (var child in current.Children.Values) stack.Push(child);
        }
    }

    /// <summary>
    /// Rebuilds the trie from the newest nine tenths of its entries, most recent first, stopping early if
    /// the node budget runs out. Runs once per crossing of a bound rather than on every insert.
    /// </summary>
    private void Sweep()
    {
        var keep = Entries()
            .OrderByDescending(entry => entry.Sequence)
            .ThenByDescending(entry => entry.Count)
            .Take(_maxEntries * 9 / 10)
            .ToList();
        _root = new Node();
        _nodes = 1;
        Count = 0;
        var nodeBudget = _maxNodes * 9 / 10;
        foreach (var entry in keep)
        {
            if (_nodes >= nodeBudget) break;
            var node = _root;
            foreach (var character in entry.Text)
            {
                var key = char.ToLowerInvariant(character);
                node.Children ??= new Dictionary<char, Node>();
                if (!node.Children.TryGetValue(key, out var next))
                {
                    next = new Node();
                    node.Children[key] = next;
                    _nodes++;
                }
                node = next;
            }
            node.Entry = entry;
            Count++;
        }
    }
}
