using Wandur.Core.Channels;
using Wandur.Core.Settings;

namespace Wandur.Core.Tests;

/// <summary>
/// Channel recognition per codebase family. The samples are written here rather than captured from a world,
/// and every one of them still belongs in the transcript: the classifier only says what may be mirrored.
/// </summary>
public sealed class ChannelClassifierTests
{
    private static ChannelMessage? Classify(ChannelRuleSet rules, string line) => rules.Match(line, DateTimeOffset.UnixEpoch);

    private static void Expect(ChannelRuleSet rules, string line, string channel, string speaker, string text)
    {
        var message = Classify(rules, line);
        Assert.NotNull(message);
        Assert.Equal(channel, message.Channel);
        Assert.Equal(speaker, message.Speaker);
        Assert.Equal(text, message.Text);
    }

    [Fact]
    public void SmaugRecognizesItsOocChatNewbieAndTellShapes()
    {
        var rules = ChannelFamilies.Family("smaug");
        Expect(rules, "[OOC] Aldric: anyone selling a lantern?", "ooc", "Aldric", "anyone selling a lantern?");
        Expect(rules, "(OOC) Brenna: welcome back to the realms", "ooc", "Brenna", "welcome back to the realms");
        Expect(rules, "Cadoc OOC: 'the east gate is closed again'", "ooc", "Cadoc", "the east gate is closed again");
        Expect(rules, "Dwyn OOC: heading out for the night", "ooc", "Dwyn", "heading out for the night");
        Expect(rules, "[CHAT] Eowyn: who wants to group up?", "chat", "Eowyn", "who wants to group up?");
        Expect(rules, "(CHAT) Faelan: I am in", "chat", "Faelan", "I am in");
        Expect(rules, "Gorm CHAT: 'meet me at the fountain'", "chat", "Gorm", "meet me at the fountain");
        Expect(rules, "[NEWBIE] Hilda: how do I wield a sword?", "newbie", "Hilda", "how do I wield a sword?");
        Expect(rules, "(NEWBIE) Ivo: type wield sword", "newbie", "Ivo", "type wield sword");
        Expect(rules, "Jorunn tells you 'bring the brass key'", "tell", "Jorunn", "bring the brass key");
        Expect(rules, "Kel tells you: the north door is unlocked", "tell", "Kel", "the north door is unlocked");
        Expect(rules, "Lira auctions 'a dented bronze helm'", "auction", "Lira", "a dented bronze helm");
        Expect(rules, "Mabon shouts 'the river bridge is out'", "shout", "Mabon", "the river bridge is out");
        Assert.Null(Classify(rules, "Nessa says, 'good morning'"));
        Assert.Null(Classify(rules, "You are standing in a wide green field."));
    }

    [Fact]
    public void StarWarsRealityAddsItsOwnShapesOnTopOfSmaug()
    {
        var rules = ChannelFamilies.Family("swr");
        Expect(rules, "[OOC] Aldric: anyone selling a lantern?", "ooc", "Aldric", "anyone selling a lantern?");
        Expect(rules, "[OOC] (Xanto) may the force be with you", "ooc", "Xanto", "may the force be with you");
        Expect(rules, "[SHIP] Talon: docking at bay three", "shipchannel", "Talon", "docking at bay three");
        Expect(rules, "[CLAN] Vex: meeting at dawn", "clantalk", "Vex", "meeting at dawn");
        Expect(rules, "Yara tells you 'the hangar is clear'", "tell", "Yara", "the hangar is clear");
        Expect(rules, "Zev OOC: 'back in ten minutes'", "ooc", "Zev", "back in ten minutes");
        Expect(rules, "[CHAT] Anka: who is flying tonight?", "chat", "Anka", "who is flying tonight?");
        Expect(rules, "[NEWBIE] Bix: how do I board a ship?", "newbie", "Bix", "how do I board a ship?");
        Expect(rules, "Cade tells you: land on pad two", "tell", "Cade", "land on pad two");
        Expect(rules, "(OOC) Dax: good hunting", "ooc", "Dax", "good hunting");
        Expect(rules, "Enno shouts 'raid incoming'", "shout", "Enno", "raid incoming");
        // Legends of the Jedi marks account holders with @ and staff with a role tag, and has a CommNet channel
        // with a frequency and a tone; these are the shapes the owner's transcript showed on 2026-09-19.
        Expect(rules, "(OOC) @Ryken: i had hibachi last week, it was excellent", "ooc", "Ryken", "i had hibachi last week, it was excellent");
        Expect(rules, "(OOC) @Nield [IMM]: Now I'm hungry.", "ooc", "Nield", "Now I'm hungry.");
        Expect(rules, "(OOC) @Fishy [NEW]: the natroll is waiting for me next tl", "ooc", "Fishy", "the natroll is waiting for me next tl");
        Expect(rules, "(OOC) @Faern [RPC]: While I expect the pats to be better than the Jets most years", "ooc", "Faern", "While I expect the pats to be better than the Jets most years");
        Expect(rules, "CommNet 0 [A Human male]( warmly ): Well played everyone!", "commnet", "A Human male", "Well played everyone!");
        Expect(rules, "CommNet 0 [A Human female]: Yuriko? Are you on Ryloth and not busy?", "commnet", "A Human female", "Yuriko? Are you on Ryloth and not busy?");
        Assert.Null(Classify(rules, "Fenn says, 'nice ship'"));
        Assert.Null(Classify(rules, "If you have any questions, you can ask on the RPC or OOC channels."));
    }

    [Fact]
    public void DikuRecognizesGossipGroupAndTellAndLeavesSayAlone()
    {
        var rules = ChannelFamilies.Family("diku");
        Expect(rules, "Aric gossips, 'anyone need a cleric?'", "gossip", "Aric", "anyone need a cleric?");
        Expect(rules, "Brann gossips 'I could use one too'", "gossip", "Brann", "I could use one too");
        Expect(rules, "Cora tells you, 'follow me north'", "tell", "Cora", "follow me north");
        Expect(rules, "Doran tells you 'wait by the gate'", "tell", "Doran", "wait by the gate");
        Expect(rules, "Eda tells the group 'pulling now'", "group", "Eda", "pulling now");
        Expect(rules, "Finn tells the group, 'ready when you are'", "group", "Finn", "ready when you are");
        Expect(rules, "[Newbie] Gwen: where is the bank?", "newbie", "Gwen", "where is the bank?");
        Expect(rules, "Hal auctions, 'a steel shield'", "auction", "Hal", "a steel shield");
        Expect(rules, "Ivo auctions 'a rusty dagger'", "auction", "Ivo", "a rusty dagger");
        Expect(rules, "Jory shouts, 'help in the crypt'", "shout", "Jory", "help in the crypt");
        Expect(rules, "Kara shouts 'on my way'", "shout", "Kara", "on my way");
        Assert.Null(Classify(rules, "Lem says, 'hello there'"));
        Assert.Null(Classify(rules, "Mira says 'watch your step'"));
    }

    [Fact]
    public void RomAddsQuestionAndAnswerToTheDikuShapes()
    {
        var rules = ChannelFamilies.Family("rom");
        Expect(rules, "Mira questions 'where is the smith?'", "question", "Mira", "where is the smith?");
        Expect(rules, "Nolan answers 'just south of the square'", "answer", "Nolan", "just south of the square");
        Expect(rules, "Orin MUSIC: 'a tune from the north'", "music", "Orin", "a tune from the north");
        Expect(rules, "Pell gossips 'good evening'", "gossip", "Pell", "good evening");
        Expect(rules, "Quill gossips, 'and to you'", "gossip", "Quill", "and to you");
        Expect(rules, "Rhys tells you 'meet me at the inn'", "tell", "Rhys", "meet me at the inn");
        Expect(rules, "Sena tells you, 'the guard is watching'", "tell", "Sena", "the guard is watching");
        Expect(rules, "Toran tells the group 'healing up'", "group", "Toran", "healing up");
        Expect(rules, "[Newbie] Ura: how do I recall?", "newbie", "Ura", "how do I recall?");
        Expect(rules, "Vann shouts 'the gates are open'", "shout", "Vann", "the gates are open");
        Expect(rules, "Wend auctions 'a plain iron ring'", "auction", "Wend", "a plain iron ring");
        Assert.Null(Classify(rules, "Yorick says, 'welcome traveller'"));
    }

    [Fact]
    public void CircleAddsHollerAndCongratToTheDikuShapes()
    {
        var rules = ChannelFamilies.Family("circle");
        Expect(rules, "Orin hollers, 'everyone out of the tower'", "holler", "Orin", "everyone out of the tower");
        Expect(rules, "Pia congrats, 'well done on thirty'", "congrat", "Pia", "well done on thirty");
        Expect(rules, "Quen gossips, 'morning all'", "gossip", "Quen", "morning all");
        Expect(rules, "Rolf gossips 'morning'", "gossip", "Rolf", "morning");
        Expect(rules, "Sig tells you, 'the shop is closed'", "tell", "Sig", "the shop is closed");
        Expect(rules, "Tam tells you 'try again tomorrow'", "tell", "Tam", "try again tomorrow");
        Expect(rules, "Ulla tells the group 'buffing now'", "group", "Ulla", "buffing now");
        Expect(rules, "[Newbie] Vala: what does score show?", "newbie", "Vala", "what does score show?");
        Expect(rules, "Wim auctions, 'a soft leather cap'", "auction", "Wim", "a soft leather cap");
        Expect(rules, "Xan shouts, 'guards to the east gate'", "shout", "Xan", "guards to the east gate");
        Expect(rules, "Yara shouts 'coming'", "shout", "Yara", "coming");
        Assert.Null(Classify(rules, "Zeno says, 'mind the step'"));
    }

    [Fact]
    public void LpRecognizesBracketedChannelsOnEitherSideOfTheName()
    {
        var rules = ChannelFamilies.Family("lp");
        Expect(rules, "[chat] Perrin: evening all", "chat", "Perrin", "evening all");
        Expect(rules, "Quinn [chat]: evening", "chat", "Quinn", "evening");
        Expect(rules, "[newbie] Rhea: how do I look at things?", "newbie", "Rhea", "how do I look at things?");
        Expect(rules, "Sten [newbie]: type look", "newbie", "Sten", "type look");
        Expect(rules, "[ooc] Tova: back in a moment", "ooc", "Tova", "back in a moment");
        Expect(rules, "Ulf [ooc]: no rush", "ooc", "Ulf", "no rush");
        Expect(rules, "Vidar tells you: meet me at the well", "tell", "Vidar", "meet me at the well");
        Expect(rules, "Wren tells you 'on my way'", "tell", "Wren", "on my way");
        Expect(rules, "[Chat] Yvo: the guild hall moved", "chat", "Yvo", "the guild hall moved");
        Expect(rules, "[NEWBIE] Zara: thank you", "newbie", "Zara", "thank you");
        Assert.Null(Classify(rules, "Alva says: welcome"));
        Assert.Null(Classify(rules, "The wizard smiles at you."));
    }

    [Fact]
    public void GenericKeepsOnlyTheShapesThatAreSafeAcrossCodebases()
    {
        var rules = ChannelFamilies.Family(null);
        Expect(rules, "[OOC] Aldric: good evening", "ooc", "Aldric", "good evening");
        Expect(rules, "(OOC) Brenna: and to you", "ooc", "Brenna", "and to you");
        Expect(rules, "[CHAT] Cass: anyone around?", "chat", "Cass", "anyone around?");
        Expect(rules, "(CHAT) Dara: over here", "chat", "Dara", "over here");
        Expect(rules, "[NEWBIE] Edda: how do I get help?", "newbie", "Edda", "how do I get help?");
        Expect(rules, "(NEWBIE) Fell: type help", "newbie", "Fell", "type help");
        Expect(rules, "Gale gossips, 'the market is open'", "gossip", "Gale", "the market is open");
        Expect(rules, "Hune gossips 'thanks'", "gossip", "Hune", "thanks");
        Expect(rules, "Idris tells you 'meet me inside'", "tell", "Idris", "meet me inside");
        Expect(rules, "Jek tells you, 'the door sticks'", "tell", "Jek", "the door sticks");
        Expect(rules, "Kile tells you: on my way", "tell", "Kile", "on my way");
        Assert.Null(Classify(rules, "Lorn says, 'good hunting'"));
        // A shape only some codebases use stays out of the generic set rather than guessing.
        Assert.Null(Classify(rules, "Mave hollers, 'fire!'"));
    }

    [Fact]
    public void TellsAndPagesAreMarkedPrivateAndOpenChannelsAreNot()
    {
        var rules = ChannelFamilies.Family("smaug");
        Assert.True(Classify(rules, "Jorunn tells you 'this is between us'")!.IsPrivate);
        Assert.Equal("tell {speaker}", Classify(rules, "Jorunn tells you 'x'")!.ReplyCommand);
        Assert.False(Classify(rules, "[OOC] Aldric: hello")!.IsPrivate);
        Assert.Equal("ooc", Classify(rules, "[OOC] Aldric: hello")!.ReplyCommand);
    }

    [Fact]
    public void TheFirstRuleThatMatchesWinsAndALineIsNeverTwoMessages()
    {
        var rules = new ChannelRuleSet([
            new("first", "\\[OOC\\] (?<speaker>[A-Za-z]+): (?<text>.*)$", "one"),
            new("second", "\\[OOC\\] (?<speaker>[A-Za-z]+): (?<text>.*)$", "two")]);
        var message = Classify(rules, "[OOC] Aldric: hello");
        Assert.NotNull(message);
        Assert.Equal("first", message.Channel);
        Assert.Equal("one", message.ReplyCommand);
    }

    [Fact]
    public void AMultiWordSpeakerIsProseRatherThanAChannel()
    {
        var rules = ChannelFamilies.Family("diku");
        Assert.Null(Classify(rules, "The town crier gossips, 'hear ye'"));
        Assert.NotNull(Classify(rules, "Crier gossips, 'hear ye'"));
    }

    [Fact]
    public void ColorCodesAreStrippedBeforeMatchingAndKeptForTheRuns()
    {
        var classifier = new ChannelClassifier(ChannelFamilies.Family("smaug"));
        var lines = classifier.Feed("\u001b[1;33m[OOC] Bora:\u001b[0;32m hello there\u001b[0m\r\n", DateTimeOffset.UnixEpoch);
        var message = Assert.Single(lines).Message;
        Assert.NotNull(message);
        Assert.Equal("ooc", message.Channel);
        Assert.Equal("Bora", message.Speaker);
        Assert.Equal("hello there", message.Text);
        Assert.DoesNotContain('\u001b', message.RawLine);
        Assert.Equal("hello there", string.Concat(message.Runs.Select(r => r.Text)).Trim());
        Assert.Contains(message.Runs, run => run.Style.Foreground is not null);
    }

    [Fact]
    public void AnIndentedFollowingLineExtendsTheMessageAboveIt()
    {
        var classifier = new ChannelClassifier(ChannelFamilies.Family("smaug"));
        Assert.Single(classifier.Feed("[OOC] Aldric: I had a longer thought about\r\n", DateTimeOffset.UnixEpoch));
        var continued = Assert.Single(classifier.Feed("     the price of lanterns lately\r\n", DateTimeOffset.UnixEpoch));
        Assert.True(continued.IsContinuation);
        Assert.Equal("I had a longer thought about the price of lanterns lately", continued.Message!.Text);
        // A line back at the margin is ordinary output again, not more of the conversation.
        Assert.Empty(classifier.Feed("A cold wind blows.\r\n", DateTimeOffset.UnixEpoch));
        Assert.Empty(classifier.Feed("     and this is no longer a channel line\r\n", DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void OnlyCompleteLinesAreClassified()
    {
        var classifier = new ChannelClassifier(ChannelFamilies.Family("smaug"));
        Assert.Empty(classifier.Feed("[OOC] Aldric: half a ", DateTimeOffset.UnixEpoch));
        var message = Assert.Single(classifier.Feed("line arrives later\r\n", DateTimeOffset.UnixEpoch)).Message;
        Assert.Equal("half a line arrives later", message!.Text);
    }

    [Fact]
    public void AStructuredMessageSuppressesThePrintedCopyOfTheSameLine()
    {
        var classifier = new ChannelClassifier(ChannelFamilies.Family("smaug"));
        var now = DateTimeOffset.UnixEpoch;
        classifier.ExpectPrintedCopy("[OOC] Aldric: hello", now);
        Assert.Empty(classifier.Feed("[OOC] Aldric: hello\r\n", now));
        // A different line, and the same line a minute later, are both real output.
        classifier.ExpectPrintedCopy("[OOC] Aldric: hello", now);
        Assert.Single(classifier.Feed("[OOC] Brenna: hello\r\n", now));
        classifier.ExpectPrintedCopy("[OOC] Aldric: hello", now);
        Assert.Single(classifier.Feed("[OOC] Aldric: hello\r\n", now.AddMinutes(1)));
    }

    [Fact]
    public void GmcpChannelMessagesDecodeToAChannelSpeakerAndLine()
    {
        var package = CommChannelProtocol.Decode("Comm.Channel.Text {\"channel\":\"ooc\",\"talker\":\"Aldric\",\"text\":\"[OOC] Aldric: hello\"}");
        Assert.NotNull(package);
        Assert.Equal("ooc", package.Channel);
        Assert.Equal("Aldric", package.Talker);
        Assert.Equal("[OOC] Aldric: hello", package.Text);
        Assert.Null(CommChannelProtocol.Decode("Room.Info {\"name\":\"A field\"}"));
        Assert.Null(CommChannelProtocol.Decode("Comm.Channel.Text not json"));
        Assert.Null(CommChannelProtocol.Decode("Comm.Channel.Text {\"channel\":\"ooc\"}"));
    }

    [Theory]
    [InlineData("SMAUG 1.4a", "smaug")]
    [InlineData("SWR 1.0 (SMAUG derivative)", "swr")]
    [InlineData("ROM 2.4b6", "rom")]
    [InlineData("CircleMUD 3.1", "circle")]
    [InlineData("DikuMUD", "diku")]
    [InlineData("LPMud", "lp")]
    [InlineData("custom in house engine", "generic")]
    [InlineData("", "generic")]
    public void TheDirectorysCodebaseChoosesTheFamily(string codebase, string family)
        => Assert.Equal(family, ChannelFamilies.Match(codebase));

    [Fact]
    public void AWorldsOwnRulesReplaceTheFamilyDefaults()
    {
        var own = new List<ChannelRule> { new("house", "House: (?<text>.*)$", "house") };
        var rules = ChannelFamilies.For(own, "SMAUG 1.4a");
        Assert.Equal(1, rules.Count);
        Assert.Equal("house", Classify(rules, "House: the door is open")!.Channel);
        Assert.Null(Classify(rules, "[OOC] Aldric: hello"));
        Assert.True(ChannelFamilies.For([], "SMAUG 1.4a").Count > 1);
    }

    [Fact]
    public void AnUnreadablePatternIsIgnoredRatherThanFatal()
    {
        var rules = new ChannelRuleSet([new("broken", "(?<speaker>[A-Za-z"), new("ooc", "\\[OOC\\] (?<speaker>[A-Za-z]+): (?<text>.*)$", "ooc")]);
        Assert.Equal(1, rules.Count);
        Assert.Equal("ooc", Classify(rules, "[OOC] Aldric: hello")!.Channel);
    }

    [Fact]
    public void AProfileRejectsChannelRulesItCouldNotUse()
    {
        var profile = new ConnectionProfile { Name = "Test", Host = "example.org", ChannelRules = [new("ooc", "(?<speaker>[A-Za-z")] };
        Assert.Throws<ArgumentException>(profile.Validate);
        new ConnectionProfile { Name = "Test", Host = "example.org", Codebase = "SMAUG 1.4a", ChannelRules = [new("ooc", "^\\[OOC\\] ", "ooc")] }.Validate();
    }
}
