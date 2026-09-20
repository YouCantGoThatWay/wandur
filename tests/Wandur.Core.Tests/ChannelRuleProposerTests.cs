using Wandur.Core.Channels;

namespace Wandur.Core.Tests;

/// <summary>
/// The teaching heuristic: one line in, a rule out, with the pieces the dialog shows. The lines are the
/// shapes worlds actually print, and every proposal is checked against its own example first.
/// </summary>
public sealed class ChannelRuleProposerTests
{
    private static ChannelMessage Matches(ChannelRule rule, string line)
    {
        var message = ChannelRuleSet.Apply(rule, line, DateTimeOffset.UnixEpoch);
        Assert.NotNull(message);
        return message;
    }

    [Fact]
    public void AnOocLineWithAnAccountMarkAndARoleTag()
    {
        var proposal = ChannelRuleProposer.Propose("(OOC) @Nield [IMM]: Now I'm hungry.");
        Assert.Equal("(OOC) ", proposal.Head);
        Assert.Equal("@Nield [IMM]", proposal.Speaker);
        Assert.Equal(": ", proposal.Separator);
        Assert.Equal("Now I'm hungry.", proposal.Text);
        Assert.Equal("\\(OOC\\) ", proposal.HeadPattern);
        Assert.Equal("@?[A-Za-z]+(?: \\[[A-Za-z]+\\])?", proposal.SpeakerPattern);
        Assert.Equal(": ", proposal.SeparatorPattern);
        Assert.Equal("", proposal.Closing);
        Assert.Equal("^\\(OOC\\) (?<speaker>@?[A-Za-z]+(?: \\[[A-Za-z]+\\])?): (?<text>.*)$", proposal.Rule.Pattern);
        Assert.Equal("ooc", proposal.Rule.Channel);
        Assert.Equal("ooc", proposal.Rule.ReplyCommand);
        Assert.False(proposal.Rule.IsPrivate);
        var message = Matches(proposal.Rule, "(OOC) @Nield [IMM]: Now I'm hungry.");
        Assert.Equal("Nield", message.Speaker);
        Assert.Equal("Now I'm hungry.", message.Text);
        Assert.Equal("Aldric", Matches(proposal.Rule, "(OOC) Aldric: plain name, no tag").Speaker);
        Assert.Null(ChannelRuleSet.Apply(proposal.Rule, "[OOC] Aldric: a different head", DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void ACommNetLineGeneralisesTheFrequencyAndKeepsTheDescription()
    {
        var proposal = ChannelRuleProposer.Propose("CommNet 0 [A Human male]( warmly ): Well played everyone!");
        Assert.Equal("CommNet 0 ", proposal.Head);
        Assert.Equal("CommNet [0-9]+ ", proposal.HeadPattern);
        Assert.Equal("[A Human male]", proposal.Speaker);
        Assert.Equal("\\[[^\\]]+\\]", proposal.SpeakerPattern);
        Assert.Equal(": ", proposal.Separator);
        // One example: the tone is part of the shape.
        Assert.Equal("\\( ?[^)]*? ?\\): ", proposal.SeparatorPattern);
        Assert.Equal("Well played everyone!", proposal.Text);
        Assert.Equal("commnet", proposal.Rule.Channel);
        Assert.Equal("commnet", proposal.Rule.ReplyCommand);
        var message = Matches(proposal.Rule, "CommNet 1250 [A Wookiee]( growling ): Rrrr");
        Assert.Equal("A Wookiee", message.Speaker);
        Assert.Equal("Rrrr", message.Text);
        Assert.Null(ChannelRuleSet.Apply(proposal.Rule, "CommNet 0 [A Human male]: no tone", DateTimeOffset.UnixEpoch));

        // A second example without the tone makes it optional.
        var narrowed = ChannelRuleProposer.Propose("CommNet 0 [A Human male]( warmly ): Well played everyone!", "CommNet 12 [A Twi'lek female]: Thanks!");
        Assert.Equal("(?:\\( ?[^)]*? ?\\))?: ", narrowed.SeparatorPattern);
        Assert.Equal("CommNet [0-9]+ ", narrowed.HeadPattern);
        Assert.Equal("A Human male", Matches(narrowed.Rule, "CommNet 0 [A Human male]( warmly ): Well played everyone!").Speaker);
        Assert.Equal("A Twi'lek female", Matches(narrowed.Rule, "CommNet 12 [A Twi'lek female]: Thanks!").Speaker);
        Assert.Equal("Thanks!", Matches(narrowed.Rule, "CommNet 12 [A Twi'lek female]: Thanks!").Text);
    }

    [Fact]
    public void ATellIsPrivateAndAnswersThePersonWhoSpoke()
    {
        var proposal = ChannelRuleProposer.Propose("Aldric tells you 'the gate is open'");
        Assert.Equal("", proposal.Head);
        Assert.Equal("Aldric", proposal.Speaker);
        Assert.Equal(" tells you '", proposal.Separator);
        Assert.Equal("the gate is open", proposal.Text);
        Assert.Equal("'", proposal.Closing);
        Assert.Equal("^(?<speaker>@?[A-Za-z]+) tells you '(?<text>.*)'$", proposal.Rule.Pattern);
        Assert.Equal("tell", proposal.Rule.Channel);
        Assert.Equal("tell {speaker}", proposal.Rule.ReplyCommand);
        Assert.True(proposal.Rule.IsPrivate);
        Assert.Equal("the gate is open", Matches(proposal.Rule, "Aldric tells you 'the gate is open'").Text);
        Assert.Equal("Brenna", Matches(proposal.Rule, "Brenna tells you 'it's shut again'").Speaker);
    }

    [Fact]
    public void ABracketedHeadNamesTheChannel()
    {
        var proposal = ChannelRuleProposer.Propose("[CHAT] Anka: who is flying tonight?");
        Assert.Equal("[CHAT] ", proposal.Head);
        Assert.Equal("\\[CHAT\\] ", proposal.HeadPattern);
        Assert.Equal("Anka", proposal.Speaker);
        Assert.Equal("@?[A-Za-z]+", proposal.SpeakerPattern);
        Assert.Equal(": ", proposal.Separator);
        Assert.Equal("who is flying tonight?", proposal.Text);
        Assert.Equal("chat", proposal.Rule.Channel);
        Assert.Equal("chat", proposal.Rule.ReplyCommand);
        Assert.Equal("^\\[CHAT\\] (?<speaker>@?[A-Za-z]+): (?<text>.*)$", proposal.Rule.Pattern);
        Assert.Equal("Anka", Matches(proposal.Rule, "[CHAT] Anka: who is flying tonight?").Speaker);
    }

    [Fact]
    public void AVerbSeparatorWithAQuoteNamesTheChannelAndClosesTheText()
    {
        var gossip = ChannelRuleProposer.Propose("Nessa gossips, 'good morning'");
        Assert.Equal("gossip", gossip.Rule.Channel);
        Assert.Equal(", '", gossip.Closing == "'" ? gossip.Separator[^3..] : gossip.Separator);
        Assert.Equal("good morning", Matches(gossip.Rule, "Nessa gossips, 'good morning'").Text);
        var ooc = ChannelRuleProposer.Propose("Cadoc OOC: 'the east gate is closed again'");
        Assert.Equal("ooc", ooc.Rule.Channel);
        Assert.Equal("the east gate is closed again", Matches(ooc.Rule, "Cadoc OOC: 'the east gate is closed again'").Text);
        // A quote the line never closes belongs to the text.
        var unclosed = ChannelRuleProposer.Propose("Bob says: 'tis a fine day");
        Assert.Equal(" says: ", unclosed.Separator);
        Assert.Equal("'tis a fine day", unclosed.Text);
        Assert.Equal("say", unclosed.Rule.Channel);
    }

    [Fact]
    public void TwoExamplesKeepOnlyWhatTheyShare()
    {
        var proposal = ChannelRuleProposer.Propose("[OOC] Aldric: hello", "(OOC) @Brenna [IMM]: hi there");
        Assert.Equal("(?:\\[OOC\\] |\\(OOC\\) )", proposal.HeadPattern);
        Assert.Equal("@?[A-Za-z]+(?: \\[[A-Za-z]+\\])?", proposal.SpeakerPattern);
        Assert.Equal(": ", proposal.SeparatorPattern);
        Assert.Equal("ooc", proposal.Rule.Channel);
        Assert.Equal("Aldric", Matches(proposal.Rule, "[OOC] Aldric: hello").Speaker);
        Assert.Equal("Brenna", Matches(proposal.Rule, "(OOC) @Brenna [IMM]: hi there").Speaker);
        Assert.Null(ChannelRuleSet.Apply(proposal.Rule, "[CHAT] Aldric: hello", DateTimeOffset.UnixEpoch));
        // The same shape twice narrows nothing.
        var same = ChannelRuleProposer.Propose("[CHAT] Anka: one", "[CHAT] Bix: two");
        Assert.Equal("^\\[CHAT\\] (?<speaker>@?[A-Za-z]+): (?<text>.*)$", same.Rule.Pattern);
    }

    [Fact]
    public void ALineWithNoSpeakerYieldsATextGroupOnly()
    {
        var proposal = ChannelRuleProposer.Propose("You are standing in a wide green field.");
        Assert.False(proposal.HasSpeaker);
        Assert.Equal("", proposal.SpeakerPattern);
        Assert.Equal("", proposal.Speaker);
        Assert.DoesNotContain("(?<speaker>", proposal.Rule.Pattern);
        Assert.EndsWith("(?<text>.*)$", proposal.Rule.Pattern);
        Assert.Equal("are standing in a wide green field.", Matches(proposal.Rule, "You are standing in a wide green field.").Text);
        Assert.Equal("", Matches(proposal.Rule, "You are standing in a wide green field.").Speaker);
        var tagged = ChannelRuleProposer.Propose("[INFO] The gates close at dusk.");
        Assert.Equal("[INFO] ", tagged.Head);
        Assert.Equal("info", tagged.Rule.Channel);
        Assert.Equal("^\\[INFO\\] (?<text>.*)$", tagged.Rule.Pattern);
        // Colour is not shape.
        Assert.Equal("^\\[INFO\\] (?<text>.*)$", ChannelRuleProposer.Propose("\u001b[32m[INFO]\u001b[0m The gates close at dusk.\r\n").Rule.Pattern);
    }

    [Fact]
    public void ComposeRebuildsThePatternFromEditedPieces()
    {
        Assert.Equal("^\\[X\\] (?<speaker>[A-Z]+): (?<text>.*)$", ChannelRuleProposer.Compose("\\[X\\] ", "[A-Z]+", ": ", ""));
        Assert.Equal("^(?<speaker>[A-Za-z]+) says '(?<text>.*)'$", ChannelRuleProposer.Compose("", "[A-Za-z]+", " says '", "'"));
        Assert.Equal("^Note: (?<text>.*)$", ChannelRuleProposer.Compose("Note: ", "", ": ", ""));
        Assert.Equal("Line [0-9]+ \\(of [0-9]+\\)\\.", ChannelRuleProposer.Generalize("Line 12 (of 30)."));
        Assert.Equal("tell", ChannelRuleProposer.GuessChannel("", " tells you '"));
        Assert.Equal("shipchannel", ChannelRuleProposer.GuessChannel("[SHIPCHANNEL] ", ": "));
        Assert.Equal("channel", ChannelRuleProposer.GuessChannel("", ": "));
        Assert.Equal("whisper {speaker}", ChannelRuleProposer.GuessReply("whisper"));
        Assert.Null(ChannelRuleProposer.GuessReply("..."));
    }
}
