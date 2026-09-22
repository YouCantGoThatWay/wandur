using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Protocol;
using Wandur.Core.Scripting;
using Wandur.Core.Settings;
using Wandur.Desktop.Services;
using Wandur.Desktop.Terminal;
using Wandur.Desktop.Views;
using Wandur.Models;

namespace Wandur.Desktop.Tests;

/// <summary>What the owner asked for after playing Legends of the Jedi with a generated pack: color codes in
/// panel text, a way to bring a panel to the front, and gauges in the vitals strip under the transcript.</summary>
public sealed class ScriptPanelColorFocusBarsTests
{
    private const string Script = """
        const combat = mud.panel("combat", { title: "&228A Vicious Womprat&D", dock: "right" });
        combat.label("status", { text: "&RRed&D plain" });
        combat.gauge("hp", { label: "&CHull&D", value: 40, max: 100 });
        combat.button("flee", { label: "&YFlee&D now" });
        const cargo = mud.panel("cargo", { title: "Cargo", dock: "right" });
        cargo.label("hold", { text: "&GEmpty&D hold, &Y12&D credits, ^bstowed^^" });
        cargo.table("manifest", { columns: ["&WItem&D", "Qty"], rows: [["&Cspice&D", "3"]] });
        const bars = mud.panel("vitals", { title: "Vitals", dock: "bars" });
        bars.gauge("force", { label: "&CForce&D", value: 40, max: 80 });
        bars.label("note", { text: "labels are ignored in the strip" });
        mud.alias(/^focuscargo$/, () => { cargo.focus(); mud.echo("focused"); });
        mud.alias(/^hidecargo$/, () => cargo.hide());
        mud.alias(/^showfocus$/, () => cargo.show({ focus: true }));
        mud.alias(/^hidebars$/, () => bars.hide());
        mud.alias(/^showbars$/, () => bars.show());
        mud.alias(/^closebars$/, () => bars.close());
        mud.alias(/^morebars$/, () => mud.panel("ship", { dock: "bars" }).gauge("hull", { label: "Hull", value: 10, max: 100 }));
        mud.alias(/^raise$/, () => bars.gauge("force", { label: "&CForce&D", value: 60, max: 80 }));
        """;

    private static Color Of(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;
    private static Run[] Runs(TextBlock block) => block.Inlines!.OfType<Run>().ToArray();
    private static int Count(string text, string word) => text.Split(word).Length - 1;

    [AvaloniaFact]
    public async Task PanelsRenderColorCodesFocusOnRequestAndPutBarsGaugesUnderTheTranscript()
    {
        var path = Path.Combine(Path.GetTempPath(), "wandur-panel-color-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var store = new SettingsStore(Path.Combine(path, "settings.json"));
        var profile = new ConnectionProfile { Name = "Panel world", Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port };
        store.Save(new ClientSettings { Theme = "Ember", Profiles = [profile] });
        var window = new MainWindow(new TranscriptDisplayFactory(), store, new MemoryPasswordVault(),
            new MemoryRoomMapStore(), new InlineScriptFactory(), new MemoryScriptLibraryStore()) { Width = 1200, Height = 800 };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            await window.Sessions.OpenAsync(profile);
            using var server = await listener.AcceptTcpClientAsync(timeout.Token);
            var script = window.Controller.ScriptLibrary.Items[0];
            script.Runtime.Source = Script;
            await script.Runtime.RunAsync();
            Assert.True(script.Runtime.IsRunning, script.Runtime.Error);
            Dispatcher.UIThread.RunJobs();
            var panels = window.Controller.ScriptLibrary.Panels.Panels;
            Assert.Equal(new[] { "combat", "cargo", "vitals" }, panels.Select(panel => panel.Id).ToArray());

            // The bars panel never appears in the rail; coded titles are stripped for the tab header.
            var workspace = window.Workspace;
            Assert.Empty(workspace.ScriptPanelTools);
            Assert.Null(workspace.RightPanelsDock);
            var rail = Assert.Single(window.GetVisualDescendants().OfType<ScriptPanelRailView>());
            Assert.True(rail.IsVisible);
            Assert.Equal(new[] { "A Vicious Womprat", "Cargo" }, panels.Where(panel => !panel.IsBars).Select(panel => panel.Title).ToArray());
            Assert.Same(panels[0], rail.SelectedPanel);

            // A coded label is two runs: the red palette brush, then the inherited default.
            var combat = Assert.Single(window.GetVisualDescendants().OfType<ScriptPanelView>(), candidate => candidate.Panel.Id == "combat");
            var status = Assert.Single(combat.GetVisualDescendants().OfType<TextBlock>(), block => block.Name == "ScriptWidget_status");
            var runs = Runs(status);
            Assert.Equal(new[] { "Red", " plain" }, runs.Select(run => run.Text).ToArray());
            Assert.Equal(Of(combat.GetValue(TerminalPalette.Colors[9])), Of(runs[0].Foreground));
            Assert.False(runs[1].IsSet(TextElement.ForegroundProperty), "the plain run inherits the block's foreground");
            // The gauge caption and the button label render their codes too; the plain part of the button stays text.
            var gauge = Assert.Single(combat.GetVisualDescendants().OfType<ResourceBar>());
            Assert.Equal("Hull", Assert.Single(Runs(gauge.Label)).Text);
            Assert.Equal(Of(combat.GetValue(TerminalPalette.Colors[14])), Of(Runs(gauge.Label)[0].Foreground));
            var button = Assert.Single(combat.GetVisualDescendants().OfType<Button>());
            var buttonRuns = Runs(Assert.IsType<TextBlock>(button.Content));
            Assert.Equal(new[] { "Flee", " now" }, buttonRuns.Select(run => run.Text).ToArray());
            Assert.Equal(Of(combat.GetValue(TerminalPalette.Colors[11])), Of(buttonRuns[0].Foreground));

            // The strip under the transcript shows the bars gauge, with its coded caption and the plain automation name.
            var strip = Assert.Single(window.GetVisualDescendants().OfType<ResourceBarsView>());
            Assert.True(strip.IsVisible);
            var force = Assert.Single(strip.ScriptCards);
            Assert.Equal(50, Assert.Single(force.GetVisualDescendants().OfType<ProgressBar>()).Value);
            Assert.Equal("Force", Assert.Single(Runs(force.Label)).Text);
            Assert.Equal("Force", Avalonia.Automation.AutomationProperties.GetName(Assert.Single(force.GetVisualDescendants().OfType<ProgressBar>())));
            Assert.Equal(new object[] { force }, strip.GetVisualDescendants().OfType<ResourceBar>().ToArray());
            // A value update changes the same card in place.
            Assert.True(await window.Controller.SendAsync("raise"));
            await ScriptSessionTests.WaitFor(() => Math.Abs(Assert.Single(force.GetVisualDescendants().OfType<ProgressBar>()).Value - 75) < 0.001);
            Assert.Same(force, Assert.Single(strip.ScriptCards));

            // focus() brings the second panel's tab to the front; a second request within a second is dropped.
            var stopwatch = Stopwatch.StartNew();
            Assert.True(await window.Controller.SendAsync("focuscargo"));
            await ScriptSessionTests.WaitFor(() => { Dispatcher.UIThread.RunJobs(); return ReferenceEquals(rail.SelectedPanel, panels[1]); });
            Assert.False(panels[1].FocusRequested);
            rail.Select(panels[0]);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(panels[0], rail.SelectedPanel);
            Assert.True(await window.Controller.SendAsync("focuscargo"));
            await ScriptSessionTests.WaitFor(() => Count(script.Runtime.Log, "focused") == 2);
            Dispatcher.UIThread.RunJobs();
            var elapsed = stopwatch.Elapsed;
            if (elapsed < ScriptPanelAction.FocusInterval) Assert.Same(panels[0], rail.SelectedPanel);
            else Assert.Fail("The second focus request ran " + elapsed + " after the first; the rate limit could not be observed.");
            rail.Select(panels[1]);
            Dispatcher.UIThread.RunJobs();

            // The focused second panel shows its colored label, and the bars gauge sits under the transcript.
            var cargo = Assert.Single(window.GetVisualDescendants().OfType<ScriptPanelView>(), candidate => candidate.Panel.Id == "cargo");
            Assert.Same(panels[1], cargo.Panel);
            var hold = Assert.Single(cargo.GetVisualDescendants().OfType<TextBlock>(), block => block.Name == "ScriptWidget_hold");
            Assert.Equal(new[] { "Empty", " hold, ", "12", " credits, ", "stowed^" }, Runs(hold).Select(run => run.Text).ToArray());
            Assert.Equal(Of(cargo.GetValue(TerminalPalette.Colors[4])), Of(Runs(hold)[4].Background));
            Assert.Contains(cargo.GetVisualDescendants().OfType<TextBlock>(), block => Avalonia.Automation.AutomationProperties.GetName(block) == "spice");
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            if (Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR") is { } captures)
            {
                Directory.CreateDirectory(captures);
                using var frame = window.CaptureRenderedFrame();
                frame?.Save(Path.Combine(captures, "panel-color.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }

            // show({ focus: true }) after hide shows the panel and makes it the active tab again, once the interval has passed.
            Assert.True(await window.Controller.SendAsync("hidecargo"));
            await ScriptSessionTests.WaitFor(() => !panels[1].IsVisible);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(panels[0], rail.SelectedPanel);
            await Task.Delay(ScriptPanelAction.FocusInterval + TimeSpan.FromMilliseconds(100));
            Assert.True(await window.Controller.SendAsync("showfocus"));
            await ScriptSessionTests.WaitFor(() => { Dispatcher.UIThread.RunJobs(); return panels[1].IsVisible && ReferenceEquals(rail.SelectedPanel, panels[1]); });

            // hide() takes the bars out of the strip, which hides itself; show() puts them back; a second bars panel appends.
            Assert.True(await window.Controller.SendAsync("hidebars"));
            await ScriptSessionTests.WaitFor(() => !panels[2].IsVisible);
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(strip.ScriptCards);
            Assert.False(strip.IsVisible);
            Assert.True(await window.Controller.SendAsync("showbars"));
            await ScriptSessionTests.WaitFor(() => panels[2].IsVisible);
            Dispatcher.UIThread.RunJobs();
            Assert.Single(strip.ScriptCards);
            Assert.True(strip.IsVisible);
            Assert.True(await window.Controller.SendAsync("morebars"));
            await ScriptSessionTests.WaitFor(() => panels.Count == 4);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new[] { "Force", "Hull" }, strip.ScriptCards.Select(card => card.Label.Inlines is { Count: > 0 } inlines ? string.Concat(inlines.OfType<Run>().Select(run => run.Text)) : card.Label.Text).ToArray());
            Assert.Equal(2, panels.Count(panel => !panel.IsBars));
            Assert.True(await window.Controller.SendAsync("closebars"));
            await ScriptSessionTests.WaitFor(() => panels.Count == 3);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Hull", Assert.Single(strip.ScriptCards).Label.Text);
        }
        finally
        {
            await window.Sessions.DisposeAsync(); window.Close();
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    [AvaloniaFact]
    public void BarsGaugesFollowTheMappedVitalsAndLeaveWithTheirPanel()
    {
        var catalog = System.Text.Json.JsonSerializer.Deserialize<MappingCatalog>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/curated-mappings.json")), ModelJson.Options)!;
        var engine = new ProtocolBindingEngine(Assert.Single(catalog.Worlds, w => w.WorldId == "mudverse:645"));
        void Packet(string message) => engine.Observe(201, ProtocolDiagnosticFormatter.Format(201, Encoding.UTF8.GetBytes(message)), DateTimeOffset.UtcNow);
        Packet("Char.Base {\"name\":\"Test character\"}");
        Packet("Char.Vitals {\"hp\":640,\"mana\":163,\"moves\":107,\"psp\":0}");
        Packet("Char.Maxstats {\"maxhp\":640,\"maxmana\":326,\"maxmoves\":321,\"maxpsp\":0}");
        var host = new ScriptPanelHost();
        var scriptId = Guid.NewGuid();
        var view = new ResourceBarsView { Panels = host };
        var window = new Window { Content = view, Width = 800, Height = 180 };
        try
        {
            window.Show();
            view.Update(engine.State, true); window.UpdateLayout();
            Assert.Equal(3, view.GetVisualDescendants().OfType<ProgressBar>().Count());

            host.Apply(scriptId, ScriptPanelAction.Parse("""{"panel":"ship","action":"create","title":"Ship","dock":"bars"}"""));
            host.Apply(scriptId, ScriptPanelAction.Parse("""{"panel":"ship","action":"widget","widget":"hull","kind":"gauge","props":{"label":"Hull","value":25,"max":100,"warn":0.3}}"""));
            host.Apply(scriptId, ScriptPanelAction.Parse("""{"panel":"ship","action":"widget","widget":"note","kind":"label","props":{"text":"ignored"}}"""));
            window.UpdateLayout();
            var bars = view.GetVisualDescendants().OfType<ProgressBar>().ToArray();
            Assert.Equal(4, bars.Length);
            Assert.Equal(25, bars[3].Value);
            Assert.Equal("Hull", Avalonia.Automation.AutomationProperties.GetName(bars[3]));
            // At or below warn the bar takes the health color, like a docked gauge.
            Assert.Equal(Color.Parse(ResourceBar.ColorFor("health")!), Of(bars[3].Foreground));
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "ignored");

            // Mapped vitals changing keeps the script gauge after them.
            Packet("Char.Vitals {\"mana\":81.5}"); view.Update(engine.State, true); window.UpdateLayout();
            bars = view.GetVisualDescendants().OfType<ProgressBar>().ToArray();
            Assert.Equal(4, bars.Length);
            Assert.Equal(25, bars[3].Value);

            host.Apply(scriptId, ScriptPanelAction.Parse("""{"panel":"ship","action":"hide"}"""));
            window.UpdateLayout();
            Assert.Equal(3, view.GetVisualDescendants().OfType<ProgressBar>().Count());
            host.Apply(scriptId, ScriptPanelAction.Parse("""{"panel":"ship","action":"show"}"""));
            window.UpdateLayout();
            Assert.Equal(4, view.GetVisualDescendants().OfType<ProgressBar>().Count());
            // focus is a no-op on a bars panel: no request, still shown.
            host.Apply(scriptId, ScriptPanelAction.Parse("""{"panel":"ship","action":"focus"}"""));
            Assert.False(host.Panels[0].FocusRequested);
            host.Apply(scriptId, ScriptPanelAction.Parse("""{"panel":"ship","action":"close"}"""));
            window.UpdateLayout();
            Assert.Equal(3, view.GetVisualDescendants().OfType<ProgressBar>().Count());

            // With nothing mapped and no bars panel the strip hides, exactly as before.
            Packet("Char.Base {\"name\":\"Different character\"}"); view.Update(engine.State, true);
            Assert.False(view.IsVisible);
        }
        finally { window.Close(); }
    }

    /// <summary>The generated opponent script of the Legends of the Jedi pack, verbatim.</summary>
    private const string OpponentScript = """
        const p = mud.panel("opponent", { title: "Combat opponent", dock: "right" });
        p.label("name", { text: "No opponent reported" });
        p.gauge("health", { label: "Health", value: 0, max: 1, warn: 0.3 });
        p.hide();
        let previous = "";
        function refresh() {
          const name = mud.state.get("msdp.OPPONENTNAME");
          const hasName = typeof name === "string" ? name.trim() !== "" : name !== undefined && name !== null;
          const current = hasName ? mud.format(name) : "";
          if (!hasName) { if (previous !== "") { previous = ""; p.hide(); } return; }
          const hp = mud.state.get("msdp.OPPONENTHEALTH");
          const max = mud.state.get("msdp.OPPONENTHEALTHMAX");
          const n = typeof hp === "number" ? hp : Number(hp) || 0;
          const m = typeof max === "number" ? max : Number(max) || 1;
          const key = current + ":" + n + ":" + m;
          if (previous !== key) {
            const newTarget = previous === "" || previous.split(":")[0] !== current;
            previous = key;
            p.label("name", { text: current });
            p.gauge("health", { label: "Health", value: n, max: m > 0 ? m : 1, warn: 0.3 });
            p.show({ focus: newTarget });
          }
        }
        mud.on(Events.Msdp, refresh);
        refresh();
        """;

    private static byte[] Msdp(params (string Variable, string Value)[] variables)
        => [255, 250, 69, .. variables.SelectMany(pair => (byte[])[1, .. Encoding.UTF8.GetBytes(pair.Variable), 2, .. Encoding.UTF8.GetBytes(pair.Value)]), 255, 240];

    /// <summary>The owner's report: the opponent panel, hidden since load and never yet docked, did not come to the front
    /// when a fight started. Checked with another script panel holding the panels dock and with no dock yet, and with the
    /// payload arriving while the login handshake owns the session, which the replay of the host cache then covers.</summary>
    [AvaloniaTheory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task ANewOpponentShowsTheHiddenOpponentPanelAndBringsItsTabToTheFront(bool otherPanel, bool duringLogin)
    {
        var path = Path.Combine(Path.GetTempPath(), "wandur-panel-opponent-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var store = new SettingsStore(Path.Combine(path, "settings.json"));
        var profile = new ConnectionProfile { Name = "Opponent world", Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port };
        store.Save(new ClientSettings { Theme = "Ember", Profiles = [profile] });
        var window = new MainWindow(new TranscriptDisplayFactory(), store, new MemoryPasswordVault(),
            new MemoryRoomMapStore(), new InlineScriptFactory(), new MemoryScriptLibraryStore()) { Width = 1200, Height = 800 };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            await window.Sessions.OpenAsync(profile);
            using var server = await listener.AcceptTcpClientAsync(timeout.Token);
            var stream = server.GetStream();
            var library = window.Controller.ScriptLibrary;
            // Another pack panel may already sit in the session rail beside the transcript.
            var ship = library.Items[0];
            if (otherPanel)
            {
                ship.Runtime.Source = """mud.panel("ship", { title: "Ship", dock: "right" }).label("hull", { text: "Hull 100" });""";
                await ship.Runtime.RunAsync();
                Assert.True(ship.Runtime.IsRunning, ship.Runtime.Error);
            }
            var opponent = library.Add();
            opponent.Runtime.Source = OpponentScript;
            await opponent.Runtime.RunAsync();
            Assert.True(opponent.Runtime.IsRunning, opponent.Runtime.Error);
            Dispatcher.UIThread.RunJobs();
            var workspace = window.Workspace;
            Assert.NotNull(workspace.MapTool); Assert.NotNull(workspace.ChannelsTool);
            var panel = Assert.Single(library.Panels.Panels, candidate => candidate.Id == "opponent");
            Assert.False(panel.IsVisible);
            var rail = Assert.Single(window.GetVisualDescendants().OfType<ScriptPanelRailView>());
            if (otherPanel)
            {
                var shipPanel = Assert.Single(library.Panels.Panels, candidate => candidate.Id == "ship");
                Assert.Same(shipPanel, rail.SelectedPanel);
            }
            else
            {
                Assert.False(rail.IsVisible);
            }
            Assert.False(workspace.IsScriptPanelVisible(panel));

            // The fight starts: the world reports the opponent's name and health in one MSDP payload.
            await stream.WriteAsync(new byte[] { 255, 251, 69 }, timeout.Token);
            await ScriptSessionTests.WaitFor(() => window.Controller.ProtocolEvidence.Msdp == TelnetOptionState.Enabled);
            if (duringLogin)
            {
                // The handshake owns the session: the payload is cached and held back until manual input ends it.
                var fields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var attempt = typeof(WorkspaceController).GetNestedType("LoginAttempt", System.Reflection.BindingFlags.NonPublic)!;
                typeof(WorkspaceController).GetField("_login", fields)!.SetValue(window.Controller, Activator.CreateInstance(attempt, profile, "secret"));
                window.Controller.SetManualPrivate(false);
                Assert.True(opponent.Runtime.IsPaused);
            }
            await stream.WriteAsync(Msdp(("OPPONENTNAME", "&228A Vicious Womprat&D"), ("OPPONENTHEALTH", "100"), ("OPPONENTHEALTHMAX", "100")), timeout.Token);
            if (duringLogin)
            {
                await ScriptSessionTests.WaitFor(() => { window.Controller.FlushOutput(); return window.Controller.ScriptState.TryGetMsdp("OPPONENTHEALTHMAX") is not null; });
                Assert.False(panel.IsVisible);
                Assert.True(await window.Controller.SendAsync("look"));
            }
            await ScriptSessionTests.WaitFor(() => { window.Controller.FlushOutput(); return panel.IsVisible && workspace.IsScriptPanelVisible(panel); });
            Assert.True(rail.IsVisible);
            Assert.Null(workspace.RightPanelsDock);
            await ScriptSessionTests.WaitFor(() => { Dispatcher.UIThread.RunJobs(); return ReferenceEquals(rail.SelectedPanel, panel); });
            Assert.False(panel.FocusRequested);
            Assert.Equal("Combat opponent", panel.Title);
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();

            // The active tab renders the colored name without its codes, and the gauge is full.
            var view = Assert.Single(window.GetVisualDescendants().OfType<ScriptPanelView>(), candidate => ReferenceEquals(candidate.Panel, panel));
            var name = Assert.Single(view.GetVisualDescendants().OfType<TextBlock>(), block => block.Name == "ScriptWidget_name");
            var runs = Runs(name);
            Assert.Equal("A Vicious Womprat", string.Concat(runs.Select(run => run.Text)));
            Assert.True(runs[0].IsSet(TextElement.ForegroundProperty), "the xterm 228 code colors the name");
            var gauge = Assert.Single(view.GetVisualDescendants().OfType<ResourceBar>());
            await ScriptSessionTests.WaitFor(() =>
            {
                Dispatcher.UIThread.RunJobs();
                return Math.Abs(Assert.Single(gauge.GetVisualDescendants().OfType<ProgressBar>()).Value - 100) < 0.001;
            });
        }
        finally
        {
            await window.Sessions.DisposeAsync(); window.Close();
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }
}
