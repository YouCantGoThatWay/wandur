using System.Text;
using System.Text.RegularExpressions;

namespace Wandur.Core.Channels;

/// <summary>
/// One transcript line read as a channel line: the literal pieces (<see cref="Head"/>, <see cref="Speaker"/>,
/// <see cref="Separator"/>, <see cref="Text"/>) and the patterns proposed for them, which compose into
/// <see cref="Rule"/>. The dialog shows the pieces and lets the reader edit the patterns; an empty
/// <see cref="SpeakerPattern"/> means no speaker was recognised and the rule carries a text group only.
/// </summary>
public sealed record ChannelRuleProposal(ChannelRule Rule, string Head, string Speaker, string Separator, string Text,
    string HeadPattern, string SpeakerPattern, string SeparatorPattern, string Closing)
{
    public bool HasSpeaker => SpeakerPattern.Length > 0;
}

/// <summary>
/// Guesses the shape of a channel line from one example, or two. Worlds print channels as a literal head
/// (<c>(OOC) </c>, <c>[CHAT] </c>, <c>CommNet 0 </c>), a speaker (a name such as <c>@Nield</c> or <c>Aldric</c>,
/// possibly followed by a role tag <c>[IMM]</c>, or a bracketed description <c>[A Human male]</c> with an
/// optional tone), a separator (<c>: </c>, <c> tells you '</c>, <c>, '</c>) and the text. The proposal keeps
/// the head as an escaped literal with its digits generalised, the speaker as a name or description
/// class, and the text as everything to the end of the line inside whatever quote opened it. With a
/// second example the pieces are narrowed to what both lines share. The result is a starting point
/// the reader corrects, never a parser of record.
/// </summary>
public static class ChannelRuleProposer
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);
    private static readonly Regex Name = new(@"\G@?[A-Z][a-z]+(?:['-][A-Za-z]+)*\b", RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex RoleTag = new(@"\G \[[A-Za-z]+\]", RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex Description = new(@"\G\[[A-Za-z][A-Za-z' -]*\]", RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex Tone = new(@"\G\( ?[^()]*? ?\)", RegexOptions.CultureInvariant, Timeout);
    // Up to three words of verb ("tells you", "says", "OOC"), then what opens the text: a colon, a quote,
    // a comma and a quote, or a prompt arrow. A bare space is not a separator, or every capitalised
    // sentence would read as a speaker.
    private static readonly Regex SeparatorShape = new(@"\G(?: ?[A-Za-z]+){0,3}(?:,? ?['""]|: ?['""]?|> ?)(?=\S)", RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex LeadingTag = new(@"^(?:\[[^\]]{1,20}\]|\([^)]{1,20}\)|[A-Za-z]+)[: ]*", RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex Letters = new(@"[A-Za-z]+", RegexOptions.CultureInvariant, Timeout);
    private const string TonePattern = @"\( ?[^)]*? ?\)";
    private const string NamePattern = "@?[A-Za-z]+";
    private const string WideNamePattern = "@?[A-Za-z'-]+";
    private const string DescriptionPattern = @"\[[^\]]+\]";
    private const string RoleTagPattern = @"(?: \[[A-Za-z]+\])?";

    /// <summary>The proposal for one line, narrowed by a second example of the same channel when given.</summary>
    public static ChannelRuleProposal Propose(string line, string? second = null)
    {
        var parts = Read(ChannelRuleSet.Strip(line ?? "").Trim());
        if (!string.IsNullOrWhiteSpace(second)) parts = Narrow(parts, Read(ChannelRuleSet.Strip(second).Trim()));
        var channel = GuessChannel(parts.Head, parts.Separator);
        var pattern = Compose(parts.HeadPattern, parts.SpeakerPattern, parts.SeparatorPattern, parts.Closing);
        var rule = new ChannelRule(channel, pattern, GuessReply(channel), IsPrivateChannel(channel));
        return new(rule, parts.Head, parts.Speaker, parts.Separator, parts.Text, parts.HeadPattern, parts.SpeakerPattern, parts.SeparatorPattern, parts.Closing);
    }

    /// <summary>The rule pattern the pieces compose into; the dialog calls this again after every edit.</summary>
    public static string Compose(string headPattern, string speakerPattern, string separatorPattern, string closing)
    {
        var pattern = new StringBuilder("^").Append(headPattern);
        if (speakerPattern.Length > 0) pattern.Append("(?<speaker>").Append(speakerPattern).Append(')').Append(separatorPattern);
        return pattern.Append("(?<text>.*)").Append(Escape(closing)).Append('$').ToString();
    }

    /// <summary>
    /// The channel a head or a separator names: the first word of the head (<c>ooc</c>, <c>chat</c>,
    /// <c>commnet</c>), else the verb in the separator with its plural s dropped (<c>tells you</c> is
    /// <c>tell</c>, <c>gossips</c> is <c>gossip</c>), else <c>channel</c>.
    /// </summary>
    public static string GuessChannel(string head, string separator)
    {
        foreach (var source in new[] { head, separator })
        {
            foreach (Match word in Letters.Matches(source))
            {
                var name = word.Value.ToLowerInvariant();
                if (name is "you" or "the" or "to" or "a" or "an") continue;
                if (name.Length > 3 && name.EndsWith('s') && !name.EndsWith("ss")) name = name[..^1];
                return name;
            }
        }
        return "channel";
    }

    /// <summary>Tells, pages and whispers answer one person; everything else is a room of people.</summary>
    public static bool IsPrivateChannel(string channel)
        => new[] { "tell", "whisper", "page", "reply", "msg" }.Any(name => channel.Contains(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Worlds name the command after the channel, which is the only guess worth making.</summary>
    public static string? GuessReply(string channel)
    {
        var name = new string([.. channel.Where(char.IsAsciiLetterOrDigit)]).ToLowerInvariant();
        return name.Length == 0 ? null : IsPrivateChannel(name) ? name + " {speaker}" : name;
    }

    /// <summary>Escapes a literal for a pattern, leaving spaces readable and generalising runs of digits.</summary>
    public static string Generalize(string literal)
    {
        var result = new StringBuilder();
        for (var i = 0; i < literal.Length; i++)
        {
            if (char.IsAsciiDigit(literal[i]))
            {
                while (i + 1 < literal.Length && char.IsAsciiDigit(literal[i + 1])) i++;
                result.Append("[0-9]+");
                continue;
            }
            result.Append(Escape(literal[i]));
        }
        return result.ToString();
    }

    private static string Escape(string literal)
    {
        var result = new StringBuilder();
        foreach (var c in literal) result.Append(Escape(c));
        return result.ToString();
    }

    private static string Escape(char c) => c is '\\' or '*' or '+' or '?' or '|' or '{' or '}' or '[' or ']' or '(' or ')' or '^' or '$' or '.' or '#' ? "\\" + c : c.ToString();

    private enum SpeakerKind { None, Name, Description }

    /// <summary>The pieces one line was cut into, and the patterns they became.</summary>
    private sealed record Parts(string Head, string Speaker, string Separator, string Text, string Closing, SpeakerKind Kind, bool RoleTag, bool Tone)
    {
        public string HeadPattern { get; init; } = Generalize(Head);
        public string SpeakerPattern { get; init; } = Kind switch
        {
            SpeakerKind.Name => (Speaker.Any(c => c is '\'' or '-') ? WideNamePattern : NamePattern) + (RoleTag ? RoleTagPattern : ""),
            SpeakerKind.Description => DescriptionPattern,
            _ => ""
        };
        public string SeparatorPattern { get; init; } = (Tone ? TonePattern : "") + Escape(Separator);
    }

    private static Parts Read(string plain)
    {
        // Names first, left to right, then descriptions: a bracketed tag at the start of the line
        // ("[CHAT] Anka: ...") is a head, and only when no name follows it is a bracket the speaker.
        for (var i = 0; i < plain.Length; i++)
        {
            if (i > 0 && char.IsLetterOrDigit(plain[i - 1])) continue;
            if (TryName(plain, i) is { } parts) return parts;
        }
        for (var i = 0; i < plain.Length; i++)
            if (TryDescription(plain, i) is { } parts) return parts;
        // No speaker: the leading tag or word is the head and the rest is the text.
        var head = LeadingTag.Match(plain) is { Success: true } tag && tag.Length < plain.Length ? tag.Value : "";
        return new(head, "", "", plain[head.Length..], "", SpeakerKind.None, false, false);
    }

    private static Parts? TryName(string plain, int at)
    {
        var name = Name.Match(plain, at);
        if (!name.Success) return null;
        var end = name.Index + name.Length;
        var tag = RoleTag.Match(plain, end);
        if (tag.Success) end += tag.Length;
        if (Separator(plain, end) is not var (separator, text, closing)) return null;
        return new(plain[..at], name.Value + (tag.Success ? tag.Value : ""), separator, text, closing, SpeakerKind.Name, tag.Success, false);
    }

    private static Parts? TryDescription(string plain, int at)
    {
        var description = Description.Match(plain, at);
        if (!description.Success) return null;
        var end = description.Index + description.Length;
        var tone = Tone.Match(plain, end);
        if (tone.Success) end += tone.Length;
        if (Separator(plain, end) is not var (separator, text, closing)) return null;
        return new(plain[..at], description.Value, separator, text, closing, SpeakerKind.Description, false, tone.Success);
    }

    /// <summary>The separator after a speaker and the text it opens; an opening quote counts only when the line closes it.</summary>
    private static (string Separator, string Text, string Closing)? Separator(string plain, int at)
    {
        var match = SeparatorShape.Match(plain, at);
        if (!match.Success || match.Length == 0) return null;
        var separator = match.Value;
        var quote = separator.TrimEnd().LastOrDefault();
        if (quote is '\'' or '"')
        {
            var start = at + separator.Length;
            if (plain.Length - 1 > start && plain[^1] == quote) return (separator, plain[start..^1], quote.ToString());
            // The quote opened something the line never closed, so it belongs to the text.
            separator = separator[..separator.LastIndexOf(quote)];
            if (separator.Trim().Length == 0) return null;
        }
        return (separator, plain[(at + separator.Length)..], "");
    }

    /// <summary>Keeps what two examples share and widens each piece where they differ.</summary>
    private static Parts Narrow(Parts first, Parts second)
    {
        if (first.Kind == SpeakerKind.None || second.Kind == SpeakerKind.None) return first.Kind == SpeakerKind.None ? second : first;
        var head = first.HeadPattern == second.HeadPattern ? first.HeadPattern
            : first.Head.Length == 0 || second.Head.Length == 0 ? "(?:" + Generalize(first.Head + second.Head) + ")?"
            : "(?:" + first.HeadPattern + "|" + second.HeadPattern + ")";
        var bothNames = first.Kind == SpeakerKind.Name && second.Kind == SpeakerKind.Name;
        var speaker = bothNames
            ? ((first.Speaker + second.Speaker).Any(c => c is '\'' or '-') ? WideNamePattern : NamePattern) + (first.RoleTag || second.RoleTag ? RoleTagPattern : "")
            : first.SpeakerPattern == second.SpeakerPattern ? first.SpeakerPattern : "(?:" + first.SpeakerPattern + "|" + second.SpeakerPattern + ")";
        var tone = first.Tone != second.Tone ? "(?:" + TonePattern + ")?" : first.Tone ? TonePattern : "";
        var separator = first.Separator == second.Separator ? Escape(first.Separator)
            : "(?:" + Escape(first.Separator) + "|" + Escape(second.Separator) + ")";
        // The literal pieces stay those of the first example; only the patterns widen.
        return first with
        {
            Closing = first.Closing == second.Closing ? first.Closing : "",
            HeadPattern = head, SpeakerPattern = speaker, SeparatorPattern = tone + separator
        };
    }
}
