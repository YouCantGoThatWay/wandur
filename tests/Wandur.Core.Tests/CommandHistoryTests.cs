using Wandur.Core.Sessions;

namespace Wandur.Core.Tests;

public class CommandHistoryTests
{
    [Fact]
    public void HistoryRestoresUnsentDraftAfterNavigatingBackDown()
    {
        var history = new CommandHistory();
        history.Add("north");
        history.Add("look");
        Assert.Equal("look", history.Previous("say unfinished"));
        Assert.Equal("north", history.Previous("look"));
        Assert.Equal("look", history.Next());
        Assert.Equal("say unfinished", history.Next());
    }

    [Fact]
    public void PrivateCommandsAndEmptyCommandsNeverEnterHistory()
    {
        var history = new CommandHistory();
        history.Add("look");
        history.Add("secret", isPrivate: true);
        history.Add("");
        Assert.Equal("look", history.Previous(""));
        Assert.Equal("look", history.Previous(""));
    }
}
