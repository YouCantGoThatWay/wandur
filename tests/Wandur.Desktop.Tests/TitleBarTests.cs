using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Settings;
using Wandur.Desktop;

namespace Wandur.Desktop.Tests;

public sealed class TitleBarTests
{
    [AvaloniaFact]
    public async Task ToolbarKeepsItsIconAndNativeDragSpaceWhenSessionStateChanges()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wandur-titlebar-" + Guid.NewGuid());
        var window = new MainWindow(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), new SettingsStore(Path.Combine(directory, "settings.json")),
            new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var disconnect = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "Disconnect");
            var icon = Assert.IsType<Avalonia.Controls.Shapes.Path>(disconnect.Content);
            await window.Controller.StartAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.Same(icon, disconnect.Content);
            Assert.True(disconnect.IsEffectivelyEnabled);

            var logo = window.GetVisualDescendants().OfType<Image>().Single(i => i.Name == "AppLogo");
            Assert.NotNull(logo.Source);

            // The live session state remains in the footer rather than crowding the toolbar.
            // AppTitle tracks the window Title (character · world · Wandur) once a session is open.
            var identity = window.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "TitleBarIdentity");
            var title = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "AppTitle");
            // The nameplate carries the world alone; the full string (character · world · Wandur) stays on
            // the operating system's window title, where there is room for it.
            Assert.Equal(window.Controller.WorldName, title.Text, ignoreCase: true);   // engraved in capitals
            Assert.Contains(window.Controller.WorldName, window.Title);
            Assert.Contains("Wandur", window.Title);
            Assert.Equal(FontWeight.Normal, title.FontWeight);
            Assert.Contains(logo, identity.Children);
            Assert.Equal(0, Grid.GetColumn(identity));
            var picker = window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "ToolbarWorlds");
            var toolbar = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "MainToolbar");
            // Hull keeps world selection on the left of the toolbar, separate from native drag chrome.
            var pickerOrigin = picker.TranslatePoint(default, toolbar)!.Value;
            Assert.InRange(pickerOrigin.X, 0, 32);
            Assert.InRange(pickerOrigin.Y, 0, toolbar.Bounds.Height - picker.Bounds.Height);
            var status = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "SessionStatus");
            Assert.Contains(status.GetSelfAndVisualAncestors().OfType<StackPanel>(), p => p.Name == "FooterStatus");

            var header = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "WindowHeader");
            Assert.True(toolbar.Bounds.Height >= 40);
            window.ToolbarVisible = false;
            window.UpdateLayout();
            // Hiding the toolbar must never take away the draggable strip the traffic lights sit in. With the
            // title band up, that strip is the band's own drag surface rather than the old header row.
            var bandDrag = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "MetalHeaderDrag");
            if (OperatingSystem.IsMacOS())
                Assert.True(header.Bounds.Height >= 50 || (bandDrag.IsVisible && bandDrag.Bounds.Height >= 50),
                    $"no draggable title area: header {header.Bounds.Height}, band {bandDrag.Bounds.Height}");
            else Assert.Equal(0, header.MinHeight);
            Assert.False(disconnect.IsEffectivelyVisible);
            Assert.True(window.Controller.IsConnected);
            window.ToolbarVisible = true;
            window.UpdateLayout();
            Assert.True(disconnect.IsEffectivelyVisible);
        }
        finally
        {
            await window.Sessions.DisposeAsync();
            window.Close();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [AvaloniaFact]
    public async Task ToolbarWorldPickerFollowsTheActiveSessionProfile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wandur-picker-" + Guid.NewGuid());
        var store = new SettingsStore(Path.Combine(directory, "settings.json"));
        var first = new ConnectionProfile { Name = "First", Host = "first.example", Port = 4000 };
        var second = new ConnectionProfile { Name = "Second", Host = "127.0.0.1", Port = 0 };
        // Listener for a real connect so the session profile sticks.
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        second = second with { Port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port };
        store.Save(new ClientSettings { Profiles = [first, second] });
        var window = new MainWindow(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), store,
            new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var picker = window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "ToolbarWorlds");
            Assert.Equal(first.Id, Assert.IsType<ConnectionProfile>(picker.SelectedItem).Id);

            var open = window.Sessions.OpenAsync(second);
            using var _ = await listener.AcceptTcpClientAsync();
            await open;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(second.Id, Assert.IsType<ConnectionProfile>(picker.SelectedItem).Id);
        }
        finally
        {
            await window.Sessions.DisposeAsync();
            window.Close();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
