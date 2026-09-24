using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Wandur.Core.History;
using Wandur.Core.Settings;
using Wandur.Core.Storage;
using Wandur.Desktop.Terminal;

namespace Wandur.Desktop.Tests;

public sealed class SessionHistoryCaptureTests
{
    [AvaloniaFact]
    public async Task LoopbackCaptureMasksSplitEchoesAndDiscardsPartialLinesAcrossPrivateMode()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wandur-history-wire-" + Guid.NewGuid());
        var settings = new SettingsStore(Path.Combine(directory, "settings.json"));
        var store = new SqliteHistoryStore(new ClientDatabase(Path.Combine(directory, "history.db")));
        using var server = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        server.Start();
        var profile = new ConnectionProfile { Name = "Starfall", Host = "127.0.0.1", Port = ((System.Net.IPEndPoint)server.LocalEndpoint).Port };
        var controller = new WorkspaceController(new TranscriptDisplayFactory(), settings, new MemoryPasswordVault(),
            new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore(), history: store);
        try
        {
            await controller.StartAsync(profile);
            using var peer = await server.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            async Task Output(string text, string expected)
            {
                await peer.GetStream().WriteAsync(System.Text.Encoding.UTF8.GetBytes(text), TestContext.Current.CancellationToken);
                for (var i = 0; i < 100; i++)
                {
                    await Task.Delay(10, TestContext.Current.CancellationToken);
                    Dispatcher.UIThread.RunJobs(); controller.FlushOutput();
                    if (controller.Terminal.PlainText.Contains(expected)) return;
                }
                Assert.Fail("Loopback output did not reach the transcript.");
            }
            await Output("unfinished", "unfinished");
            controller.SetManualPrivate(true);
            controller.SetManualPrivate(false);
            await Output("visible\r\n", "visible");
            controller.SetManualPrivate(true);
            await controller.SendAsync("hunter2");
            controller.SetManualPrivate(false);
            await Output("echo hun", "echo hun");
            await Output("\u001b[32mter2\u001b[0m done\r\n", "hunter2 done");
            await controller.DisconnectAsync();
            var session = Assert.Single(await Task.Run(() => store.Sessions(new())));
            var entries = await Task.Run(() => store.Entries(session.Id));
            Assert.Contains(entries, e => e.Text == "visible");
            Assert.Contains(entries, e => e.Text == "echo [redacted] done");
            Assert.DoesNotContain(entries, e => e.Text.Contains("unfinished") || e.Text.Contains("hunter2"));
            Assert.Empty(await Task.Run(() => store.Search("hunter2", new())));
        }
        finally { await controller.DisposeAsync(); }
    }

    [AvaloniaFact]
    public async Task CapturesDemoCommandsPrivatelyAndCanDisableAndResumeRecording()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wandur-history-" + Guid.NewGuid());
        var settings = new SettingsStore(Path.Combine(directory, "settings.json"));
        var store = new SqliteHistoryStore(new ClientDatabase(Path.Combine(directory, "history.db")));
        var controller = new WorkspaceController(new TranscriptDisplayFactory(), settings, new MemoryPasswordVault(),
            new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore(), history: store);
        try
        {
            await controller.StartAsync(); Dispatcher.UIThread.RunJobs(); controller.FlushOutput();
            Assert.True(await controller.SendAsync("look"));
            controller.SetManualPrivate(true);
            Assert.True(await controller.SendAsync("violet-secret"));
            controller.FlushOutput();
            controller.SetManualPrivate(false);
            await controller.FlushHistoryAsync();
            Assert.NotEmpty(await Task.Run(() => store.Search("look", new())));
            Assert.Empty(await Task.Run(() => store.Search("violet", new())));
            controller.SaveSettings(controller.Settings with { HistoryEnabled = false });
            await controller.SendAsync("unrecordedword"); controller.FlushOutput();
            await controller.FlushHistoryAsync();
            Assert.Empty(await Task.Run(() => store.Search("unrecordedword", new())));
            controller.SaveSettings(controller.Settings with { HistoryEnabled = true, HistoryRetentionDays = 0 });
            await controller.SendAsync("resumedword"); controller.FlushOutput();
            await controller.DisconnectAsync();
            Assert.NotEmpty(await Task.Run(() => store.Search("resumedword", new())));
            Assert.Equal(2, (await Task.Run(() => store.Sessions(new()))).Count);
            Assert.All(await Task.Run(() => store.Sessions(new())), s => Assert.NotNull(s.EndedAt));
        }
        finally { await controller.DisposeAsync(); }
    }

    [AvaloniaFact]
    public async Task PreferencesDoNotPruneOrDisableHistoryUntilSaved()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wandur-history-prefs-" + Guid.NewGuid());
        var settings = new SettingsStore(Path.Combine(directory, "settings.json"));
        var controller = new WorkspaceController(new TranscriptDisplayFactory(), settings, new MemoryPasswordVault(),
            new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        try
        {
            using (var model = new ViewModels.PreferencesViewModel(controller, _ => { }))
            {
                Assert.True(model.HistoryEnabled);
                model.HistoryEnabled = false; model.HistoryRetentionDays = 365;
                Assert.True(controller.Settings.HistoryEnabled);
                Assert.Equal(30, controller.Settings.HistoryRetentionDays);
            }
            using (var model = new ViewModels.PreferencesViewModel(controller, _ => { }))
            {
                model.HistoryEnabled = false; model.HistoryRetentionDays = 0;
                model.SaveCommand.Execute(null);
            }
            Assert.False(settings.Load().Settings.HistoryEnabled);
            Assert.Equal(0, settings.Load().Settings.HistoryRetentionDays);
        }
        finally { await controller.DisposeAsync(); }
    }
}
