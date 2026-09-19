using Wandur.Core.Input;
using Wandur.Core.Sessions;

namespace Wandur.Core.Tests;

public sealed class CompletionTests
{
    [Fact]
    public void RecencyOutranksFrequencyThenFrequencyThenLength()
    {
        var trie = new CompletionTrie();
        trie.Insert("wombat", 1); trie.Insert("wombat", 1); trie.Insert("wombat", 1);
        trie.Insert("womprat", 2);
        trie.Insert("women", 3); trie.Insert("womenfolk", 3); trie.Insert("womenfolk", 3);
        trie.Insert("wombs", 3);
        // Sequence 3 first: womenfolk (twice) beats the single sightings, which are then ordered by length.
        Assert.Equal(new[] { "womenfolk", "wombs", "women", "womprat", "wombat" }, trie.Suggest("wom", 10));
        Assert.Equal(new[] { "womenfolk", "wombs" }, trie.Suggest("wom", 2));
    }

    [Fact]
    public void KeysAreCaseInsensitiveAndTheLastSeenCasingIsKept()
    {
        var trie = new CompletionTrie();
        trie.Insert("Womprat", 1);
        Assert.Equal(new[] { "Womprat" }, trie.Suggest("wom"));
        Assert.Equal(new[] { "Womprat" }, trie.Suggest("WOM"));
        trie.Insert("womprat", 2);
        Assert.Equal(new[] { "womprat" }, trie.Suggest("Wom"));
        Assert.Equal(1, trie.Count);
    }

    [Fact]
    public void ExactMatchIsExcludedAndShortPrefixesGiveNothing()
    {
        var trie = new CompletionTrie();
        trie.Insert("look", 1); trie.Insert("looking", 2);
        Assert.Equal(new[] { "looking" }, trie.Suggest("look"));
        Assert.Empty(trie.Suggest("looking"));
        Assert.Empty(trie.Suggest("l"));
        Assert.Empty(trie.Suggest(""));
        Assert.Empty(trie.Suggest(null));
        Assert.Empty(trie.Suggest("zz"));
    }

    [Fact]
    public void EvictionKeepsTheMostRecentEntries()
    {
        var trie = new CompletionTrie(maxEntries: 100);
        for (var i = 0; i < 150; i++) trie.Insert($"word{i:000}", i);
        Assert.True(trie.Count <= 100, $"{trie.Count} entries after the sweep");
        Assert.Empty(trie.Suggest("word00"));
        Assert.Equal("word149", trie.Suggest("word", 1)[0]);
        Assert.Contains("word140", trie.Words());
        Assert.DoesNotContain("word010", trie.Words());
    }

    [Fact]
    public void NodeBudgetAlsoTriggersASweep()
    {
        var trie = new CompletionTrie(maxEntries: 1000, maxNodes: 20);
        for (var i = 0; i < 40; i++) trie.Insert($"abc{i:00}", i);
        // A sweep may overshoot its budget by the last word it re-inserts, never by more.
        Assert.True(trie.NodeCount <= 25, $"{trie.NodeCount} nodes");
        Assert.True(trie.Count < 40, $"{trie.Count} entries");
        Assert.Equal("abc39", trie.Suggest("abc", 1)[0]);
    }

    [Fact]
    public void TokensAreLettersDigitsApostrophesAndHyphensOfThreeToThirtyTwoCharacters()
    {
        var tokens = CompletionLearner.Tokens("A Vicious Womprat's scurry-past, 'quoted' 1234 ab " + new string('x', 33) + " x1 don't -dash-").ToList();
        Assert.Equal(new[] { "Vicious", "Womprat's", "scurry-past", "quoted", "don't", "dash" }, tokens);
    }

    [Fact]
    public void ObserveLearnsCompleteLinesWithoutEscapeSequencesAndSkipsRejectedOnes()
    {
        var learner = new CompletionLearner();
        learner.Observe("\u001b[32mA Vicious Wom");
        Assert.Empty(learner.Words.Suggest("wom"));
        learner.Observe("prat\u001b[0m scurries past.\r\nsecret passphrase here\r\nA bantha ", line => line.Contains("passphrase"));
        Assert.Equal(new[] { "Womprat" }, learner.Words.Suggest("wom"));
        Assert.Empty(learner.Words.Suggest("passph"));
        Assert.Empty(learner.Words.Suggest("ban"));
        learner.Observe("wanders by.\n");
        Assert.Equal(new[] { "bantha" }, learner.Words.Suggest("ban"));
        Assert.Equal(2, learner.Sequence);
    }

    [Fact]
    public void LineModeWinsOverWordModeAndWordModeNeedsTwoCharacters()
    {
        var history = new CommandHistory();
        history.Add("look");
        history.Add("look womprat");
        var learner = new CompletionLearner();
        learner.Learn("A Vicious Womprat scurries past.");
        learner.Learn("look womprat");
        Assert.Equal(" womprat", CompletionSuggester.Suggest("look", 4, false, history.Entries, learner.Words));
        Assert.Equal("k womprat", CompletionSuggester.Suggest("loo", 3, false, history.Entries, learner.Words));
        Assert.Equal("k womprat", CompletionSuggester.Suggest("LOO", 3, false, history.Entries, learner.Words));
        Assert.Equal("prat", CompletionSuggester.Suggest("kill wom", 8, false, history.Entries, learner.Words));
        Assert.Null(CompletionSuggester.Suggest("kill w", 6, false, history.Entries, learner.Words));
        Assert.Null(CompletionSuggester.Suggest("look womprat", 12, false, history.Entries, learner.Words));
    }

    [Fact]
    public void NothingWhilePrivateEmptyOrWithTheCaretAwayFromTheEnd()
    {
        var history = new CommandHistory();
        history.Add("look womprat");
        var learner = new CompletionLearner();
        learner.Learn("A Vicious Womprat scurries past.");
        Assert.Null(CompletionSuggester.Suggest("look", 4, true, history.Entries, learner.Words));
        Assert.Null(CompletionSuggester.Suggest("", 0, false, history.Entries, learner.Words));
        Assert.Null(CompletionSuggester.Suggest(null, 0, false, history.Entries, learner.Words));
        Assert.Null(CompletionSuggester.Suggest("look", 2, false, history.Entries, learner.Words));
        Assert.Null(CompletionSuggester.Suggest("wom", 1, false, history.Entries, learner.Words));
    }

    [Fact]
    public void HistoryExposesPublicEntriesOnly()
    {
        var history = new CommandHistory();
        history.Add("look");
        history.Add("hunter2", isPrivate: true);
        history.Add("north");
        Assert.Equal(new[] { "look", "north" }, history.Entries);
    }
}
