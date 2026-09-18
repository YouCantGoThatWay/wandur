using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Settings;
using Wandur.Desktop.Views;
using Wandur.Desktop.ViewModels;

namespace Wandur.Desktop.Tests;

public sealed class WorkspaceNavigationTests
{
    private static MainWindow CreateWindow() => new(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(),
        new SettingsStore(Path.Combine(Path.GetTempPath(), "wandur-navigation-" + Guid.NewGuid(), "settings.json")),
        new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());

    [AvaloniaFact]
    public async Task DirectoryAlwaysOpensInCenterAndPreservesConnection()
    {
        var window = CreateWindow();
        try
        {
            window.Show(); await window.Sessions.OpenAsync(); Dispatcher.UIThread.RunJobs();
            var session = window.Sessions.Active;
            await window.BrowseWorldsAsync(); Dispatcher.UIThread.RunJobs();
            Assert.Single(window.GetVisualDescendants().OfType<WorldBrowserView>());
            Assert.Empty(window.OwnedWindows);
            Assert.Same(session, window.Sessions.Active);
            Assert.Single(window.Sessions.Tabs);
            Assert.True(session.Controller.IsConnected);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<TabStrip>(), t => t.Name == "SessionTabs");
            window.Workspace.Navigation.Selected = window.Workspace.Navigation.Entries.Single(e => e.Key == session);
            Dispatcher.UIThread.RunJobs();
            Assert.Single(window.GetVisualDescendants().OfType<TerminalView>());
            Assert.False(window.Sessions.IsBrowsing);
            Capture(window, "workspace-session.png");
            await window.BrowseWorldsAsync(); Dispatcher.UIThread.RunJobs();
            Capture(window, "workspace-search.png");
        }
        finally { await window.Sessions.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task EditorNavigationAndClosingDoNotCloseSessionsAndKeyboardCyclesAllEntries()
    {
        var window = CreateWindow();
        try
        {
            window.Show(); await window.Sessions.OpenAsync(); Dispatcher.UIThread.RunJobs();
            var first = window.Sessions.Active;
            window.Workspace.OpenMapEditor(first.Controller); Dispatcher.UIThread.RunJobs();
            var script = Assert.Single(window.Workspace.MapDocuments);
            Assert.Same(script, window.Workspace.Navigation.Selected!.Key);
            Assert.Equal(false, Application.Current!.Resources["DockDocumentControlTabStripVisible"]);
            await window.Sessions.OpenAsync(); Dispatcher.UIThread.RunJobs();
            var second = window.Sessions.Active;
            Assert.NotSame(first, second);
            var rows = window.Workspace.Navigation.Entries.Where(e => e.Key is SessionTab).ToArray();
            Assert.Equal(2, rows.Length);
            Assert.NotEqual(rows[0].Title, rows[1].Title);
            window.Workspace.Navigation.Selected = window.Workspace.Navigation.Entries.Single(e => e.Key == script);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(first, window.Sessions.Active);
            Assert.Single(window.GetVisualDescendants().OfType<MapEditorView>());
            await window.Workspace.CloseSelectedAsync(); Dispatcher.UIThread.RunJobs();
            Assert.True(script.IsClosed);
            Assert.True(first.Controller.IsConnected);
            Assert.True(second.Controller.IsConnected);
            window.Workspace.ShowSearch();
            window.Workspace.SelectNextDocument(1); Dispatcher.UIThread.RunJobs();
            Assert.Same(first, window.Sessions.Active);
            window.Workspace.SelectNextDocument(1); Dispatcher.UIThread.RunJobs();
            Assert.Same(second, window.Sessions.Active);
            await window.Workspace.CloseSelectedAsync(); Dispatcher.UIThread.RunJobs();
            Assert.Single(window.Sessions.Tabs);
            Assert.Same(first, window.Sessions.Active);
        }
        finally { await window.Sessions.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task DuplicateSavedConnectionGetsIndependentSessionIdsNamesAndPages()
    {
        var window = CreateWindow();
        using var server = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        server.Start();
        var profile = new ConnectionProfile { Name = "One world", Host = "127.0.0.1", Port = ((System.Net.IPEndPoint)server.LocalEndpoint).Port };
        try
        {
            window.Show();
            await window.Sessions.OpenAsync(profile);
            using var socket1 = await server.AcceptTcpClientAsync();
            var first = window.Sessions.Active;
            first.Controller.Pages.SelectedPage = SessionPage.Diagnostics;
            await window.Sessions.OpenAsync(profile);
            using var socket2 = await server.AcceptTcpClientAsync();
            var second = window.Sessions.Active;
            Assert.NotEqual(first.Id, second.Id);
            Assert.Equal(SessionPage.Play, second.Controller.Pages.SelectedPage);
            Assert.Equal(SessionPage.Diagnostics, first.Controller.Pages.SelectedPage);
            window.Sessions.Rename(first, "Ackbar — trading");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Ackbar — trading", window.Workspace.Navigation.OpenEntries.Single(e => e.Key == first).Title);
            window.Workspace.Navigation.Selected = window.Workspace.Navigation.OpenEntries.Single(e => e.Key == first);
            first.Controller.Pages.SelectedPage = SessionPage.Diagnostics; Dispatcher.UIThread.RunJobs();
            Assert.Equal(SessionPage.Diagnostics, first.Controller.Pages.SelectedPage);
            Assert.True(first.Controller.IsConnected); Assert.True(second.Controller.IsConnected);
            Assert.False(window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "CommandInput").IsEffectivelyVisible);
            Assert.Empty(window.GetVisualDescendants().OfType<ScriptLibraryView>());
            Capture(window, "workspace-session-diagnostics.png");
            var secondRow = window.Workspace.Navigation.OpenEntries.Single(e => e.Key == second);
            secondRow.RenameCommand.Execute(null);
            window.Workspace.Navigation.RenameText = "Alt — exploring";
            window.Workspace.Navigation.SaveNameCommand.Execute(null);
            Assert.Equal("Alt — exploring", second.CustomName);
            Assert.Equal(2, window.Sessions.Tabs.Count);
        }
        finally { await window.Sessions.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task CloseMenuTargetsTheNewWorkspaceAfterRestoringLayout()
    {
        var window = CreateWindow();
        try
        {
            window.Show(); await window.Sessions.OpenAsync(); Dispatcher.UIThread.RunJobs();
            var session = window.Sessions.Active;
            window.ResetLayout(); Dispatcher.UIThread.RunJobs();
            var file = NativeMenu.GetMenu(window)!.Items.OfType<NativeMenuItem>().Single(i => i.Header == Wandur.Core.Localization.Strings.File);
            file.Menu!.Items.OfType<NativeMenuItem>().Single(i => i.Header == Wandur.Core.Localization.Strings.CloseWorkspaceItem).Command!.Execute(null);
            for (var i = 0; i < 10 && window.Sessions.Tabs.Contains(session); i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
            Assert.DoesNotContain(session, window.Sessions.Tabs);
            Assert.True(window.Sessions.IsBrowsing);
        }
        finally { await window.Sessions.DisposeAsync(); window.Close(); }
    }

    private static void Capture(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        using var bitmap = window.CaptureRenderedFrame();
        bitmap?.Save(Path.Combine(dir, name), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
