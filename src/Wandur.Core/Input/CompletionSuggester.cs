namespace Wandur.Core.Input;

/// <summary>
/// Decides what the composer may offer after the caret. Line mode first: when the draft is a prefix of an
/// earlier command line, the rest of the most recent such line. Otherwise word mode: the rest of the best
/// word the trie knows for the word being typed. Nothing while private, with an empty draft, or with the
/// caret anywhere but the end.
/// </summary>
public static class CompletionSuggester
{
    /// <summary>The text to append, or null when there is nothing to offer.</summary>
    public static string? Suggest(string? text, int caretIndex, bool isPrivate, IReadOnlyList<string> history, CompletionTrie words)
    {
        if (isPrivate || string.IsNullOrEmpty(text) || caretIndex != text.Length) return null;
        for (var i = history.Count - 1; i >= 0; i--)
        {
            var line = history[i];
            if (line.Length > text.Length && line.StartsWith(text, StringComparison.OrdinalIgnoreCase)) return line[text.Length..];
        }
        var word = text[(text.LastIndexOf(' ') + 1)..];
        if (word.Length < CompletionTrie.MinimumPrefixLength) return null;
        var candidates = words.Suggest(word, 1);
        return candidates.Count == 0 ? null : candidates[0][word.Length..];
    }
}
