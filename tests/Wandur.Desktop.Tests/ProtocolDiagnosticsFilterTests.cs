using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Settings;
using Wandur.Desktop;
using Wandur.Desktop.ViewModels;
using Wandur.Desktop.Views;

namespace Wandur.Desktop.Tests;

/// <summary>The Messages tab filters by the kinds seen (chips) and by text; both are a view over the same retained entries.</summary>
[Collection(UiLanguageCollection.Name)]
public sealed class ProtocolDiagnosticsFilterTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
    private static byte[] Gmcp(string text) => Encoding.UTF8.GetBytes(text);
    private static byte[] Msdp(params (string Name, string Value)[] fields) =>
        Encoding.UTF8.GetBytes(string.Concat(fields.Select(f => "\u0001" + f.Name + "\u0002" + f.Value)));
    private static readonly byte[] Mssp = Encoding.UTF8.GetBytes("\u0001NAME\u0002Fixture World\u0001PLAYERS\u00023");

    private static ProtocolDiagnosticsViewModel Seeded()
    {
        var model = new ProtocolDiagnosticsViewModel();
        model.Append(Now, 201, Gmcp("Char.Vitals {\"hp\":42,\"maxhp\":100}"));
        model.Append(Now, 69, Msdp(("OPPONENTHEALTH", "80"), ("HEALTH", "42")));
        model.Append(Now, 70, Mssp);
        model.Append(Now, 201, Gmcp("Char.Vitals {\"hp\":40,\"maxhp\":100}"));
        model.Append(Now, 201, Gmcp("Room.Info {\"name\":\"The Cockpit\"}"));
        return model;
    }

    [Fact]
    public void KindsComeFromTheGmcpPackageEveryMsdpVariableAndTheOptionNameWithCounts()
    {
        var model = Seeded();
        Assert.Equal(["Char.Vitals", "Room.Info", "HEALTH", "OPPONENTHEALTH", "MSSP"], model.Kinds.Select(k => k.Name));
        Assert.Equal(["GMCP", "GMCP", "MSDP", "MSDP", "MSSP"], model.Kinds.Select(k => k.Protocol));
        Assert.Equal([2, 1, 1, 1, 1], model.Kinds.Select(k => k.Count));
        Assert.Equal("Char.Vitals (2)", model.Kinds[0].Label);
        Assert.Equal(["OPPONENTHEALTH", "HEALTH"], model.Entries[1].Kinds);
        Assert.Equal("MSSP", model.Entries[2].Protocol);
        Assert.Equal(["MSSP"], model.Entries[2].Kinds);
        model.Append(Now, 201, null);
        Assert.Empty(model.Entries[^1].Kinds);
        Assert.Equal(5, model.Kinds.Count);
        Assert.Equal(model.Entries, model.Visible);
        Assert.False(model.IsFiltering);
    }

    [Fact]
    public void MsdpKindsSurviveARedactedVariableAndAMalformedPayload()
    {
        var model = new ProtocolDiagnosticsViewModel();
        model.Append(Now, 69, Msdp(("HEALTH", "42"), ("PASSWORD", "hidden-secret")));
        Assert.Equal(["HEALTH", "PASSWORD"], model.Entries[0].Kinds);
        Assert.DoesNotContain("hidden-secret", model.Entries[0].Content!.Body);
        model.Append(Now, 69, Encoding.UTF8.GetBytes("not-msdp-data"));
        Assert.Equal(["MSDP"], model.Entries[1].Kinds);
        var thirty = Msdp(Enumerable.Range(0, 30).Select(i => ($"VARIABLE_{i:00}", "1")).ToArray());
        model.Append(Now, 69, thirty);
        Assert.Equal(30, model.Entries[2].Kinds.Count);
        Assert.Equal(33, model.Kinds.Count);
        Assert.True(model.HasMoreKinds); Assert.Equal(9, model.HiddenKindCount);
        Assert.Equal(ProtocolDiagnosticsViewModel.VisibleKindCap, model.ChipKinds.Count);
        model.KindsExpanded = true;
        Assert.Equal(33, model.ChipKinds.Count);
    }

    [Fact]
    public void TogglingChipsFiltersTheVisibleListAndSeveralCanBeActive()
    {
        var model = Seeded();
        model.ToggleKind("OPPONENTHEALTH");
        Assert.True(model.IsFiltering);
        Assert.Single(model.Visible);
        Assert.Same(model.Entries[1], model.Visible[0]);
        Assert.Equal("1 of 5", model.CountLabel);
        model.ToggleKind("Char.Vitals");
        Assert.Equal([model.Entries[0], model.Entries[1], model.Entries[3]], model.Visible);
        Assert.Equal("3 of 5", model.CountLabel);
        model.ToggleKind("OPPONENTHEALTH");
        Assert.Equal([model.Entries[0], model.Entries[3]], model.Visible);
        Assert.Equal(5, model.Entries.Count);
        model.ClearKindsCommand.Execute(null);
        Assert.Equal(model.Entries, model.Visible);
        Assert.False(model.IsFiltering);
        Assert.Contains("5 messages", model.CountLabel);
    }

    [Fact]
    public void TextFiltersOnKindNamesAndBodiesCaseInsensitivelyAndCombinesWithChips()
    {
        var model = Seeded();
        model.Filter = "vitals";
        Assert.Equal([model.Entries[0], model.Entries[3]], model.Visible);
        model.Filter = "cockpit";
        Assert.Equal([model.Entries[4]], model.Visible);
        model.Filter = "health";
        Assert.Equal([model.Entries[1]], model.Visible);
        model.Filter = "42";
        Assert.Equal([model.Entries[0], model.Entries[1]], model.Visible);
        model.ToggleKind("HEALTH");
        Assert.Equal([model.Entries[1]], model.Visible);
        Assert.Equal("1 of 5", model.CountLabel);
        model.Filter = "no-such-text";
        Assert.Empty(model.Visible);
        Assert.False(model.IsEmpty);
        model.ClearFilterCommand.Execute(null);
        Assert.Equal("", model.Filter);
        Assert.Equal([model.Entries[1]], model.Visible);
    }

    [Fact]
    public void FollowLatestTracksTheFilteredListAndNewEntriesJoinItWhenTheyMatch()
    {
        var model = Seeded();
        model.ToggleKind("Char.Vitals");
        Assert.True(model.Follow);
        Assert.Same(model.Entries[3], model.SelectedEntry);
        model.Append(Now, 69, Msdp(("HEALTH", "41")));
        Assert.Same(model.Entries[3], model.SelectedEntry);
        Assert.Equal(2, model.Visible.Count);
        model.Append(Now, 201, Gmcp("Char.Vitals {\"hp\":39}"));
        Assert.Equal(3, model.Visible.Count);
        Assert.Same(model.Entries[^1], model.SelectedEntry);
        Assert.Equal(3, model.Kinds.Single(k => k.Name == "Char.Vitals").Count);
        model.Filter = "39";
        Assert.Equal([model.Entries[^1]], model.Visible);
        Assert.Same(model.Entries[^1], model.SelectedEntry);
    }

    [Fact]
    public void ASelectedEntryStaysSelectedWhileItMatchesAndClearsWhenItDoesNot()
    {
        var model = Seeded();
        model.Follow = false; model.SelectedEntry = model.Entries[1];
        model.ToggleKind("HEALTH");
        Assert.Same(model.Entries[1], model.SelectedEntry);
        model.Filter = "cockpit";
        Assert.Null(model.SelectedEntry);
        model.Filter = "";
        Assert.Null(model.SelectedEntry);
        Assert.False(model.Follow);
        model.SelectedEntry = model.Entries[1];
        model.Follow = true;
        Assert.Same(model.Visible[^1], model.SelectedEntry);
    }

    [Fact]
    public void ClearingTheHistoryResetsKindsSelectionAndTextAndEvictionKeepsTheViewInStep()
    {
        var model = Seeded();
        model.ToggleKind("Char.Vitals"); model.Filter = "hp"; model.KindsExpanded = true;
        model.ClearCommand.Execute(null);
        Assert.Empty(model.Kinds); Assert.Empty(model.ChipKinds); Assert.Empty(model.Visible);
        Assert.False(model.HasActiveKinds); Assert.False(model.KindsExpanded);
        Assert.Equal("hp", model.Filter);
        model.Append(Now, 201, Gmcp("Char.Vitals {\"hp\":1}"));
        Assert.Single(model.Visible);
        Assert.False(model.Kinds.Single().IsActive);
        model.Filter = "";
        for (var i = 0; i < ProtocolDiagnosticsViewModel.MaximumEntries + 50; i++)
            model.Append(Now, 69, Msdp((i % 2 == 0 ? "HEALTH" : "MANA", i.ToString())));
        model.ToggleKind("HEALTH");
        Assert.Equal(ProtocolDiagnosticsViewModel.MaximumEntries, model.Entries.Count);
        Assert.All(model.Visible, e => Assert.Contains("HEALTH", e.Kinds));
        Assert.Equal(model.Entries.Where(e => e.Kinds.Contains("HEALTH")), model.Visible);
        model.Append(Now, 69, Msdp(("HEALTH", "x")));
        Assert.Equal(model.Entries.Where(e => e.Kinds.Contains("HEALTH")), model.Visible);
    }

    [Fact]
    public void FilteringTwoThousandEntriesOverAHundredKindsIsQuick()
    {
        var model = new ProtocolDiagnosticsViewModel();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 2000; i++)
            model.Append(Now, 69, Msdp(($"VARIABLE_{i % 100:000}", i.ToString()), ($"OTHER_{(i * 7) % 100:000}", "value")));
        Assert.Equal(200, model.Kinds.Count);
        for (var i = 0; i < 20; i++) { model.Filter = "VARIABLE_0" + i % 10; model.ToggleKind($"OTHER_{i:000}"); }
        model.ClearKindsCommand.Execute(null); model.Filter = "";
        stopwatch.Stop();
        Assert.Equal(ProtocolDiagnosticsViewModel.MaximumEntries, model.Visible.Count);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"took {stopwatch.Elapsed}");
    }

    private static WorkspaceController Controller() => new(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(),
        new SettingsStore(Path.Combine(Path.GetTempPath(), "wandur-diagfilter-" + Guid.NewGuid(), "settings.json")),
        new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());

    private static void Click(Window window, Control target)
    {
        var point = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task ChipsAndTheFilterBoxNarrowTheMessagesTab()
    {
        await using var controller = Controller();
        var view = new TerminalView(controller);
        var window = new Window { Content = view, Width = 960, Height = 680 };
        using var server = new TcpListener(IPAddress.Loopback, 0); server.Start();
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            await controller.StartAsync(new ConnectionProfile { Host = "127.0.0.1", Port = ((IPEndPoint)server.LocalEndpoint).Port });
            using var socket = await server.AcceptTcpClientAsync();
            controller.Pages.SelectedIndex = (int)SessionPage.Diagnostics; Dispatcher.UIThread.RunJobs();
            static byte[] Message(byte option, byte[] payload) => new byte[] { 255, 250, option }.Concat(payload).Concat(new byte[] { 255, 240 }).ToArray();
            async Task Output(byte[] bytes)
            {
                await socket.GetStream().WriteAsync(bytes);
                for (var i = 0; i < 10; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); controller.FlushOutput(); }
            }
            await Output(new byte[] { 255, 251, 201, 255, 251, 69 }
                .Concat(Message(201, Gmcp("Char.Vitals {\"hp\":42,\"maxhp\":100}")))
                .Concat(Message(69, Msdp(("OPPONENTHEALTH", "80"), ("HEALTH", "42")))).ToArray());
            // The telnet parser hands diagnostics GMCP and MSDP only; an MSSP reply is appended the way the controller would.
            controller.Diagnostics.Append(DateTimeOffset.Now, 70, Mssp);
            Dispatcher.UIThread.RunJobs();
            var model = controller.Diagnostics;
            Assert.Equal(3, model.Entries.Count);
            var diagnostics = view.GetVisualDescendants().OfType<ProtocolDiagnosticsView>().Single();
            var chips = diagnostics.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "KindChips");
            var count = diagnostics.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "DiagnosticsCount");
            var list = diagnostics.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "ProtocolMessages");
            var filter = diagnostics.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "DiagnosticsFilter");
            var more = diagnostics.GetVisualDescendants().OfType<ToggleButton>().Single(t => t.Name == "MoreKinds");
            ToggleButton Chip(string name) => chips.GetVisualDescendants().OfType<ToggleButton>()
                .Single(t => t.DataContext is ProtocolDiagnosticKind { Name: var kind } && kind == name);
            IEnumerable<string> ChipLabels() => chips.GetVisualDescendants().OfType<ToggleButton>().Select(t => (string)t.Content!);
            Assert.Equal(["Char.Vitals (1)", "HEALTH (1)", "OPPONENTHEALTH (1)", "MSSP (1)"], ChipLabels());
            Assert.False(more.IsVisible);
            Assert.Equal(3, list.ItemCount);

            Click(window, Chip("OPPONENTHEALTH"));
            Assert.True(model.Kinds.Single(k => k.Name == "OPPONENTHEALTH").IsActive);
            Assert.Equal(1, list.ItemCount);
            Assert.Equal("MSDP", model.Visible.Single().Protocol);
            Assert.Equal("1 of 3", count.Text);
            Assert.True(Chip("OPPONENTHEALTH").IsChecked);

            Click(window, Chip("OPPONENTHEALTH"));
            Assert.Equal(3, list.ItemCount);
            filter.Focus(); window.KeyTextInput("vitals"); Dispatcher.UIThread.RunJobs();
            Assert.Equal("vitals", model.Filter);
            Assert.Equal(1, list.ItemCount);
            Assert.Equal("GMCP", model.Visible.Single().Protocol);
            Assert.Equal("1 of 3", count.Text);
            Assert.Same(model.Visible[0], list.SelectedItem);

            Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            using (var frame = window.CaptureRenderedFrame())
            {
                Assert.NotNull(frame);
                var folder = Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR");
                if (!string.IsNullOrEmpty(folder)) { Directory.CreateDirectory(folder); frame.Save(Path.Combine(folder, "diagnostics-filter.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions()); }
            }

            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("", model.Filter); Assert.Equal("", filter.Text ?? "");
            Assert.Equal(3, list.ItemCount);

            Click(window, Chip("HEALTH"));
            Click(window, diagnostics.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "AllKinds"));
            Assert.False(model.HasActiveKinds); Assert.Equal(3, list.ItemCount);

            model.Append(DateTimeOffset.Now, 69, Msdp(Enumerable.Range(0, 30).Select(i => ($"VARIABLE_{i:00}", "1")).ToArray()));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(34, model.Kinds.Count);
            Assert.True(more.IsVisible);
            Assert.Equal("10 more", (string)more.Content!);
            Assert.Equal(ProtocolDiagnosticsViewModel.VisibleKindCap, chips.GetVisualDescendants().OfType<ToggleButton>().Count());
            Click(window, more);
            Assert.True(model.KindsExpanded);
            Assert.Equal(34, chips.GetVisualDescendants().OfType<ToggleButton>().Count());
        }
        finally { window.Close(); }
    }
}
