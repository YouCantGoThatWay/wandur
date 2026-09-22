using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Model.Controls;
using Dock.Model.Core;
using Wandur.Core.Settings;
using Wandur.Desktop.Views;

namespace Wandur.Desktop.Tests;

public sealed class ScriptPanelViewTests
{
    private const string PanelScript = """
        const ship = mud.panel("ship", { title: "Ship", dock: "right" });
        ship.gauge("hull", { label: "Hull", value: 10, max: 100 });
        ship.label("system", { text: "In orbit" });
        ship.button("flee", { label: "Flee", onClick: () => mud.send("flee") });
        ship.input("say", { placeholder: "Say...", onSubmit: text => mud.send("say " + text) });
        mud.on(Events.Gmcp, event => {
            if (event.package === "Char.Vitals") ship.gauge("hull", { label: "Hull", value: event.data.hp, max: 100 });
        });
        mud.alias(/^hide$/, () => ship.hide());
        mud.alias(/^show$/, () => ship.show());
        mud.alias(/^close$/, () => ship.close());
        """;

    private static Color Of(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    [AvaloniaFact]
    public async Task ADeclaredPanelDocksRendersNativeWidgetsUpdatesInPlaceAndItsCallbacksReachTheWorld()
    {
        var path = Path.Combine(Path.GetTempPath(), "wandur-script-panel-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var store = new SettingsStore(Path.Combine(path, "settings.json"));
        var profile = new ConnectionProfile { Name = "Panel world", Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port };
        store.Save(new ClientSettings { Theme = "Ember", Profiles = [profile] });
        var window = new MainWindow(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), store, new MemoryPasswordVault(),
            new MemoryRoomMapStore(), new InlineScriptFactory(), new MemoryScriptLibraryStore());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            await window.Sessions.OpenAsync(profile);
            using var server = await listener.AcceptTcpClientAsync(timeout.Token);
            using var reader = new StreamReader(server.GetStream(), Encoding.UTF8, leaveOpen: true);
            var script = window.Controller.ScriptLibrary.Items[0];
            script.Runtime.Source = PanelScript;
            await script.Runtime.RunAsync();
            Assert.True(script.Runtime.IsRunning, script.Runtime.Error);
            Dispatcher.UIThread.RunJobs();

            var panel = Assert.Single(window.Controller.ScriptLibrary.Panels.Panels);
            Assert.Equal("Ship", panel.Title);
            var rail = Assert.Single(window.GetVisualDescendants().OfType<ScriptPanelRailView>());
            Assert.True(rail.IsVisible);
            Assert.Same(panel, rail.SelectedPanel);
            var view = Assert.Single(window.GetVisualDescendants().OfType<ScriptPanelView>());
            var gauge = Assert.Single(view.GetVisualDescendants().OfType<ProgressBar>());
            Assert.Equal(10, gauge.Value);
            var orbit = Assert.Single(view.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "In orbit");
            Assert.Equal(new FontFamily(Wandur.Desktop.Terminal.TerminalPalette.Monospace), orbit.FontFamily);
            var button = Assert.Single(view.GetVisualDescendants().OfType<Button>());
            Assert.Equal(new FontFamily(Wandur.Desktop.Terminal.TerminalPalette.Monospace), button.FontFamily);
            Assert.Equal("Flee", button.Content);
            var input = Assert.Single(view.GetVisualDescendants().OfType<TextBox>());
            Assert.Equal("Say...", input.PlaceholderText);

            // Callbacks are read before GMCP is negotiated, so the client's Core.Hello cannot precede them.
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("flee", await reader.ReadLineAsync(timeout.Token));

            input.Text = "hello";
            input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Assert.Equal("say hello", await reader.ReadLineAsync(timeout.Token));
            Assert.Equal("", input.Text);

            // A GMCP update changes the same bar rather than rebuilding the panel.
            await server.GetStream().WriteAsync(new byte[] { 255, 251, 201 }, timeout.Token);
            await server.GetStream().WriteAsync(Gmcp("Char.Vitals {\"hp\":42}"), timeout.Token);
            await ScriptSessionTests.WaitFor(() => { window.Controller.FlushOutput(); return Math.Abs(gauge.Value - 42) < 0.001; });
            Assert.Same(gauge, Assert.Single(view.GetVisualDescendants().OfType<ProgressBar>()));

            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            if (Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR") is { } captures)
            {
                Directory.CreateDirectory(captures);
                using var frame = window.CaptureRenderedFrame();
                frame?.Save(Path.Combine(captures, "script-panel.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }

            // A theme switch recolors coded widget text through the same palette brushes the transcript uses.
            var status = Assert.Single(view.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "In orbit");
            var before = Of(view.GetValue(Wandur.Desktop.Terminal.TerminalPalette.Colors[7]));
            window.Controller.SaveSettings(window.Controller.Settings with { Theme = "Paper" });
            Dispatcher.UIThread.RunJobs();
            Assert.NotEqual(before, Of(view.GetValue(Wandur.Desktop.Terminal.TerminalPalette.Colors[7])));
            Assert.Same(status, Assert.Single(view.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "In orbit"));

            Assert.True(await window.Controller.SendAsync("hide"));
            await ScriptSessionTests.WaitFor(() => !window.Controller.ScriptLibrary.Panels.Panels[0].IsVisible);
            Dispatcher.UIThread.RunJobs();
            Assert.False(window.Workspace.IsScriptPanelVisible(panel));
            Assert.False(rail.IsVisible);
            Assert.True(await window.Controller.SendAsync("show"));
            await ScriptSessionTests.WaitFor(() => window.Controller.ScriptLibrary.Panels.Panels[0].IsVisible);
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.Workspace.IsScriptPanelVisible(panel));
            Assert.True(rail.IsVisible);

            Assert.True(await window.Controller.SendAsync("close"));
            await ScriptSessionTests.WaitFor(() => window.Controller.ScriptLibrary.Panels.Panels.Count == 0);
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(window.Workspace.ScriptPanelTools);
            Assert.False(rail.IsVisible);
            Assert.Empty(window.GetVisualDescendants().OfType<ScriptPanelView>());
        }
        finally
        {
            await window.Sessions.DisposeAsync(); window.Close();
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    [AvaloniaFact]
    public async Task StoppingAScriptRetiresItsPanelAndItsTool()
    {
        var path = Path.Combine(Path.GetTempPath(), "wandur-script-panel-stop-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var store = new SettingsStore(Path.Combine(path, "settings.json"));
        var profile = new ConnectionProfile { Name = "Panel world", Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port };
        var window = new MainWindow(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), store, new MemoryPasswordVault(),
            new MemoryRoomMapStore(), new InlineScriptFactory(), new MemoryScriptLibraryStore());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            await window.Sessions.OpenAsync(profile);
            using var server = await listener.AcceptTcpClientAsync(timeout.Token);
            var script = window.Controller.ScriptLibrary.Items[0];
            script.Runtime.Source = "mud.panel('left', { title: 'Cargo', dock: 'left' }).text('log', { text: 'empty' });";
            await script.Runtime.RunAsync();
            Dispatcher.UIThread.RunJobs();
            var panel = Assert.Single(window.Controller.ScriptLibrary.Panels.Panels);
            Assert.Equal("Cargo", panel.Title);
            Assert.Equal("left", panel.Dock);
            var rail = Assert.Single(window.GetVisualDescendants().OfType<ScriptPanelRailView>());
            Assert.True(rail.Shows(panel));
            script.Runtime.Stop();
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(window.Controller.ScriptLibrary.Panels.Panels);
            Assert.Empty(window.Workspace.ScriptPanelTools);
            Assert.False(rail.IsVisible);
        }
        finally
        {
            await window.Sessions.DisposeAsync(); window.Close();
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    private const string TwoPanelScript = """
        const ship = mud.panel("ship", { title: "Ship", dock: "right" });
        ship.gauge("hull", { label: "Hull", value: 80, max: 100 });
        ship.label("status", { text: "In orbit" });
        const cargo = mud.panel("cargo", { title: "Cargo", dock: "right" });
        cargo.label("hold", { text: "Empty hold" });
        mud.alias(/^hideship$/, () => ship.hide());
        mud.alias(/^showship$/, () => ship.show());
        mud.alias(/^closeship$/, () => ship.close());
        mud.alias(/^opennav$/, () => mud.panel("nav", { title: "Nav", dock: "left" }).label("where", { text: "Tatooine" }));
        mud.alias(/^closenav$/, () => mud.panel("nav", { title: "Nav", dock: "left" }).close());
        """;

    private static string[] Shape(IDock dock) => dock.VisibleDockables!.Select(item => item is IProportionalDockSplitter ? "splitter" : item.Id).ToArray();
    private static double[] Shares(IDock dock) => dock.VisibleDockables!.OfType<IDock>().Select(item => Math.Round(item.Proportion, 2)).ToArray();

    [AvaloniaFact]
    public async Task PanelsGetADockOfTheirOwnBesideTheMapAndChannelsAndBelowTheLibrary()
    {
        var path = Path.Combine(Path.GetTempPath(), "wandur-script-panel-dock-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var store = new SettingsStore(Path.Combine(path, "settings.json"));
        var profile = new ConnectionProfile { Name = "Panel world", Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port };
        store.Save(new ClientSettings { Theme = "Ember", Profiles = [profile] });
        var window = new MainWindow(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), store, new MemoryPasswordVault(),
            new MemoryRoomMapStore(), new InlineScriptFactory(), new MemoryScriptLibraryStore()) { Width = 1200, Height = 800 };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            await window.Sessions.OpenAsync(profile);
            using var server = await listener.AcceptTcpClientAsync(timeout.Token);
            var script = window.Controller.ScriptLibrary.Items[0];
            script.Runtime.Source = TwoPanelScript;
            await script.Runtime.RunAsync();
            Assert.True(script.Runtime.IsRunning, script.Runtime.Error);
            Dispatcher.UIThread.RunJobs();
            var panels = window.Controller.ScriptLibrary.Panels.Panels;
            var shipPanel = panels[0];
            var cargoPanel = panels[1];

            // Two right panels are two tabs in the session rail beside the transcript; the first declared one is selected.
            var workspace = window.Workspace;
            Assert.Empty(workspace.ScriptPanelTools);
            Assert.Null(workspace.RightPanelsDock);
            Assert.Equal(new[] { "Ship", "Cargo" }, panels.Select(panel => panel.Title).ToArray());
            var rail = Assert.Single(window.GetVisualDescendants().OfType<ScriptPanelRailView>());
            Assert.True(rail.IsVisible);
            Assert.True(rail.Shows(shipPanel));
            Assert.True(rail.Shows(cargoPanel));
            Assert.Same(shipPanel, rail.SelectedPanel);
            var right = Assert.IsAssignableFrom<IProportionalDock>(workspace.MapTool!.Owner!.Owner);
            Assert.Equal("right", right.Id);
            Assert.Equal(new[] { "map-dock", "splitter", "channels-dock" }, Shape(right));
            Assert.Equal(new[] { 0.58, 0.42 }, Shares(right));
            var mapDock = Assert.IsAssignableFrom<IToolDock>(workspace.MapTool!.Owner);
            Assert.Equal(new IDockable[] { workspace.MapTool }, mapDock.VisibleDockables!);
            var library = Assert.IsAssignableFrom<IToolDock>(workspace.WorldsTool!.Owner);
            Assert.Equal(new IDockable[] { workspace.WorldsTool }, library.VisibleDockables!);
            var layout = Assert.IsAssignableFrom<IProportionalDock>(library.Owner);
            Assert.Equal("layout", layout.Id);
            Assert.Null(workspace.LeftPanelsDock);
            // The open accordion section shows the first declared panel; others stay in the tree collapsed.
            var shown = Assert.Single(window.GetVisualDescendants().OfType<ScriptPanelView>(), candidate => ReferenceEquals(candidate.Panel, shipPanel));
            Assert.Contains(shown.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "In orbit");
            Assert.Same(shipPanel, rail.SelectedPanel);

            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            if (Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR") is { } captures)
            {
                Directory.CreateDirectory(captures);
                using var frame = window.CaptureRenderedFrame();
                frame?.Save(Path.Combine(captures, "panel-dock.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }

            // Hiding takes a panel out of the rail; showing it again returns it to its declared place, ahead of the later panel.
            Assert.True(await window.Controller.SendAsync("hideship"));
            await ScriptSessionTests.WaitFor(() => !panels[0].IsVisible);
            Dispatcher.UIThread.RunJobs();
            Assert.False(workspace.IsScriptPanelVisible(shipPanel));
            Assert.False(rail.Shows(shipPanel));
            Assert.True(rail.Shows(cargoPanel));
            Assert.Same(cargoPanel, rail.SelectedPanel);
            Assert.True(await window.Controller.SendAsync("showship"));
            await ScriptSessionTests.WaitFor(() => shipPanel.IsVisible);
            Dispatcher.UIThread.RunJobs();
            Assert.True(workspace.IsScriptPanelVisible(shipPanel));
            Assert.True(rail.Shows(shipPanel));
            Assert.True(rail.Shows(cargoPanel));

            // A left-declared panel still lives in the same rail beside the transcript; the world library dock is unchanged.
            Assert.True(await window.Controller.SendAsync("opennav"));
            await ScriptSessionTests.WaitFor(() => panels.Count == 3);
            Dispatcher.UIThread.RunJobs();
            Assert.Null(workspace.LeftPanelsDock);
            var nav = panels[2];
            Assert.True(rail.Shows(nav));
            Assert.Same(library, workspace.WorldsTool.Owner);
            Assert.Same(layout, library.Owner);
            Assert.Equal(new[] { "left", "splitter", "documents", "splitter", "right" }, Shape(layout));
            Assert.Equal(0.18, library.Proportion);

            // Closing the nav panel leaves the two right panels in the rail.
            Assert.True(await window.Controller.SendAsync("closenav"));
            await ScriptSessionTests.WaitFor(() => panels.Count == 2);
            Dispatcher.UIThread.RunJobs();
            Assert.Null(workspace.LeftPanelsDock);
            Assert.False(rail.Shows(nav));
            Assert.True(rail.Shows(shipPanel));
            Assert.True(rail.Shows(cargoPanel));

            // The layout can be rebuilt while the panels stay declared: the new session still shows them in the rail.
            window.ResetLayout();
            Dispatcher.UIThread.RunJobs();
            Assert.NotSame(workspace, window.Workspace);
            Assert.Null(workspace.RightPanelsDock);
            workspace = window.Workspace;
            Assert.Null(workspace.RightPanelsDock);
            Assert.Equal(new[] { "Ship", "Cargo" }, window.Controller.ScriptLibrary.Panels.Panels.Select(panel => panel.Title).ToArray());
            rail = Assert.Single(window.GetVisualDescendants().OfType<ScriptPanelRailView>());
            Assert.True(rail.IsVisible);
            Assert.True(rail.Shows(shipPanel));
            Assert.True(rail.Shows(cargoPanel));
            right = Assert.IsAssignableFrom<IProportionalDock>(workspace.MapTool!.Owner!.Owner);
            Assert.Equal(new[] { "map-dock", "splitter", "channels-dock" }, Shape(right));

            // The script closes one panel; the user closes the other from its tab. The rail hides with the last one.
            Assert.True(await window.Controller.SendAsync("closeship"));
            await ScriptSessionTests.WaitFor(() => panels.Count == 1);
            Dispatcher.UIThread.RunJobs();
            Assert.False(rail.Shows(shipPanel));
            Assert.True(rail.Shows(cargoPanel));
            Assert.Same(cargoPanel, rail.SelectedPanel);
            window.Controller.ScriptLibrary.Panels.Close(cargoPanel);
            Dispatcher.UIThread.RunJobs();
            await ScriptSessionTests.WaitFor(() => panels.Count == 0);
            Assert.Empty(panels);
            Assert.Empty(workspace.ScriptPanelTools);
            Assert.Null(workspace.RightPanelsDock);
            Assert.False(rail.IsVisible);
            Assert.Equal(new[] { "map-dock", "splitter", "channels-dock" }, Shape(right));
            Assert.Equal(new[] { 0.58, 0.42 }, Shares(right));
            Assert.Empty(window.GetVisualDescendants().OfType<ScriptPanelView>());
        }
        finally
        {
            await window.Sessions.DisposeAsync(); window.Close();
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    private static byte[] Gmcp(string text) => [255, 250, 201, .. Encoding.UTF8.GetBytes(text), 255, 240];
}
