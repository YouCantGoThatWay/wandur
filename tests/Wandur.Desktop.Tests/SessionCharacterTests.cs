using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Settings;
using Wandur.Core.Storage;
using Wandur.Desktop.Terminal;
using Wandur.Models;

namespace Wandur.Desktop.Tests;

/// <summary>Two characters on one world: the character's name labels the tab, the open sessions row, the session bar
/// and the window title, from the profile's login name until the world reports who is playing.</summary>
public sealed class SessionCharacterTests
{
    private static byte[] Msdp(string variable, string value)
        => [255, 250, 69, 1, .. Encoding.UTF8.GetBytes(variable), 2, .. Encoding.UTF8.GetBytes(value), 255, 240];

    private static FieldBinding Bind(string variable, string entity, string category, string key, string member, string conversion = "number")
        => new() { Source = new("MSDP", "MSDP", "/" + variable), Target = new(entity, category, key, member), Label = key, Conversion = conversion };

    private static WorldMapping Mapping(int port) => new()
    {
        WorldId = "mudverse:509", Endpoint = new("127.0.0.1", port), SchemaFingerprint = new('a', 64), GeneratedAt = DateTimeOffset.UtcNow, Revision = 1,
        Bindings =
        [
            Bind("HEALTH", "character", "resource", "health", "current"), Bind("HEALTHMAX", "character", "resource", "health", "maximum"),
            Bind("CHARACTERNAME", "character", "identity", "name", "value", "text")
        ]
    };

    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    [AvaloniaFact]
    public async Task TheCharacterNamesTheTabTheSessionBarAndTheWindowTitle()
    {
        var path = Path.Combine(Path.GetTempPath(), "wandur-character-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var database = new ClientDatabase(Path.Combine(path, "wandur.db"));
        var store = new SqliteSettingsStore(database, Path.Combine(path, "settings.json"));
        var usage = new SqliteWorldUsageStore(database);
        // One world, two profiles: an account login whose character the world names later, and Mira.
        var account = new ConnectionProfile { Name = "Legends of the Jedi", Host = "127.0.0.1", Port = port, Username = "account", ProtocolMapping = Mapping(port) };
        var mira = account with { Id = Guid.NewGuid(), Username = "Mira" };
        store.Save(new ClientSettings { Profiles = [account, mira] });
        var window = new MainWindow(new TranscriptDisplayFactory(), store, new MemoryPasswordVault(), new MemoryRoomMapStore(),
            new RecordingScriptFactory(), new MemoryScriptLibraryStore(), usage: usage) { Width = 1200, Height = 800 };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var titles = new List<string>();
        string Status() { titles.Add(window.Title ?? ""); return Find<TextBlock>(window, "SessionStatus").Text ?? ""; }
        string Row(SessionTab tab) => window.Workspace.Navigation.OpenEntries.Single(entry => ReferenceEquals(entry.Key, tab)).Title;
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            Assert.Equal("Wandur", window.Title);
            Assert.DoesNotContain("·", Status());

            // The first session opens as the account: the login name is the only name there is.
            await window.Sessions.OpenAsync(account);
            using var first = await listener.AcceptTcpClientAsync(timeout.Token);
            var firstTab = window.Sessions.Active;
            var controller = firstTab.Controller;
            await ScriptSessionTests.WaitFor(() => controller.IsConnected);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("account", controller.CharacterName);
            Assert.Equal("Legends of the Jedi · account", firstTab.Title);
            Assert.Equal("Legends of the Jedi · account", Row(firstTab));
            Assert.Equal("account · Legends of the Jedi · Wandur", window.Title);
            Assert.StartsWith("●  Legends of the Jedi · account  ·  ", Status());

            // The world names the character through the mapping (MSDP CHARACTERNAME): the reported name wins everywhere.
            var changes = 0;
            controller.CharacterChanged += () => changes++;
            var stream = first.GetStream();
            await stream.WriteAsync(new byte[] { 255, 251, 69 }, timeout.Token);
            await stream.WriteAsync(Msdp("HEALTH", "100"), timeout.Token);
            await stream.WriteAsync(Msdp("CHARACTERNAME", "Talek"), timeout.Token);
            await ScriptSessionTests.WaitFor(() => { controller.FlushOutput(); return controller.CharacterName == "Talek"; });
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, changes);
            Assert.Equal("Legends of the Jedi · Talek", firstTab.Title);
            Assert.Equal("Legends of the Jedi · Talek", Row(firstTab));
            Assert.Equal("Talek · Legends of the Jedi · Wandur", window.Title);
            Assert.StartsWith("●  Legends of the Jedi · Talek  ·  ", Status());
            // The world remembers who played there last, for both profiles of the world.
            Assert.Equal("Talek", usage.Load()[account.Id].LastCharacter);
            Assert.Equal("Talek", usage.Load()[mira.Id].LastCharacter);

            // A second session on the same world as Mira: the two tabs differ and the title follows the active one.
            await window.Sessions.OpenAsync(mira);
            using var second = await listener.AcceptTcpClientAsync(timeout.Token);
            var secondTab = window.Sessions.Active;
            Assert.NotSame(firstTab, secondTab);
            await ScriptSessionTests.WaitFor(() => secondTab.Controller.IsConnected);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Legends of the Jedi · Mira", secondTab.Title);
            Assert.Equal("Mira · Legends of the Jedi · Wandur", window.Title);
            Assert.StartsWith("●  Legends of the Jedi · Mira  ·  ", Status());
            Assert.Equal(["Legends of the Jedi · Talek", "Legends of the Jedi · Mira"],
                window.Workspace.Navigation.OpenEntries.Where(entry => entry.Key is SessionTab).Select(entry => entry.Title));
            window.Sessions.Select(firstTab); Dispatcher.UIThread.RunJobs();
            Assert.Equal("Talek · Legends of the Jedi · Wandur", window.Title);
            Assert.StartsWith("●  Legends of the Jedi · Talek  ·  ", Status());
            Assert.Equal("Legends of the Jedi · Talek", firstTab.Title);

            // A tab without a session shows the app alone, and no title ever carried an em dash.
            window.Sessions.NewTab(); Dispatcher.UIThread.RunJobs();
            Assert.Equal("Wandur", window.Title);
            Assert.DoesNotContain("·", Status());
            Assert.All(titles, title => Assert.DoesNotContain("\u2014", title));
        }
        finally
        {
            await window.Sessions.DisposeAsync(); window.Close();
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }
}
