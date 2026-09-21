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

            // Identity on the left, controls on the right, and the live session state down in the footer.
            var identity = window.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "TitleBarIdentity");
            var title = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "AppTitle");
            Assert.Equal("Wandur", title.Text);
            Assert.Equal(FontWeight.SemiBold, title.FontWeight);
            Assert.Contains(logo, identity.Children);
            Assert.Equal(0, Grid.GetColumn(identity));
            var picker = window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "ToolbarWorlds");
            var controls = picker.GetSelfAndVisualAncestors().OfType<StackPanel>().First(p => Grid.GetColumn(p) == 2);
            Assert.NotSame(identity, controls);
            var status = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "SessionStatus");
            Assert.Contains(status.GetSelfAndVisualAncestors().OfType<StackPanel>(), p => p.Name == "FooterStatus");

            var header = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "WindowHeader");
            var toolbar = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "MainToolbar");
            Assert.True(toolbar.Bounds.Height >= 40);
            window.ToolbarVisible = false;
            window.UpdateLayout();
            if (OperatingSystem.IsMacOS()) Assert.True(header.Bounds.Height >= 52);
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
