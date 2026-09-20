using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Wandur.Core.Terminal;

namespace Wandur.Core.Channels;

/// <summary>
/// One recognized communication channel shape. <paramref name="Pattern"/> is a .NET regular expression
/// anchored to the start of a plain (ANSI stripped) line; the optional named groups <c>speaker</c> and
/// <c>text</c> carry who spoke and what they said. <paramref name="ReplyCommand"/> is the command prefix
/// that speaks on the channel, where <c>{speaker}</c> is replaced by the person being answered.
/// <paramref name="Exclude"/> turns the rule around: a line it matches is not a channel, whatever else
/// claims it, which is how a false positive is taught away. <paramref name="Disabled"/> keeps a rule on
/// the profile without applying it. The JSON shape is the one a channel pack carries:
/// <c>{ "channel", "pattern", "reply_command", "private", "exclude", "disabled" }</c>, the last three
/// omitted when false.
/// </summary>
public sealed record ChannelRule(
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("pattern")] string Pattern,
    [property: JsonPropertyName("reply_command")] [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReplyCommand = null,
    [property: JsonPropertyName("private")] [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool IsPrivate = false,
    [property: JsonPropertyName("exclude")] [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Exclude = false,
    [property: JsonPropertyName("disabled")] [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Disabled = false);

/// <summary>
/// The rules stored on a world profile. A profile is a record, and two profiles that carry the same rules
/// are the same profile, so this collection compares by its contents rather than by identity.
/// </summary>
public sealed class ChannelRuleList : List<ChannelRule>, IEquatable<ChannelRuleList>
{
    public ChannelRuleList() { }
    public ChannelRuleList(IEnumerable<ChannelRule> rules) : base(rules) { }
    public bool Equals(ChannelRuleList? other) => other is not null && (ReferenceEquals(this, other) || this.SequenceEqual(other));
    public override bool Equals(object? other) => Equals(other as ChannelRuleList);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Count);
        foreach (var rule in this) hash.Add(rule);
        return hash.ToHashCode();
    }
}

/// <summary>A channel line the classifier recognized. <see cref="Runs"/> keeps the transcript's own colors.</summary>
public sealed record ChannelMessage(string Channel, string Speaker, string Text, DateTimeOffset Timestamp, string RawLine, bool IsPrivate)
{
    public IReadOnlyList<TextRun> Runs { get; init; } = [];
    public string? ReplyCommand { get; init; }
}

/// <summary>
/// An ordered set of rules. The first rule that matches wins, so one line is never two messages; an
/// exclusion anywhere in the set wins over every other rule, so a line taught away stays away. A
/// disabled rule is kept in <see cref="Rules"/> but never consulted.
/// </summary>
public sealed class ChannelRuleSet
{
    /// <summary>A pathological pattern must not stall the output pump; an expensive line simply does not match.</summary>
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(50);
    private static readonly Regex AnsiSequence = new("\u001b\\[[0-9;?]*[ -/]*[@-~]|\u001b\\][^\u0007]*\u0007|\u001b.",
        RegexOptions.CultureInvariant, MatchTimeout);
    private readonly List<ChannelRule> _rules = [];
    private readonly List<(ChannelRule Rule, Regex Expression)> _compiled = [];
    private readonly List<(ChannelRule Rule, Regex Expression)> _exclusions = [];

    public ChannelRuleSet(IEnumerable<ChannelRule> rules)
    {
        foreach (var rule in rules)
        {
            if (rule is null || string.IsNullOrWhiteSpace(rule.Channel) || string.IsNullOrEmpty(rule.Pattern)) continue;
            // A world profile may carry a hand written pattern. An invalid one is ignored, never fatal.
            if (Compile(rule.Pattern) is not { } expression) continue;
            _rules.Add(rule);
            if (rule.Disabled) continue;
            (rule.Exclude ? _exclusions : _compiled).Add((rule, expression));
        }
    }

    /// <summary>The expression a rule runs as, or null when the pattern cannot be read.</summary>
    public static Regex? Compile(string pattern)
    {
        try { return new Regex(pattern.StartsWith('^') ? pattern : "^" + pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, MatchTimeout); }
        catch (ArgumentException) { return null; }
    }

    /// <summary>Every readable rule, in order, including the disabled ones.</summary>
    public IReadOnlyList<ChannelRule> Rules => _rules;
    public int Count => _rules.Count;
    /// <summary>The channel names this set knows, first seen first, exclusions left out.</summary>
    public IReadOnlyList<string> Channels =>
        [.. _compiled.Select(entry => entry.Rule.Channel).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The transcript keeps the colors; recognition works on the plain text underneath them.</summary>
    public static string Strip(string line)
    {
        if (line.IndexOf('\u001b') < 0) return line.TrimEnd('\r', '\n');
        try { return AnsiSequence.Replace(line, "").TrimEnd('\r', '\n'); }
        catch (RegexMatchTimeoutException) { return line.TrimEnd('\r', '\n'); }
    }

    /// <summary>The reply command for a channel named by a structured message rather than by a pattern.</summary>
    public ChannelRule? ForChannel(string channel)
        => _compiled.FirstOrDefault(entry => string.Equals(entry.Rule.Channel, channel, StringComparison.OrdinalIgnoreCase)).Rule;

    /// <summary>The first rule that claims this line, with its speaker and text, or null when it is ordinary output.</summary>
    public ChannelMessage? Match(string line, DateTimeOffset timestamp)
    {
        var plain = Strip(line);
        if (plain.Length == 0 || plain.Length > 2048) return null;
        foreach (var (_, expression) in _exclusions)
            if (Matches(expression, plain) is not null) return null;
        foreach (var (rule, expression) in _compiled)
            if (Matches(expression, plain) is { } match && Message(rule, match, plain, timestamp) is { } message) return message;
        return null;
    }

    /// <summary>What one rule makes of a line, exclusions and the rest of the set aside; the teaching dialog's preview.</summary>
    public static ChannelMessage? Apply(ChannelRule rule, string line, DateTimeOffset timestamp)
    {
        var plain = Strip(line);
        if (plain.Length == 0 || plain.Length > 2048 || Compile(rule.Pattern) is not { } expression) return null;
        return Matches(expression, plain) is { } match ? Message(rule, match, plain, timestamp) : null;
    }

    private static Match? Matches(Regex expression, string plain)
    {
        try { var match = expression.Match(plain); return match.Success ? match : null; }
        catch (RegexMatchTimeoutException) { return null; }
    }

    private static ChannelMessage? Message(ChannelRule rule, Match match, string plain, DateTimeOffset timestamp)
    {
        var speaker = CleanSpeaker(match.Groups["speaker"] is { Success: true } group ? group.Value : "");
        // A speaker is a short name: letters, and for worlds that speak through a description ("A Human
        // male" on a CommNet) spaces, apostrophes and hyphens, never digits or punctuation. Anything else
        // is prose that happens to read like a channel.
        if (speaker.Length > 40 || (speaker.Length > 0 && (!char.IsLetter(speaker[0]) || !speaker.All(c => char.IsLetter(c) || c is ' ' or '\'' or '-')))) return null;
        var text = match.Groups["text"] is { Success: true } body ? body.Value : plain;
        return new(rule.Channel, speaker, text.Trim(), timestamp, plain, rule.IsPrivate) { ReplyCommand = rule.ReplyCommand };
    }

    /// <summary>
    /// The name inside what a taught pattern captured: an account mark (<c>@Nield</c>), a role tag
    /// (<c>Nield [IMM]</c>) and the brackets of a description (<c>[A Human male]</c>) are decoration.
    /// </summary>
    public static string CleanSpeaker(string speaker)
    {
        var name = speaker.Trim();
        if (name.StartsWith('@')) name = name[1..].TrimStart();
        if (name.Length > 1 && name[0] == '[' && name[^1] == ']') name = name[1..^1].Trim();
        var tag = name.LastIndexOf(" [", StringComparison.Ordinal);
        if (tag > 0 && name.EndsWith(']')) name = name[..tag].TrimEnd();
        return name;
    }

    public bool IsChannelLine(string line) => Match(line, DateTimeOffset.UtcNow) is not null;
}
