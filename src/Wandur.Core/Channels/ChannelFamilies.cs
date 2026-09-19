using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wandur.Core.Channels;

/// <summary>
/// The shipped channel shapes, one set per codebase family. A world with its own rules on its profile
/// overrides these; a world the directory describes by codebase gets its family; anything else gets the
/// union of the safest shapes. A future channel pack delivers exactly the same thing: an ordered rule set.
/// </summary>
public static class ChannelFamilies
{
    public const string Generic = "generic";
    private static readonly Lazy<IReadOnlyDictionary<string, ChannelRuleSet>> Sets = new(Load);
    /// <summary>The family names, in the order the shipped resource lists them.</summary>
    public static IReadOnlyList<string> Names => [.. Sets.Value.Keys];

    /// <summary>The rules of one family, or the generic set when the name is not one Wandur ships.</summary>
    public static ChannelRuleSet Family(string? name)
        => name is not null && Sets.Value.TryGetValue(name.Trim().ToLowerInvariant(), out var set) ? set : Sets.Value[Generic];

    /// <summary>
    /// The family a directory listing's free-text codebase belongs to. Directories write these by hand
    /// ("SMAUG 1.4a", "ROM 2.4b6", "Custom LPMud"), so the match is a containment test, most specific first.
    /// </summary>
    public static string Match(string? codebase)
    {
        var text = (codebase ?? "").ToLowerInvariant();
        if (text.Length == 0) return Generic;
        if (text.Contains("swr") || text.Contains("star wars reality")) return "swr";
        if (text.Contains("smaug") || text.Contains("aftermath")) return "smaug";
        if (text.Contains("circle") || text.Contains("tba")) return "circle";
        if (text.Contains("rom") || text.Contains("envy") || text.Contains("merc") || text.Contains("godwars")) return "rom";
        if (text.Contains("lp") || text.Contains("ldmud") || text.Contains("dgd") || text.Contains("mudos")) return "lp";
        if (text.Contains("diku")) return "diku";
        return Generic;
    }

    /// <summary>The rule set for a world: its own rules when it has any, else its family, else generic.</summary>
    public static ChannelRuleSet For(IReadOnlyList<ChannelRule>? worldRules, string? codebase)
        => worldRules is { Count: > 0 } ? new ChannelRuleSet(worldRules) : Family(Match(codebase));

    private static IReadOnlyDictionary<string, ChannelRuleSet> Load()
    {
        using var stream = typeof(ChannelFamilies).Assembly.GetManifestResourceStream("Wandur.Core.Channels.families.json")
            ?? throw new InvalidOperationException("The channel families resource is missing.");
        var raw = JsonSerializer.Deserialize<Dictionary<string, List<FamilyEntry>>>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        var sets = new Dictionary<string, ChannelRuleSet>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, entries) in raw) sets[name] = new ChannelRuleSet(Expand(raw, entries, 0));
        if (!sets.ContainsKey(Generic)) sets[Generic] = new ChannelRuleSet([]);
        return sets;
    }

    /// <summary>A family may open with the rules of the family it derives from, keeping the shipped file short.</summary>
    private static IEnumerable<ChannelRule> Expand(Dictionary<string, List<FamilyEntry>> raw, List<FamilyEntry> entries, int depth)
    {
        foreach (var entry in entries)
        {
            if (entry.Include is { Length: > 0 } include)
            {
                if (depth < 4 && raw.TryGetValue(include, out var target))
                    foreach (var rule in Expand(raw, target, depth + 1)) yield return rule;
                continue;
            }
            if (entry.Channel is { Length: > 0 } channel && entry.Pattern is { Length: > 0 } pattern)
                yield return new(channel, pattern, entry.ReplyCommand, entry.IsPrivate);
        }
    }

    private sealed record FamilyEntry(string? Include, string? Channel, string? Pattern, string? ReplyCommand, bool IsPrivate);
}
