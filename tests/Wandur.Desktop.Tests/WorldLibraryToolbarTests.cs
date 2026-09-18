using Avalonia.Controls;
using Avalonia;
using Avalonia.Input;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Wandur.Core.Settings;
using Wandur.Desktop.Security;
using Wandur.Desktop.Views;

namespace Wandur.Desktop.Tests;

public sealed class WorldLibraryToolbarTests
{
    [AvaloniaFact]
    public async Task RightClickAnywhereOnRowOffersEditAndDeleteForThatWorld()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wandur-context-" + Guid.NewGuid());
        var store = new SettingsStore(Path.Combine(directory, "settings.json"));
        var first = new ConnectionProfile { Name = "First", Host = "first.example" };
        var second = new ConnectionProfile { Name = "Second", Host = "second.example" };
        store.Save(new ClientSettings { Profiles = [first, second] });
        await using var sessions = new SessionWorkspace(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), store, new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        ConnectionProfile? edited = null;
        var panel = new WorldLibraryView(sessions, () => { }, editProfile: p => edited = p);
        var window = new Window { Content = panel, Width = 285, Height = 400 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var list = panel.GetVisualDescendants().OfType<ListBox>().Single();
            list.SelectedItem = first;
            var row = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(1));
            var point = row.TranslatePoint(new Point(row.Bounds.Width - 2, row.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Right); window.MouseUp(point, MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(row.ContextMenu); Assert.True(row.ContextMenu.IsOpen);
            Assert.Equal(second, list.SelectedItem);
            var edit = row.ContextMenu.Items.OfType<MenuItem>().Single(m => m.Name == "EditWorldMenu");
            edit.Command!.Execute(edit.CommandParameter);
            Assert.Equal(second, edited);
            var delete = row.ContextMenu.Items.OfType<MenuItem>().Single(m => m.Name == "DeleteWorldMenu");
            // The command retains the clicked world even if selection changes before execution.
            list.SelectedItem = first;
            await Assert.IsAssignableFrom<IAsyncRelayCommand>(delete.Command).ExecuteAsync(delete.CommandParameter);
            Assert.Equal(first, Assert.Single(store.Load().Settings.Profiles));
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteTargetsSelectedProfileAndPreservesCredentialsWhenSaveFails(bool failSave)
    {
        var directory = Path.Combine(Path.GetTempPath(), "wandur-library-" + Guid.NewGuid());
        var store = new SettingsStore(Path.Combine(directory, "settings.json"));
        var vault = new MemoryPasswordVault();
        await using var sessions = new SessionWorkspace(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), store, vault, new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        var controller = sessions.Active.Controller;
        await controller.SaveWorldAsync(new() { Name = "First world", Host = "first.example", Username = "first" }, "first-secret", true);
        await controller.SaveWorldAsync(new() { Name = "Second world", Host = "second.example", Username = "second" }, "second-secret", true);
        var first = controller.Settings.Profiles[0]; var second = controller.Settings.Profiles[1];
        await sessions.OpenAsync();
        var panel = new WorldLibraryView(sessions, () => { }, () => { }, _ => { });
        var window = new Window { Content = panel, Width = 285, Height = 500 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var delete = panel.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "DeleteSavedWorld");
            var list = panel.GetVisualDescendants().OfType<ListBox>().Single();
            list.SelectedItem = second; Dispatcher.UIThread.RunJobs();
            if (Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR") is { } captures)
            {
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
                using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
                Directory.CreateDirectory(captures); frame.Save(Path.Combine(captures, "worlds-toolbar.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
            if (failSave) { File.Delete(store.FilePath); Directory.CreateDirectory(store.FilePath); }
            await Assert.IsAssignableFrom<IAsyncRelayCommand>(delete.Command).ExecuteAsync(null);
            Assert.True(controller.IsConnected);
            Assert.Single(sessions.Tabs);
            Assert.Equal("first-secret", await vault.ReadAsync(PasswordVault.Key(first)));
            if (failSave)
            {
                Assert.Equal(2, controller.Settings.Profiles.Count);
                Assert.Equal("second-secret", await vault.ReadAsync(PasswordVault.Key(second)));
                Assert.False(string.IsNullOrWhiteSpace(controller.Notice));
            }
            else
            {
                Assert.Equal(first, Assert.Single(store.Load().Settings.Profiles));
                Assert.Null(await vault.ReadAsync(PasswordVault.Key(second)));
                Assert.Equal(first, list.SelectedItem);
                await Assert.IsAssignableFrom<IAsyncRelayCommand>(delete.Command).ExecuteAsync(null);
                Assert.Empty(controller.Settings.Profiles);
                Assert.False(delete.IsEffectivelyEnabled);
                Assert.False(panel.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "EditSavedWorld").IsEffectivelyEnabled);
            }
        }
        finally { window.Close(); if (Directory.Exists(store.FilePath)) Directory.Delete(store.FilePath); }
    }
}
