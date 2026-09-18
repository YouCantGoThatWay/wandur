using Avalonia.Controls;
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

            var header = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "WindowHeader");
            window.ToolbarVisible = false;
            window.UpdateLayout();
            if (OperatingSystem.IsMacOS()) Assert.True(header.Bounds.Height >= 44);
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
}
