using Wandur.Core.Terminal;

namespace Wandur.Core.Tests;

/// <summary>MSDP hands scripts raw server strings that still carry SMAUG and SWR style color codes.
/// The parser turns them into the same styled runs the transcript produces for ANSI, so a panel and
/// the transcript agree on every color.</summary>
public sealed class MudColorCodesTests
{
    private static (string Text, int? Foreground, int? Background)[] Shape(IReadOnlyList<TextRun> runs)
        => runs.Select(run => (run.Text, run.Style.ForegroundIndex, run.Style.BackgroundIndex)).ToArray();

    [Fact]
    public void PlainTextIsOneDefaultRun()
    {
        var run = Assert.Single(MudColorCodes.Parse("A plain title"));
        Assert.Equal("A plain title", run.Text);
        Assert.Equal(new TextStyle(), run.Style);
        Assert.False(MudColorCodes.HasCodes("A plain title"));
        Assert.Empty(MudColorCodes.Parse(""));
    }

    [Theory]
    [InlineData('x', 0)] [InlineData('r', 1)] [InlineData('g', 2)] [InlineData('O', 3)]
    [InlineData('b', 4)] [InlineData('p', 5)] [InlineData('c', 6)] [InlineData('w', 7)]
    [InlineData('z', 8)] [InlineData('R', 9)] [InlineData('G', 10)] [InlineData('Y', 11)]
    [InlineData('B', 12)] [InlineData('P', 13)] [InlineData('C', 14)] [InlineData('W', 15)]
    public void EverySmaugLetterMapsToTheTranscriptPaletteEntry(char letter, int index)
    {
        var run = Assert.Single(MudColorCodes.Parse("&" + letter + "text"));
        Assert.Equal("text", run.Text);
        Assert.Equal(index, run.Style.ForegroundIndex);
        Assert.Equal(AnsiPalette.Defaults[index], run.Style.Foreground);
        var background = Assert.Single(MudColorCodes.Parse("^" + letter + "text"));
        Assert.Equal(index, background.Style.BackgroundIndex);
        Assert.Equal(AnsiPalette.Defaults[index], background.Style.Background);
        Assert.Null(background.Style.ForegroundIndex);
    }

    [Fact]
    public void ResetLettersRestoreTheDefaultForegroundAndBackground()
    {
        Assert.Equal([("Red", 1, 4), (" plain", null, null)], Shape(MudColorCodes.Parse("&r^bRed&D plain")));
        Assert.Equal([("Red", 9, null), (" plain", null, null)], Shape(MudColorCodes.Parse("&RRed&d plain")));
    }

    [Fact]
    public void ThreeDigitCodesUseTheXterm256Table()
    {
        var runs = MudColorCodes.Parse("&228A Vicious Womprat&D");
        var run = Assert.Single(runs);
        Assert.Equal("A Vicious Womprat", run.Text);
        Assert.Null(run.Style.ForegroundIndex);
        Assert.Equal(AnsiPalette.Indexed(228), run.Style.Foreground);
        Assert.Equal("#FFFF87", run.Style.Foreground);
        // Indexes below 16 keep their palette identity so themes recolor them.
        var low = Assert.Single(MudColorCodes.Parse("&009bright red"));
        Assert.Equal(9, low.Style.ForegroundIndex);
        var back = Assert.Single(MudColorCodes.Parse("^017deep blue"));
        Assert.Equal(AnsiPalette.Indexed(17), back.Style.Background);
        Assert.Null(back.Style.Foreground);
    }

    [Theory]
    [InlineData("&&", "&")]
    [InlineData("^^", "^")]
    [InlineData("Tom && Jerry", "Tom & Jerry")]
    [InlineData("&", "&")]
    [InlineData("&q", "&q")]
    [InlineData("&1", "&1")]
    [InlineData("&12x", "&12x")]
    [InlineData("&999", "&999")]
    [InlineData("&256", "&256")]
    [InlineData("^", "^")]
    [InlineData("^-", "^-")]
    [InlineData("50% && rising", "50% & rising")]
    public void DoubledMarkersAreLiteralAndUnknownCodesStayAsText(string text, string expected)
    {
        var run = Assert.Single(MudColorCodes.Parse(text));
        Assert.Equal(expected, run.Text);
        Assert.Equal(new TextStyle(), run.Style);
        Assert.Equal(expected, MudColorCodes.Strip(text));
    }

    [Fact]
    public void CodesNeverLeakPastTheEndOfAString()
    {
        // The last run is still red, but every string starts from the default style.
        Assert.Equal([("still red", 9, null)], Shape(MudColorCodes.Parse("&Rstill red")));
        Assert.Equal([("next", null, null)], Shape(MudColorCodes.Parse("next")));
        // A code with nothing after it produces no empty run.
        Assert.Equal([("a", 9, null)], Shape(MudColorCodes.Parse("&Ra&G")));
        Assert.Empty(MudColorCodes.Parse("&R&G&D"));
    }

    [Fact]
    public void AdjacentCodesCollapseAndTheLastOneWins()
        => Assert.Equal([("green", 10, null)], Shape(MudColorCodes.Parse("&R&Ggreen")));

    [Fact]
    public void AnsiSgrSequencesAreAppliedLikeTheTranscriptAndOtherEscapesAreStripped()
    {
        Assert.Equal([("red", 1, null), (" plain", null, null)], Shape(MudColorCodes.Parse("\u001b[31mred\u001b[0m plain")));
        Assert.Equal([("bright", 9, 4)], Shape(MudColorCodes.Parse("\u001b[1;31;44mbright")));
        Assert.Equal([("bright", 9, null), ("dim", 1, null)], Shape(MudColorCodes.Parse("\u001b[91mbright\u001b[22;31mdim")));
        var extended = MudColorCodes.Parse("\u001b[38;5;228mx\u001b[48;2;1;2;3my\u001b[39;49mz");
        Assert.Equal(AnsiPalette.Indexed(228), extended[0].Style.Foreground);
        Assert.Equal("#010203", extended[1].Style.Background);
        Assert.Equal(new TextStyle(), extended[2].Style);
        Assert.True(extended[0].Style.Bold == false);
        // Cursor movement and a bare escape carry no text at all.
        Assert.Equal("clean", MudColorCodes.Strip("\u001b[2Jcle\u001ban\u001b[K"));
        var styled = Assert.Single(MudColorCodes.Parse("\u001b[1;4;3mstyled"));
        Assert.True(styled.Style.Bold); Assert.True(styled.Style.Italic); Assert.True(styled.Style.Underline);
    }

    [Fact]
    public void MixedCodesAndEscapesInOneStringAgree()
        => Assert.Equal([("A", 9, null), ("B", 1, null), ("C", null, null), ("D", 15, null)],
            Shape(MudColorCodes.Parse("&RA\u001b[31mB\u001b[0mC&WD")));

    [Fact]
    public void StripRemovesEveryCodeAndKeepsLiterals()
    {
        Assert.Equal("A Vicious Womprat", MudColorCodes.Strip("&228A Vicious Womprat&D"));
        Assert.Equal("Tom & Jerry ^ up", MudColorCodes.Strip("&RTom && &GJerry ^^ up&D"));
        Assert.Equal("plain", MudColorCodes.Strip("plain"));
        Assert.Equal("", MudColorCodes.Strip(""));
        Assert.True(MudColorCodes.HasCodes("&Rx"));
        Assert.True(MudColorCodes.HasCodes("\u001b[31m"));
        Assert.False(MudColorCodes.HasCodes("50% done"));
    }

    [Fact]
    public void IndexedPaletteMatchesTheTerminalsTable()
    {
        Assert.Equal(AnsiPalette.Defaults[3], AnsiPalette.Indexed(3));
        Assert.Equal("#000000", AnsiPalette.Indexed(16));
        Assert.Equal("#FFFFFF", AnsiPalette.Indexed(231));
        Assert.Equal("#080808", AnsiPalette.Indexed(232));
        Assert.Equal("#EEEEEE", AnsiPalette.Indexed(255));
        Assert.Equal(AnsiPalette.Indexed(255), AnsiPalette.Indexed(999));
        Assert.Equal(AnsiPalette.Indexed(0), AnsiPalette.Indexed(-1));
        // The transcript resolves the same index to the same hex.
        var terminal = new AnsiTerminal();
        terminal.Append("\u001b[38;5;228mx");
        Assert.Equal(AnsiPalette.Indexed(228), Assert.Single(terminal.Lines[0].Runs).Style.Foreground);
    }
}
