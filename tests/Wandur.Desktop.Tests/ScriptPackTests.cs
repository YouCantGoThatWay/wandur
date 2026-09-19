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
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }
}
