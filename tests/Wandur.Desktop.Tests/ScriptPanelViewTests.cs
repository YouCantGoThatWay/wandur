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

            var tool = Assert.Single(window.Workspace.ScriptPanelTools);
            Assert.Equal("Ship", tool.Title);
            var view = Assert.Single(window.GetVisualDescendants().OfType<ScriptPanelView>());
            var gauge = Assert.Single(view.GetVisualDescendants().OfType<ProgressBar>());
            Assert.Equal(10, gauge.Value);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "In orbit");
            var button = Assert.Single(view.GetVisualDescendants().OfType<Button>());
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

            // A theme switch recolors the panel through the same dynamic resources as the rest of the shell.
            var shell = Assert.IsType<Border>(view.Content);
            var before = Of(shell.Background);
            window.Controller.SaveSettings(window.Controller.Settings with { Theme = "Paper" });
            Dispatcher.UIThread.RunJobs();
            Assert.NotEqual(before, Of(shell.Background));

            Assert.True(await window.Controller.SendAsync("hide"));
            await ScriptSessionTests.WaitFor(() => !window.Controller.ScriptLibrary.Panels.Panels[0].IsVisible);
            Dispatcher.UIThread.RunJobs();
            Assert.False(window.Workspace.IsScriptPanelVisible(tool.Panel));
            Assert.True(await window.Controller.SendAsync("show"));
            await ScriptSessionTests.WaitFor(() => window.Controller.ScriptLibrary.Panels.Panels[0].IsVisible);
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.Workspace.IsScriptPanelVisible(tool.Panel));

            Assert.True(await window.Controller.SendAsync("close"));
            await ScriptSessionTests.WaitFor(() => window.Controller.ScriptLibrary.Panels.Panels.Count == 0);
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(window.Workspace.ScriptPanelTools);
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
            var tool = Assert.Single(window.Workspace.ScriptPanelTools);
            Assert.Equal("Cargo", tool.Title);
            Assert.Equal("left", tool.Panel.Dock);
            script.Runtime.Stop();
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(window.Controller.ScriptLibrary.Panels.Panels);
            Assert.Empty(window.Workspace.ScriptPanelTools);
        }
        finally
        {
            await window.Sessions.DisposeAsync(); window.Close();
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    private static byte[] Gmcp(string text) => [255, 250, 201, .. Encoding.UTF8.GetBytes(text), 255, 240];
}
