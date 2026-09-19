using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Discovery;
using Wandur.Core.Scripting;
using Wandur.Core.Settings;
using Wandur.Desktop.Services;
using Wandur.Desktop.ViewModels;
using Wandur.Desktop.Views;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.Tests;

public sealed class ScriptPackTests
{
    private const string FirstVersion = "mud.panel('ship', { title: 'Ship' }).label('a', { text: 'v1' });";
    private const string SecondVersion = "mud.panel('ship', { title: 'Ship' }).label('a', { text: 'v2' });";

    private static void WriteDirectory(string path, WorldListing world) => File.WriteAllText(path,
        JsonSerializer.Serialize(new { schema_version = 2, format = "wandur.directory", fetched_at = DateTimeOffset.UtcNow, worlds = new[] { world } },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));

    private static WorldListing Listing(int port, string source, int version) => new()
    {
        Id = "lotj", Name = "Legends of the Jedi", Host = "127.0.0.1", Port = port,
        Scripts =
        [
            new() { Id = "ship-panel", Name = "Ship panel", Description = "Ship telemetry.", Source = source, Provenance = ScriptPackInfo.Generated, Version = version },
            // A provenance this client does not support is ignored rather than attached.
            new() { Id = "community", Name = "Community", Description = "", Source = "mud.echo('x');", Provenance = "community", Version = 1 }
        ]
    };

    private const string ThirdVersion = "mud.panel('ship', { title: 'Ship' }).label('a', { text: 'v3' });";

    /// <summary>Serves whatever the directory file on disk holds at request time, so a pack change on the server is one file write.</summary>
    private sealed class FileServedDirectory(string path) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(File.ReadAllText(path)) });
        }
    }

    private sealed class CatalogClock : TimeProvider
    {
        public long Seconds;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Seconds;
    }

    /// <summary>The owner opened a world minutes after regenerating its pack and got the previous scripts: the catalog
    /// served its five minute snapshot. An open now refreshes a snapshot older than a minute first.</summary>
    [AvaloniaFact]
    public async Task OpeningAWorldRefreshesAStaleDirectorySnapshotSoAChangedPackIsAppliedAtOnce()
    {
        var path = Path.Combine(Path.GetTempPath(), "wandur-script-pack-fresh-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        var directory = Path.Combine(path, "server-directory.json");
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var store = new SettingsStore(Path.Combine(path, "settings.json"));
        var profile = Listing(port, FirstVersion, 1).ToProfile();
        store.Save(new ClientSettings { Profiles = [profile] });
        WriteDirectory(directory, Listing(port, FirstVersion, 1));
        var clock = new CatalogClock();
        using var served = new FileServedDirectory(directory);
        using var catalog = new WorldCatalog(Path.Combine(path, "cache.json"), http: new HttpClient(served), timeProvider: clock);
        await catalog.LoadAsync();
        Assert.Equal(1, served.Calls);
        await using var sessions = new SessionWorkspace(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), store,
            new MemoryPasswordVault(), new MemoryRoomMapStore(), new InlineScriptFactory(), new MemoryScriptLibraryStore(), catalog: catalog);
        try
        {
            // Fresh snapshot: the open uses it as it is.
            await sessions.OpenAsync(profile);
            Assert.Equal(1, served.Calls);
            Assert.Equal(FirstVersion, Assert.Single(sessions.Active.Controller.ScriptLibrary.Items, entry => entry.IsPack).Source);

            // The pack changes on the server; the next open, 90 seconds later and well inside the five minute cadence, sees it.
            WriteDirectory(directory, Listing(port, SecondVersion, 2));
            clock.Seconds = 90;
            await sessions.OpenAsync(profile);
            Assert.Equal(2, served.Calls);
            var refreshed = Assert.Single(sessions.Active.Controller.ScriptLibrary.Items, entry => entry.IsPack);
            Assert.Equal(SecondVersion, refreshed.Source);
            Assert.Equal(2, refreshed.Pack!.Version);

            // Within a minute of that fetch the open goes ahead with the snapshot in hand.
            WriteDirectory(directory, Listing(port, ThirdVersion, 3));
            clock.Seconds = 100;
            await sessions.OpenAsync(profile);
            Assert.Equal(2, served.Calls);
            Assert.Equal(SecondVersion, Assert.Single(sessions.Active.Controller.ScriptLibrary.Items, entry => entry.IsPack).Source);
        }
        finally
        {
            foreach (var tab in sessions.Tabs.ToArray()) await tab.Controller.DisconnectAsync();
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    [AvaloniaFact]
    public async Task ASuppliedScriptIsAttachedOnOpenMarkedAsAPackAndRefreshedWhenItsVersionChanges()
    {
        var path = Path.Combine(Path.GetTempPath(), "wandur-script-pack-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        var directory = Path.Combine(path, "directory.json");
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var store = new SettingsStore(Path.Combine(path, "settings.json"));
        var scripts = new MemoryScriptLibraryStore();
        var profile = Listing(port, FirstVersion, 1).ToProfile();
        store.Save(new ClientSettings { Profiles = [profile] });
        WriteDirectory(directory, Listing(port, FirstVersion, 1));
        var key = $"127.0.0.1:{port}:False";

        WorldScriptEntry pack;
        using (var catalog = new WorldCatalog(directory, http: OfflineHttp.Client()))
        {
            await using var sessions = new SessionWorkspace(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), store,
                new MemoryPasswordVault(), new MemoryRoomMapStore(), new InlineScriptFactory(), scripts, catalog: catalog);
            await sessions.OpenAsync(profile);
            var library = sessions.Active.Controller.ScriptLibrary;
            pack = Assert.Single(library.Items, entry => entry.IsPack);
            Assert.Equal("Ship panel", pack.Name);
            Assert.Equal(FirstVersion, pack.Source);
            Assert.Equal(ScriptPackInfo.Generated, pack.Pack!.Provenance);
            Assert.Equal(1, pack.Pack.Version);
            Assert.Equal("Ship telemetry.", pack.Pack.Description);
            Assert.True(pack.Enabled);
            Assert.False(pack.AllowSend);
            Assert.Equal(ScriptPackInfo.IdFor(key, "ship-panel"), pack.Id);
            // The unsupported provenance never became a script, and the hand-written entry is untouched.
            Assert.Equal(2, library.Items.Count);
            Assert.Equal(ScriptExamples.Starter, Assert.Single(library.Items, entry => !entry.IsPack).Source);

            var model = new ScriptLibraryViewModel(library);
            var window = new Window { Width = 900, Height = 560, Content = new ScriptLibraryView(model) };
            try
            {
                window.Show(); Dispatcher.UIThread.RunJobs();
                model.Selected = model.Items.Single(item => item.IsPack);
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                Assert.True(model.IsPack);
                Assert.True(model.IsReadOnly);
                Assert.False(model.SaveCommand.CanExecute(null));
                var provenance = window.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Name == "PackScriptProvenance");
                Assert.Equal(L.ScriptPackMarker + " · " + L.ScriptPackGenerated, provenance.Text);
                Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
                    block => block.Name == "WorldScriptPackMarker" && block.IsVisible && block.Text == provenance.Text);
                Assert.True(window.GetVisualDescendants().OfType<ScriptCodeEditor>().Single(editor => editor.Name == "WorldScriptSource").IsReadOnly);

                // The toggle is the user's per-script choice, and it is persisted with the library.
                var allow = window.GetVisualDescendants().OfType<CheckBox>().Single(box => box.Name == "AllowPackScriptSend");
                Assert.True(allow.IsVisible);
                Assert.False(allow.IsChecked);
                allow.IsChecked = true;
                await ScriptSessionTests.WaitFor(() => Assert.Single(scripts.Load(key), script => script.Id == pack.Id).AllowSend);
                // A user who turns a pack script off must find it off after a refresh.
                await library.SetEnabledAsync(pack, false);
                Assert.False(Assert.Single(scripts.Load(key), script => script.Id == pack.Id).Enabled);
            }
            finally { model.Dispose(); window.Close(); }
        }

        WriteDirectory(directory, Listing(port, SecondVersion, 2));
        using (var catalog = new WorldCatalog(directory, http: OfflineHttp.Client()))
        {
            await using var sessions = new SessionWorkspace(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), store,
                new MemoryPasswordVault(), new MemoryRoomMapStore(), new InlineScriptFactory(), scripts, catalog: catalog);
            await sessions.OpenAsync(profile);
            var library = sessions.Active.Controller.ScriptLibrary;
            var refreshed = Assert.Single(library.Items, entry => entry.IsPack);
            Assert.Equal(pack.Id, refreshed.Id);
            Assert.Equal(SecondVersion, refreshed.Source);
            Assert.Equal(2, refreshed.Pack!.Version);
            Assert.True(refreshed.AllowSend);
            Assert.False(refreshed.Enabled);
            Assert.Equal(2, library.Items.Count);

            // Duplicating gives an ordinary hand-written script with no pack marker and no policy.
            var copy = library.Duplicate(refreshed);
            Assert.False(copy.IsPack);
            Assert.False(copy.AllowSend);
            Assert.Equal(SecondVersion, copy.Source);
            Assert.Equal(L.Format(L.ScriptDuplicateName, "Ship panel"), copy.Name);
        }

        // A regenerated pack that renames a script retires the old one; hand-written scripts stay.
        var renamed = Listing(port, SecondVersion, 1);
        renamed = renamed with { Scripts = [renamed.Scripts![0] with { Id = "cockpit", Name = "Cockpit" }] };
        WriteDirectory(directory, renamed);
        using (var catalog = new WorldCatalog(directory, http: OfflineHttp.Client()))
        {
            await using var sessions = new SessionWorkspace(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), store,
                new MemoryPasswordVault(), new MemoryRoomMapStore(), new InlineScriptFactory(), scripts, catalog: catalog);
            await sessions.OpenAsync(profile);
            var library = sessions.Active.Controller.ScriptLibrary;
            var replacement = Assert.Single(library.Items, entry => entry.IsPack);
            Assert.Equal(ScriptPackInfo.IdFor(key, "cockpit"), replacement.Id);
            Assert.DoesNotContain(library.Items, entry => entry.Id == pack.Id);
            Assert.DoesNotContain(scripts.Load(key), script => script.Id == pack.Id);
            Assert.Equal(3, library.Items.Count);
        }
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }
}
