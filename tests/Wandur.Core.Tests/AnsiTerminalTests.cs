using Wandur.Core.Terminal;

namespace Wandur.Core.Tests;

public class AnsiTerminalTests
{
    [Theory]
    [InlineData("\u001b[1;30m")]
    [InlineData("\u001b[30;1m")]
    [InlineData("\u001b[30m\u001b[1m")]
    [InlineData("\u001b[1m\u001b[30m")]
    public void LegacyBoldBlackUsesBrightGrayRegardlessOfSequenceOrder(string sequence)
    {
        var terminal = new AnsiTerminal();
        // Fragmented socket reads must not change the color result.
        foreach (var ch in sequence) terminal.Append(ch.ToString());
        terminal.Append("Readable text");
        var run = Assert.Single(terminal.Lines[0].Runs);
        Assert.Equal("#808080", run.Style.Foreground);
        Assert.True(run.Style.Bold);
        terminal.Append("\u001b[22m normal\u001b[0m default");
        Assert.Equal("#16161C", terminal.Lines[0].Runs[1].Style.Foreground);
        Assert.False(terminal.Lines[0].Runs[1].Style.Bold);
        Assert.Null(terminal.Lines[0].Runs[2].Style.Foreground);
    }

    [Fact]
    public void BoldBrightnessDoesNotAlterBackgroundsOrExplicitExtendedColors()
    {
        var terminal = new AnsiTerminal();
        terminal.Append("\u001b[1;30;40mA\u001b[38;5;0mB\u001b[38;2;22;22;28mC\u001b[90mD\u001b[22mE\u001b[39mF");
        var runs = terminal.Lines[0].Runs;
        Assert.Equal("#808080", runs[0].Style.Foreground);
        Assert.Equal("#16161C", runs[0].Style.Background);
        Assert.Equal("#16161C", runs.Single(r => r.Text.Contains('B')).Style.Foreground);
        Assert.Equal("#16161C", runs.Single(r => r.Text.Contains('C')).Style.Foreground);
        Assert.Equal("#808080", runs.Single(r => r.Text.Contains('D')).Style.Foreground);
        Assert.Equal("#808080", runs.Single(r => r.Text.Contains('E')).Style.Foreground);
        Assert.Null(runs.Single(r => r.Text.Contains('F')).Style.Foreground);
    }

    [Fact]
    public void LocalEchoDoesNotConsumeAnUnfinishedServerEscapeSequence()
    {
        var terminal = new AnsiTerminal();
        terminal.Append("\u001b[3");
        terminal.AppendLocalText("look\n");
        terminal.Append("1mred");
        Assert.Equal("look\nred", terminal.PlainText);
        Assert.Equal("#CD3131", terminal.Lines[1].Runs[0].Style.Foreground);
    }

    [Fact]
    public void SplitColorsStyleTextWithoutPrintingEscapeCodes()
    {
        var terminal = new AnsiTerminal();
        terminal.Append("Stone \u001b[3");
        terminal.Append("1;1mdoor\u001b[0m.\r\n> ");
        Assert.Equal("Stone door.\n> ", terminal.PlainText);
        var run = terminal.Lines[0].Runs.Single(r => r.Text == "door");
        Assert.Equal("#F14C4C", run.Style.Foreground);
        Assert.True(run.Style.Bold);
        Assert.Null(terminal.Lines[0].Runs.Last().Style.Foreground);
    }

    [Fact]
    public void SupportsTrueColorAndIndexedColor()
    {
        var terminal = new AnsiTerminal();
        terminal.Append("\u001b[38;2;12;34;56mA\u001b[38;5;196mB");
        Assert.Equal("#0C2238", terminal.Lines[0].Runs[0].Style.Foreground);
        Assert.Equal("#FF0000", terminal.Lines[0].Runs[1].Style.Foreground);
    }

    [Fact]
    public void CarriageReturnOverwritesAndEraseLineRemovesTail()
    {
        var terminal = new AnsiTerminal();
        terminal.Append("Loading 100%\rReady\u001b[K\n");
        Assert.Equal("Ready\n", terminal.PlainText);
    }

    [Fact]
    public void BackspaceMovesCursorBeforeReplacement()
    {
        var terminal = new AnsiTerminal();
        terminal.Append("helo\blo");
        Assert.Equal("hello", terminal.PlainText);
    }

    [Fact]
    public void OscTitlesAndLinksAreNotExecutedOrShown()
    {
        var terminal = new AnsiTerminal();
        terminal.Append("\u001b]0;untrusted title\u0007Hi \u001b]8;;https://example.com\u001b\\there\u001b]8;;\u001b\\");
        Assert.Equal("Hi there", terminal.PlainText);
    }

    [Fact]
    public void TranscriptEvictsOldLinesAndBoundsSingleLineLength()
    {
        var terminal = new AnsiTerminal(3);
        terminal.Append("one\ntwo\nthree\nfour");
        Assert.Equal("two\nthree\nfour", terminal.PlainText);
        terminal.Append(new string('x', 100_000));
        Assert.True(terminal.Lines.Last().Text.Length <= 4096);
    }
}
