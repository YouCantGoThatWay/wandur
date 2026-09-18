using Wandur.Core.Scripting;

namespace Wandur.Core.Tests;

public sealed class ScriptStoreTests
{
    [Fact]
    public void ScriptsRoundTripAtomicallyAndWorldKeysCannotEscapeTheDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wandur-scripts-" + Guid.NewGuid());
        try
        {
            var store = new WorldScriptStore(directory);
            Assert.Null(store.Load("host:4000"));
            store.Save("../../outside", "mud.echo('first');");
            store.Save("../../outside", "mud.echo('replaced');");
            store.Save("host:4000", "mud.echo('other');");
            Assert.Equal("mud.echo('replaced');", store.Load("../../outside"));
            Assert.Equal("mud.echo('other');", store.Load("host:4000"));
            Assert.Equal(2, Directory.GetFiles(directory).Length);
            Assert.All(Directory.GetFiles(directory), path => Assert.Equal(".js", Path.GetExtension(path)));
            Assert.Throws<ArgumentException>(() => store.Save("host:4000", new string('x', 262145)));
            Assert.Equal("mud.echo('other');", store.Load("host:4000"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void NewlinesInsideControlPayloadsDoNotEmitOrDuplicateLines()
    {
        var lines = new ScriptLineBuffer();
        Assert.Empty(lines.Feed("\u001b]0;title\n"));
        Assert.Equal(new[] { "Visible" }, lines.Feed("rest of title\u0007Visible\n"));
        Assert.Empty(lines.Feed("\u001bPpayload\nmore payload\u001b\\"));
        Assert.Empty(lines.Feed("\u001b[31\n"));
        Assert.Equal(new[] { "Next" }, lines.Feed("mNext\n"));
    }

    [Fact]
    public void FragmentedAnsiLinesAreDeliveredOnceWithoutPromptsOrControlCodes()
    {
        var lines = new ScriptLineBuffer();
        Assert.Empty(lines.Feed("\u001b[3"));
        Assert.Empty(lines.Feed("1mHello"));
        Assert.Equal(new[] { "Hello world", "Second" }, lines.Feed(" world\u001b[0m\r\nSecond\r\nHP: "));
        Assert.Empty(lines.Feed("100"));
        Assert.Equal(new[] { "HP: 100" }, lines.Feed("\n"));
        lines.Feed("Private partial"); lines.Clear();
        Assert.Equal(new[] { "Public" }, lines.Feed("Public\n"));
    }
}
