using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Mapping;
using Wandur.Core.Settings;
using Wandur.Desktop.ViewModels;
using Wandur.Desktop.Views;

namespace Wandur.Desktop.Tests;

public sealed class MapAutoCenterTests
{
    private static RoomObservation At(string id, double x, double y) =>
        new(id, "Room " + id, "", new Dictionary<string, string?>(), Source: RoomDataSource.Gmcp) { X = x, Y = y };

    [Fact]
    public void AutoCenterFollowsTheCurrentRoomAndCanBeTurnedOff()
    {
        var tracker = new RoomMapTracker();
        var model = new MapViewModel(tracker); model.Attach();
        Assert.True(model.AutoCenter);
        tracker.Observe(At("1", 0, 0));
        tracker.Observe(At("2", 5, 3), "east");
        Assert.Equal(5, model.CenterX); Assert.Equal(3, model.CenterY);
        model.Pan(40, 40);
        tracker.Observe(At("3", 9, 9), "east");
        Assert.Equal(9, model.CenterX); Assert.Equal(9, model.CenterY); Assert.Equal(0, model.PanX);
        model.AutoCenter = false;
        tracker.Observe(At("4", 20, 20), "east");
        Assert.Equal(9, model.CenterX);
        model.AutoCenter = true;
        Assert.Equal(20, model.CenterX); Assert.Equal(20, model.CenterY);
        model.Detach();
    }

    [AvaloniaFact]
    public void ToolbarToggleBindsAutoCenter()
    {
        var model = new MapViewModel(new RoomMapTracker()); model.Attach();
        var view = new MapView(model);
        var window = new Window { Content = view, Width = 600, Height = 400 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var toggle = view.GetVisualDescendants().OfType<ToggleButton>().Single(t => t.Name == "MapAutoCenterToggle");
            Assert.True(toggle.IsChecked);
            toggle.IsChecked = false; Dispatcher.UIThread.RunJobs();
            Assert.False(model.AutoCenter);
        }
        finally { window.Close(); model.Detach(); }
    }

    [AvaloniaFact]
    public async Task AutoCenterPersistsThroughTheControllerSettingsStore()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wandur-autocenter-" + Guid.NewGuid());
        var settingsPath = Path.Combine(directory, "settings.json");
        await using var controller = new Wandur.Desktop.WorkspaceController(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(),
            new SettingsStore(settingsPath), new MemoryPasswordVault(), new MemoryRoomMapStore(),
            new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        var model = new MapViewModel(controller); model.Attach();
        try
        {
            Assert.True(model.AutoCenter);
            model.AutoCenter = false;
            Assert.False(controller.Settings.MapAutoCenter);
            Assert.False(new SettingsStore(settingsPath).Load().Settings.MapAutoCenter);
            model.AutoCenter = true;
            Assert.True(controller.Settings.MapAutoCenter);
            Assert.True(new SettingsStore(settingsPath).Load().Settings.MapAutoCenter);
        }
        finally { model.Detach(); }
    }
}
