using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Classification;
using Wandur.Core.Settings;
using Wandur.Desktop;
using Wandur.Desktop.Views;

namespace Wandur.Desktop.Tests;

public sealed class RoomInferenceToolsTests
{
    private sealed class FakeClassifier : IRoomEnvironmentClassifier
    {
        public string ModelVersion => "9.9.9"; public double DefaultThreshold => 0.8;
        public RoomEnvironmentPrediction? Classify(string name, string description, double threshold) => null;
    }

    [AvaloniaFact]
    public async Task ToolsSectionShowsStatusAndBindsEnableSetting()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wandur-tools-" + Guid.NewGuid());
        await using var controller = new WorkspaceController(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(),
            new SettingsStore(Path.Combine(directory, "settings.json")), new MemoryPasswordVault(), new MemoryRoomMapStore(),
            new RecordingScriptFactory(), new MemoryScriptLibraryStore(), classification: RoomClassificationService.ForTesting(new FakeClassifier()));
        var view = new MapView(controller);
        var window = new Window { Content = view, Width = 700, Height = 500 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var toggle = view.GetVisualDescendants().OfType<ToggleButton>().Single(t => t.Name == "MapToolsToggle");
            toggle.IsChecked = true; Dispatcher.UIThread.RunJobs();
            var section = view.GetVisualDescendants().OfType<Expander>().Single(e => e.Name == "MapInferenceSection");
            Assert.False(section.IsExpanded);
            section.IsExpanded = true; Dispatcher.UIThread.RunJobs();
            var status = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "MapInferenceStatus");
            Assert.Contains("9.9.9", status.Text);
            var enable = view.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "MapInferenceEnable");
            Assert.True(enable.IsChecked);
            enable.IsChecked = false; Dispatcher.UIThread.RunJobs();
            Assert.False(controller.Settings.ClassifyRoomsLocally);
            Assert.False(view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "MapInferenceDownload").IsEnabled); // already installed
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task WithoutServiceTheSectionIsHidden()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wandur-tools-" + Guid.NewGuid());
        await using var controller = new WorkspaceController(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(),
            new SettingsStore(Path.Combine(directory, "settings.json")), new MemoryPasswordVault(), new MemoryRoomMapStore(),
            new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        var view = new MapView(controller);
        var window = new Window { Content = view, Width = 700, Height = 500 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Name == "MapInferenceStatus");
        }
        finally { window.Close(); }
    }
}
