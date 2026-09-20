namespace Wandur.Core.Mapping;

/// <summary>
/// Matches rooms by what has actually been observed of them, never by a manual label alone. Every term
/// must match (AND); a quoted phrase matches as one contiguous term. Matching is case-insensitive and a
/// room with no observed name or description can never match.
/// </summary>
public static class RoomSearch
{
    public static IReadOnlyList<MapRoom> Search(IReadOnlyList<MapRoom> rooms, string query)
    {
        var terms = ParseTerms(query);
        return terms.Count == 0 ? [] : rooms.Where(r => Matches(r, terms)).ToArray();
    }

    public static bool Matches(MapRoom room, string query) => Matches(room, ParseTerms(query));

    /// <summary>
    /// A room not yet manually edited was named and described straight from the server or text evidence
    /// (<see cref="RoomMapTracker"/>), so its plain <see cref="MapRoom.Name"/>/<see cref="MapRoom.Description"/>
    /// are what was observed. Editing a room moves that evidence to <see cref="MapRoom.ObservedName"/>/
    /// <see cref="MapRoom.ObservedDescription"/> so the user's own label can differ from it; a manually added
    /// room the server has never confirmed carries no observed text at all.
    /// </summary>
    public static (string? Name, string? Description) EffectiveObservedText(MapRoom room) =>
        room.IsManuallyEdited ? (room.ObservedName, room.ObservedDescription) : (room.Name, room.Description);

    private static bool Matches(MapRoom room, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0) return false;
        var (name, description) = EffectiveObservedText(room);
        if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(description)) return false;
        var text = (name ?? "") + "\n" + (description ?? "");
        return terms.All(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Splits on whitespace; a double-quoted run becomes one phrase term, quotes stripped.</summary>
    public static IReadOnlyList<string> ParseTerms(string query)
    {
        var terms = new List<string>();
        var i = 0;
        while (i < query.Length)
        {
            if (char.IsWhiteSpace(query[i])) { i++; continue; }
            if (query[i] == '"')
            {
                var end = query.IndexOf('"', i + 1);
                var content = end < 0 ? query[(i + 1)..] : query[(i + 1)..end];
                content = content.Trim();
                if (content.Length > 0) terms.Add(content);
                i = end < 0 ? query.Length : end + 1;
            }
            else
            {
                var start = i;
                while (i < query.Length && !char.IsWhiteSpace(query[i]) && query[i] != '"') i++;
                terms.Add(query[start..i]);
            }
        }
        return terms;
    }
}
