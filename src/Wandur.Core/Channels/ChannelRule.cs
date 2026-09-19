using System.Text.RegularExpressions;
using Wandur.Core.Terminal;

namespace Wandur.Core.Channels;

/// <summary>
/// One recognized communication channel shape. <paramref name="Pattern"/> is a .NET regular expression
/// anchored to the start of a plain (ANSI stripped) line; the optional named groups <c>speaker</c> and
/// <c>text</c> carry who spoke and what they said. <paramref name="ReplyCommand"/> is the command prefix
/// that speaks on the channel, where <c>{speaker}</c> is replaced by the person being answered.
/// </summary>
public sealed record ChannelRule(string Channel, string Pattern, string? ReplyCommand = null, bool IsPrivate = false);

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

/// <summary>An ordered set of rules. The first rule that matches wins, so one line is never two messages.</summary>
public sealed class ChannelRuleSet
{
    /// <summary>A pathological pattern must not stall the output pump; an expensive line simply does not match.</summary>
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(50);
    private static readonly Regex AnsiSequence = new("\u001b\\[[0-9;?]*[ -/]*[@-~]|\u001b\\][^\u0007]*\u0007|\u001b.",
        RegexOptions.CultureInvariant, MatchTimeout);
    private readonly List<(ChannelRule Rule, Regex Expression)> _compiled = [];

    public ChannelRuleSet(IEnumerable<ChannelRule> rules)
    {
        foreach (var rule in rules)
        {
            if (rule is null || string.IsNullOrWhiteSpace(rule.Channel) || string.IsNullOrEmpty(rule.Pattern)) continue;
            // A world profile may carry a hand written pattern. An invalid one is ignored, never fatal.
            try
            {
                var pattern = rule.Pattern.StartsWith('^') ? rule.Pattern : "^" + rule.Pattern;
                _compiled.Add((rule, new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, MatchTimeout)));
            }
            catch (ArgumentException) { }
        }
    }

    public IReadOnlyList<ChannelRule> Rules => [.. _compiled.Select(entry => entry.Rule)];
    public int Count => _compiled.Count;

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
        foreach (var (rule, expression) in _compiled)
        {
            Match match;
            try { match = expression.Match(plain); }
            catch (RegexMatchTimeoutException) { continue; }
            if (!match.Success) continue;
            var speaker = match.Groups["speaker"] is { Success: true } group ? group.Value : "";
            // A speaker is one word of letters. Anything else is prose that happens to read like a channel.
            if (speaker.Length > 0 && !speaker.All(char.IsLetter)) continue;
            var text = match.Groups["text"] is { Success: true } body ? body.Value : plain;
            return new(rule.Channel, speaker, text.Trim(), timestamp, plain, rule.IsPrivate) { ReplyCommand = rule.ReplyCommand };
        }
        return null;
    }

    public bool IsChannelLine(string line) => Match(line, DateTimeOffset.UtcNow) is not null;
}
