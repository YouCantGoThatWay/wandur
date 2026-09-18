using Wandur.Core.Mapping;

namespace Wandur.Core.Tests;

public sealed class TextRoomObserverTests
{
    [Fact]
    public void FragmentedColoredRoomWaitsForCompletedExitsAndIgnoresOccupants()
    {
        var parser = new TextRoomObserver();
        Assert.Empty(parser.Feed("\u001b[1mThe Hall\u001b[0m\r\nStone arches rise above you.\r\nA guard is standing here.\r\n\r\nExits: nor"));
        var room = Assert.Single(parser.Feed("th east\r\n> "));
        Assert.Equal("The Hall", room.Name);
        Assert.Equal("Stone arches rise above you.", room.Description);
        Assert.Equal(new[] { "east", "north" }, room.Exits.Keys.Order());
        Assert.Empty(parser.Feed("Somebody says: hello!\r\n"));
    }

    [Fact]
    public void LotjDirectionalExitLinesAreOneObservation()
    {
        var parser = new TextRoomObserver();
        Assert.Empty(parser.Feed("\u001b[1mThe Cockpit | Academy Transport Shuttle\u001b[0m\nPanels glow in the dark.\n\nObvious exits:\nNorth - A passage\n"));
        var room = Assert.Single(parser.Feed("South - The cabin\n\n*((==HP==((||||\n"));
        Assert.Equal("The Cockpit | Academy Transport Shuttle", room.Name);
        Assert.Equal(2, room.Exits.Count);
    }

    [Theory]
    [InlineData("HP: 100 MV: 80> ")]
    [InlineData("> ")]
    public void MultilineExitsFinishAtARecognizableUnterminatedPrompt(string prompt)
    {
        var parser = new TextRoomObserver();
        var room = Assert.Single(parser.Feed("Hall\nStone arches.\nExits:\nNorth - The stairs\nSouth - The door\n" + prompt));
        Assert.Equal("Hall", room.Name);
        Assert.Equal(2, room.Exits.Count);
    }

    [Fact]
    public void LoginAndChatDoNotCreateRoomsAndFailedMovementIsDetected()
    {
        var parser = new TextRoomObserver();
        Assert.Empty(parser.Feed("Welcome!\nUsername: \n(P)assword:\nSomeone says: Exits: north\n"));
        Assert.True(TextRoomObserver.IsMovementFailure("You can't go that way."));
        Assert.True(TextRoomObserver.IsMovementFailure("The door is closed."));
        Assert.False(TextRoomObserver.IsMovementFailure("You open the door."));
    }
}
