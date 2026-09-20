using Wandur.Core.Mapping;

namespace Wandur.Core.Tests;

public sealed class RoomSearchTests
{
    /// <summary>An ordinary room tracked from server or text evidence, never manually edited.</summary>
    private static MapRoom Seen(string id, string name, string description) => new(id, name, description, null, 0, 0, 0, false);
    /// <summary>A room whose displayed label was overridden by hand; the server's own text moved to the Observed* fields.</summary>
    private static MapRoom Edited(string id, string label, string? observedName, string? observedDescription) =>
        new(id, label, "", null, 0, 0, 0, false) { IsManuallyEdited = true, ObservedName = observedName, ObservedDescription = observedDescription };

    [Fact]
    public void EveryTermMustMatchTheObservedNameOrDescription()
    {
        var rooms = new[]
        {
            Seen("a", "Ancient Temple", "A crumbling temple beside a quiet garden."),
            Seen("b", "Temple Gate", "Guarded stone gate."),
            Seen("c", "Garden Path", "A gravel path through a garden."),
        };
        var matches = RoomSearch.Search(rooms, "temple garden");
        Assert.Equal(["a"], matches.Select(r => r.Id));
    }

    [Fact]
    public void AQuotedPhraseMatchesOnlyAsAContiguousRun()
    {
        var rooms = new[]
        {
            Seen("a", "Ancient Temple", "A crumbling temple beside a quiet garden."),
            Seen("b", "Temple of the Ancient Kings", "Old stonework."),
        };
        Assert.Equal(["a"], RoomSearch.Search(rooms, "\"ancient temple\"").Select(r => r.Id));
        Assert.Equal(["a", "b"], RoomSearch.Search(rooms, "ancient temple").Select(r => r.Id).Order());
    }

    [Fact]
    public void MatchingIsCaseInsensitive()
    {
        var rooms = new[] { Seen("a", "Ancient Temple", "A crumbling TEMPLE beside a quiet garden.") };
        Assert.Equal(["a"], RoomSearch.Search(rooms, "TEMPLE").Select(r => r.Id));
        Assert.Equal(["a"], RoomSearch.Search(rooms, "temple").Select(r => r.Id));
    }

    [Fact]
    public void RoomsWithoutAnyObservedTextAreNeverMatched()
    {
        var rooms = new[]
        {
            // A manually added room the server has never confirmed: a hand-typed label is not evidence.
            Edited("a", "Temple (guess)", null, null),
            // An ordinary tracked room: its plain name/description are what was observed.
            Seen("b", "Temple", ""),
            // An edited room whose display label no longer says "temple", but the server's own text still does.
            Edited("c", "The Old Shrine", "Sunken Temple", "A temple in ruins."),
        };
        Assert.Equal(["b", "c"], RoomSearch.Search(rooms, "temple").Select(r => r.Id).Order());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyQueryMatchesNothing(string query) => Assert.Empty(RoomSearch.Search([Seen("a", "Temple", "A temple.")], query));
}
