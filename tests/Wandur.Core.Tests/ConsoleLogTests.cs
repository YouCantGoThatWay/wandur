using System.Text;
using System.Text.RegularExpressions;
using Wandur.Core.Diagnostics;
using Wandur.Core.Protocol;

namespace Wandur.Core.Tests;

public sealed class ConsoleLogTests
{
    [Fact]
    public void RingKeepsTheLatestTwoThousandEntries()
    {
        var log = new ConsoleLog();
        for (var i = 1; i <= 2100; i++) log.Append(ConsoleEntryKind.Received, $"line {i}\r\n", false);
        Assert.Equal(ConsoleLog.MaximumEntries, log.Count);
        var entries = log.Snapshot();
        Assert.Equal(101, entries[0].Sequence); Assert.Equal("line 101\r\n", entries[0].Text);
        Assert.Equal(2100, entries[^1].Sequence);
    }

    [Fact]
    public void RingKeepsAtMostHalfAMebibyteOfTextButNeverDropsTheNewestEntry()
    {
        var log = new ConsoleLog();
        for (var i = 0; i < 6; i++) log.Append(ConsoleEntryKind.Received, new string((char)('a' + i), 100_000), false);
        Assert.Equal(5, log.Count);
        Assert.True(log.Characters <= ConsoleLog.MaximumCharacters);
        Assert.Equal('b', log.Snapshot()[0].Text[0]);
        log.Append(ConsoleEntryKind.Received, new string('z', ConsoleLog.MaximumCharacters + 1), false);
        Assert.Equal(1, log.Count);
    }

    [Fact]
    public void ControlCharactersAreMadeVisible()
    {
        Assert.Equal("\u241B[32mgreen\u241B[0m\u240D\u240A", ConsoleLog.Visible("\u001B[32mgreen\u001B[0m\r\n"));
        Assert.Equal("a\u240A\n    b", ConsoleLog.Visible("a\nb", indent: 4));
        Assert.Equal("^G^I^?^@", ConsoleLog.Visible("\a\t\u007F\0"));
        Assert.Equal("plain text é", ConsoleLog.Visible("plain text é"));
    }

    [Fact]
    public void RenderShowsTimestampMarkerAndAlignedContinuationLines()
    {
        var at = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
        var sent = new ConsoleEntry(1, at, ConsoleEntryKind.Sent, "look").Render();
        Assert.Matches(new Regex(@"^\d\d:\d\d:\d\d\.\d\d\d >> look$"), sent);
        var received = new ConsoleEntry(2, at, ConsoleEntryKind.Received, "one\r\ntwo\r\n").Render();
        var lines = received.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.EndsWith(" << one\u240D\u240A", lines[0]);
        Assert.Equal(new string(' ', "HH:mm:ss.fff << ".Length) + "two\u240D\u240A", lines[1]);
        Assert.EndsWith(" [script] hello", new ConsoleEntry(3, at, ConsoleEntryKind.Script, "hello").Render());
        Assert.EndsWith(" [private]", new ConsoleEntry(4, at, ConsoleEntryKind.Private, "").Render());
    }

    [Fact]
    public void HiddenChunksCollapseIntoOnePrivateMarker()
    {
        var log = new ConsoleLog();
        for (var i = 0; i < 5; i++) log.Append(ConsoleEntryKind.Received, "Password: hunter2\r\n", hidden: true);
        log.Append(ConsoleEntryKind.Sent, "hunter2", hidden: true);
        var only = Assert.Single(log.Snapshot());
        Assert.Equal(ConsoleEntryKind.Private, only.Kind);
        Assert.Equal("", only.Text);
        Assert.EndsWith(ConsoleEntry.PrivateMarker, only.Render());
        log.Append(ConsoleEntryKind.Received, "Welcome back.\r\n", false);
        log.Append(ConsoleEntryKind.Received, "secret again", hidden: true);
        log.Append(ConsoleEntryKind.Script, "still secret", hidden: true);
        Assert.Equal(new[] { ConsoleEntryKind.Private, ConsoleEntryKind.Received, ConsoleEntryKind.Private }, log.Snapshot().Select(e => e.Kind));
        Assert.DoesNotContain("hunter2", log.Render());
        Assert.DoesNotContain("secret", log.Render());
    }

    [Fact]
    public void RememberedSecretsAreMaskedTheWayProtocolDiagnosticsMaskThem()
    {
        string[] secrets = ["hunter2", "hunter"];
        var log = new ConsoleLog();
        log.Append(ConsoleEntryKind.Received, "Your password hunter2 is weak.\r\n", false, secrets);
        var protocol = ProtocolDiagnosticFormatter.Format(201, Encoding.UTF8.GetBytes("Char.Info {\"note\":\"hunter2\"}"), false, secrets);
        var token = Regex.Match(protocol.Body, @"\[\w+\]").Value;
        Assert.Equal($"Your password {token} is weak.\r\n", log.Snapshot()[0].Text);
        Assert.Equal("say \"hi\" \\ é \u001B[1m" + token, ConsoleLog.Mask("say \"hi\" \\ é \u001B[1mhunter2", secrets));
        var untouched = "nothing to hide";
        Assert.Same(untouched, ConsoleLog.Mask(untouched, secrets));
        Assert.Same(untouched, ConsoleLog.Mask(untouched, null));
    }

    [Fact]
    public void ClearEmptiesTheRingAndEveryChangeRaisesTheEvent()
    {
        var log = new ConsoleLog();
        var changes = 0;
        log.Changed += () => changes++;
        log.Append(ConsoleEntryKind.Received, "a", false);
        log.Append(ConsoleEntryKind.Received, "", false);
        Assert.Equal(1, changes);
        log.Clear();
        Assert.Equal(0, log.Count); Assert.Equal(0, log.Characters); Assert.Equal("", log.Render());
        Assert.Equal(2, changes);
        log.Append(ConsoleEntryKind.Received, "b", hidden: true);
        Assert.Equal(ConsoleEntryKind.Private, Assert.Single(log.Snapshot()).Kind);
    }
}
