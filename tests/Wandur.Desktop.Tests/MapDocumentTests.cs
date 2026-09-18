using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Settings;
using Wandur.Desktop.Views;

namespace Wandur.Desktop.Tests;

public sealed class MapDocumentTests
{
    private static MainWindow CreateWindow() => new(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), new SettingsStore(Path.Combine(Path.GetTempPath(), "wandur-map-doc-" + Guid.NewGuid(), "settings.json")),
        new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());

    [AvaloniaFact]
    public async Task MapPanelOpensReusableEditorDocumentAndEditsUpdateBothViews()
    {
        var window = CreateWindow();
        try
        {
            window.Show(); await window.Sessions.OpenAsync(); Dispatcher.UIThread.RunJobs();
            var panel = Assert.Single(window.GetVisualDescendants().OfType<MapView>());
            panel.GetVisualDescendants().OfType<ToggleButton>().Single(b => b.Name == "MapToolsToggle").IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain(panel.GetVisualDescendants().OfType<TextBox>(), c => c.Name == "MapRoomName");
            var open = panel.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "OpenMapEditor");
            Assert.True(open.IsEffectivelyEnabled);
            open.Command!.Execute(null); Dispatcher.UIThread.RunJobs();
            var document = Assert.Single(window.Workspace.MapDocuments);
            Assert.False(panel.Model.IsEditMode);
            Assert.False(panel.GetVisualDescendants().OfType<ToggleButton>().Single(b => b.Name == "MapToolsToggle").IsChecked);
            Assert.True(document.CanFloat);
            Assert.Single(window.GetVisualDescendants().OfType<MapEditorView>());
            window.Workspace.OpenMapEditor(window.Controller);
            Assert.Same(document, Assert.Single(window.Workspace.MapDocuments));
            Assert.NotSame(panel.Model, document.Model);
            Assert.Same(panel.Model.Tracker, document.Model.Tracker);
            var room = document.Model.Snapshot.Rooms[0];
            document.Model.SelectRoom(room.Id);
            document.Model.RoomEditor.Name = "Edited from the document";
            document.Model.SaveRoomCommand.Execute(null);
            Assert.Equal("Edited from the document", panel.Model.Snapshot.Rooms.Single(r => r.Id == room.Id).Name);
            Capture(window, "map-editor-document.png");
            window.Controller.Pages.SelectedPage = Wandur.Desktop.ViewModels.SessionPage.Diagnostics; Dispatcher.UIThread.RunJobs();
            window.Workspace.CloseDockable(document); Dispatcher.UIThread.RunJobs();
            Assert.Empty(window.Workspace.MapDocuments);
            Assert.Equal(Wandur.Desktop.ViewModels.SessionPage.Diagnostics, window.Controller.Pages.SelectedPage);
            Assert.Equal(false, Application.Current!.Resources["DockDocumentControlTabStripVisible"]);
            Assert.True(window.Controller.IsConnected);
        }
        finally { await window.Sessions.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task FloatingEditorStaysWithItsSessionAndClosingSessionRemovesIt()
    {
        var window = CreateWindow();
        try
        {
            window.Show(); await window.Sessions.OpenAsync();
            var first = window.Sessions.Active;
            window.Workspace.OpenMapEditor(first.Controller); Dispatcher.UIThread.RunJobs();
            var document = Assert.Single(window.Workspace.MapDocuments);
            window.Workspace.FloatDockable(document); Dispatcher.UIThread.RunJobs();
            Assert.NotEmpty(window.Workspace.HostWindows!);
            await window.Sessions.OpenAsync(); Dispatcher.UIThread.RunJobs();
            var second = window.Sessions.Active;
            Assert.NotSame(first.Controller.Map, second.Controller.Map);
            Assert.Same(first.Controller.Map, document.Model.Tracker);
            document.OnSelected();
            Assert.Same(first, window.Sessions.Active);
            await window.Sessions.CloseAsync(first); Dispatcher.UIThread.RunJobs();
            Assert.Empty(window.Workspace.MapDocuments);
            Assert.True(document.IsClosed);
            Assert.True(second.Controller.IsConnected);
        }
        finally { await window.Sessions.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task ReconnectingClosesOldEditorBeforeTheSessionMapIsReplaced()
    {
        var window = CreateWindow();
        try
        {
            window.Show(); await window.Sessions.OpenAsync();
            window.Workspace.OpenMapEditor(window.Controller); Dispatcher.UIThread.RunJobs();
            var document = Assert.Single(window.Workspace.MapDocuments);
            var original = document.WorldMap;
            await window.Controller.DisconnectAsync();
            Assert.False(document.IsClosed);
            await window.Controller.StartAsync(); Dispatcher.UIThread.RunJobs();
            Assert.True(document.IsClosed);
            Assert.Empty(window.Workspace.MapDocuments);
            Assert.NotSame(original, window.Controller.Map);
            Assert.Same(original, document.Model.Tracker);
        }
        finally { await window.Sessions.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task RestoringLayoutClosesEditorWithoutDiscardingMapChanges()
    {
        var window = CreateWindow();
        try
        {
            window.Show(); await window.Sessions.OpenAsync();
            window.Workspace.OpenMapEditor(window.Controller); Dispatcher.UIThread.RunJobs();
            var document = Assert.Single(window.Workspace.MapDocuments);
            var room = document.Model.Snapshot.Rooms[0];
            document.Model.SelectRoom(room.Id);
            document.Model.RoomEditor.Notes = "Retained after restoring panels";
            document.Model.SaveRoomCommand.Execute(null);
            window.ResetLayout(); Dispatcher.UIThread.RunJobs();
            Assert.True(document.IsClosed);
            Assert.Empty(window.Workspace.MapDocuments);
            Assert.Equal("Retained after restoring panels", window.Controller.Map.Snapshot.Rooms.Single(r => r.Id == room.Id).Notes);
            Assert.True(window.Controller.IsConnected);
        }
        finally { await window.Sessions.DisposeAsync(); window.Close(); }
    }

    private static void Capture(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
        if (Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR") is { } path)
        { Directory.CreateDirectory(path); frame.Save(Path.Combine(path, name), new Avalonia.Media.Imaging.PngBitmapEncoderOptions()); }
    }
}
