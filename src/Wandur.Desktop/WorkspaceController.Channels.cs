using Wandur.Core.Channels;
using Wandur.Core.Settings;
using Wandur.Core.Terminal;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop;

/// <summary>
/// Channel recognition. Every line still reaches the transcript untouched; what is recognized here is
/// copied to the Channels panel as well, which is why nothing in this file ever suppresses output.
/// </summary>
public sealed partial class WorkspaceController
{
    private ChannelClassifier _channels = new(ChannelFamilies.Family(ChannelFamilies.Generic));

    /// <summary>A recognized channel line, and whether it extends the message before it rather than starting one.</summary>
    public event Action<ChannelMessage, bool>? ChannelMessageReceived;
    public ChannelRuleSet ChannelRules => _channels.Rules;
    /// <summary>The world this session opened as, with the channel rules in force; null for the demo.</summary>
    public ConnectionProfile? ActiveProfile { get; private set; }

    private void ConfigureChannels(ConnectionProfile? profile)
    {
        ActiveProfile = profile;
        _channels = new(ChannelFamilies.For(profile?.ChannelRules, profile?.Codebase));
    }

    /// <summary>
    /// Settings were saved, here or in another tab: when the saved copy of this world carries different rules
    /// or a different codebase, the classifier picks them up at once, keeping whatever line was in flight.
    /// </summary>
    private void RefreshChannelRules()
    {
        if (ActiveProfile is not { } active || Settings.Profiles.FirstOrDefault(p => p.Id == active.Id) is not { } saved) return;
        if (saved.ChannelRules.Equals(active.ChannelRules) && saved.Codebase == active.Codebase) return;
        ActiveProfile = active with { ChannelRules = saved.ChannelRules, Codebase = saved.Codebase };
        _channels.Rules = ChannelFamilies.For(saved.ChannelRules, saved.Codebase);
    }

    /// <summary>
    /// Teaches this world one rule: it is appended to the profile's rules, saved through the settings store
    /// like any profile change, and in force for the next line. A world opened from the directory but not
    /// yet saved is saved by this, since the rule needs a profile to live on.
    /// </summary>
    public void TeachChannelRule(ChannelRule rule)
    {
        if (ActiveProfile is not { } active) throw new InvalidOperationException(L.TeachChannelNoProfile);
        var profile = Settings.Profiles.FirstOrDefault(p => p.Id == active.Id) ?? active;
        var updated = profile with { ChannelRules = new([.. profile.ChannelRules, rule]) };
        updated.Validate();
        var profiles = Settings.Profiles.Any(p => p.Id == updated.Id)
            ? Settings.Profiles.Select(p => p.Id == updated.Id ? updated : p).ToList()
            : [.. Settings.Profiles, updated];
        SaveSettings(Settings with { Profiles = profiles });
        ShowNotice(L.Format(L.TeachChannelSaved, updated.Name));
    }

    /// <summary>The newest lines of the transcript, plain text, for a rule's preview.</summary>
    public IReadOnlyList<string> RecentTranscriptLines(int count)
    {
        var lines = Terminal.Lines;
        var result = new List<string>(Math.Min(count, lines.Count));
        for (var i = lines.Count - 1; i >= 0 && result.Count < count; i--)
        {
            var plain = ChannelRuleSet.Strip(lines[i].Text).TrimEnd();
            if (plain.Length > 0) result.Add(plain);
        }
        result.Reverse();
        return result;
    }

    /// <summary>Public output only: the caller applies the same privacy gate the agent feed uses.</summary>
    private void FeedChannels(string text)
    {
        if (ChannelMessageReceived is null) { _channels.Feed(text, DateTimeOffset.Now); return; }
        foreach (var line in _channels.Feed(text, DateTimeOffset.Now))
            if (line.Message is { } message) ChannelMessageReceived.Invoke(message, line.IsContinuation);
    }

    /// <summary>
    /// A world that speaks GMCP names its own channels, so the structured message is routed directly. Worlds
    /// usually print the same line as well; the classifier drops that copy rather than showing it twice.
    /// </summary>
    private void FeedChannelProtocol(string gmcp)
    {
        if (CommChannelProtocol.Decode(gmcp) is not { } package) return;
        var plain = ChannelRuleSet.Strip(package.Text);
        _channels.ExpectPrintedCopy(plain, DateTimeOffset.Now);
        if (ChannelMessageReceived is null) return;
        // The package's text is the rendered line, so a matching rule trims the prefix the panel already shows.
        var matched = _channels.Rules.Match(plain, DateTimeOffset.Now);
        var rule = _channels.Rules.ForChannel(package.Channel);
        var speaker = package.Talker.Length > 0 ? package.Talker : matched?.Speaker ?? "";
        var body = matched is not null && matched.Text.Length > 0 ? matched.Text : plain;
        var colors = new AnsiTerminal(2);
        colors.Append(package.Text + "\n");
        IReadOnlyList<TextRun> runs = colors.Lines.Count > 1 ? colors.Lines[^2].Runs : [new TextRun(plain, new TextStyle())];
        runs = ChannelClassifier.Body(runs, plain, body);
        var isPrivate = rule?.IsPrivate ?? matched?.IsPrivate ?? new[] { "tell", "page", "whisper" }
            .Any(name => package.Channel.Contains(name, StringComparison.OrdinalIgnoreCase));
        var reply = rule?.ReplyCommand ?? matched?.ReplyCommand ?? DefaultReply(package.Channel, isPrivate);
        ChannelMessageReceived.Invoke(new(package.Channel, speaker, body, DateTimeOffset.Now, plain, isPrivate) { Runs = runs, ReplyCommand = reply }, false);
    }

    /// <summary>Worlds name the command after the channel, which is the only guess worth making.</summary>
    private static string? DefaultReply(string channel, bool isPrivate)
    {
        var name = new string([.. channel.Where(char.IsAsciiLetterOrDigit)]).ToLowerInvariant();
        return name.Length == 0 ? null : isPrivate ? name + " {speaker}" : name;
    }
}
