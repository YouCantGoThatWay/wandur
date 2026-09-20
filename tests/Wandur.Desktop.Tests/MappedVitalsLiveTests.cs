using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Discovery;
using Wandur.Core.Settings;
using Wandur.Desktop.Terminal;
using Wandur.Desktop.Views;
using Wandur.Models;

namespace Wandur.Desktop.Tests;

/// <summary>The owner's Legends of the Jedi session: auto-login through the name and password prompts, one MSDP
/// variable per subnegotiation, a pack script whose opponent panel comes to the front when a fight starts, and the
/// mapped vitals strip under the transcript, which must keep following the world after that.</summary>
public sealed class MappedVitalsLiveTests
{
    /// <summary>The generated opponent script of the Legends of the Jedi pack, verbatim.</summary>
    private const string OpponentWatch = """
        const p = mud.panel("opponent-watch", { title: "Combat", dock: "right" });
        p.label("opponent", { text: "No opponent" });
        p.show();
        let drawn = "";
        let active = false;
        function refresh() {
          const raw = mud.state.get("msdp.OPPONENTNAME");
          const name = raw === undefined ? "" : mud.format(raw);
          const next = name.trim();
          if (next === drawn) return;
          drawn = next;
          const fighting = next.length > 0;
          p.label("opponent", { text: fighting ? "Opponent: " + next : "No opponent" });
          if (fighting && !active) p.show({ focus: true });
          if (!fighting && active) p.hide();
          active = fighting;
        }
        mud.on(Events.Msdp, refresh);
        refresh();
        """;

    /// <summary>The generated skill script of the same pack: it reads variables the world reports but no mapping binds.</summary>
    private const string SkillLevels = """
        const p = mud.panel("skill-levels", { title: "Skills", dock: "right" });
        p.table("skills", { columns: ["Skill", "Level"], rows: [] });
        const fields = [["Combat", "LEVELCOMBAT"], ["Piloting", "LEVELPILOTING"], ["Slicer", "LEVELSLICER"]];
        let drawn = "";
        function refresh() {
          const rows = fields.map(x => {
            const v = mud.state.get("msdp." + x[1]);
            return [x[0], v === undefined ? "" : mud.format(v)];
          });
          const key = JSON.stringify(rows);
          if (key === drawn) return;
          drawn = key;
          p.table("skills", { columns: ["Skill", "Level"], rows: rows });
        }
        mud.on(Events.Msdp, refresh);
        refresh();
        """;

    private const string Prompt = "*((==HP==(((||||||||||   [OOC: 6 |||||||][Near:  ]\r\n*((==MV==(((|||||||||   [Speaking: basic / none ]";

    private static byte[] Msdp(string variable, string value)
        => [255, 250, 69, 1, .. Encoding.UTF8.GetBytes(variable), 2, .. Encoding.UTF8.GetBytes(value), 255, 240];

    private static byte[] MsdpArray(string variable, IEnumerable<string> values)
        => [255, 250, 69, 1, .. Encoding.UTF8.GetBytes(variable), 2, 5, .. values.SelectMany(value => (byte[])[2, .. Encoding.UTF8.GetBytes(value)]), 6, 255, 240];

    private static byte[] Text(string text) => Encoding.UTF8.GetBytes(text);

    private static FieldBinding Bind(string variable, string entity, string category, string key, string member, string conversion = "number")
        => new() { Source = new("MSDP", "MSDP", "/" + variable), Target = new(entity, category, key, member), Label = key, Conversion = conversion };

    /// <summary>The shape of the world's mapping: vitals, opponent, identity and progression, all over MSDP.</summary>
    private static WorldMapping Mapping(int port) => new()
    {
        WorldId = "mudverse:509", Endpoint = new("127.0.0.1", port), SchemaFingerprint = new('a', 64), GeneratedAt = DateTimeOffset.UtcNow, Revision = 1,
        Bindings =
        [
            Bind("HEALTH", "character", "resource", "health", "current"), Bind("HEALTHMAX", "character", "resource", "health", "maximum"),
            Bind("MOVEMENT", "character", "resource", "movement", "current"), Bind("MOVEMENTMAX", "character", "resource", "movement", "maximum"),
            Bind("OPPONENTHEALTH", "opponent", "resource", "health", "current"), Bind("OPPONENTHEALTHMAX", "opponent", "resource", "health", "maximum"),
            Bind("OPPONENTNAME", "opponent", "identity", "name", "value", "text"),
            Bind("CHARACTERNAME", "character", "identity", "name", "value", "text"),
            Bind("CLAN", "character", "identity", "faction", "value", "text"),
            Bind("LEVELCOMBAT", "character", "progression", "combat", "current"),
            Bind("LEVELPILOTING", "character", "progression", "piloting", "current"),
            Bind("WORLDTIME", "world", "metric", "time", "value")
        ]
    };

    /// <summary>Everything the client has written, as one Latin-1 string so telnet bytes stay visible.</summary>
    private sealed class Sent
    {
        private readonly StringBuilder _text = new();
        private readonly object _gate = new();
        private int _answered;
        private int _listed;
        public Sent(NetworkStream stream, CancellationToken token) => _ = Task.Run(async () =>
        {
            var buffer = new byte[8192];
            try
            {
                while (true)
                {
                    var count = await stream.ReadAsync(buffer, token);
                    if (count == 0) return;
                    lock (_text) _text.Append(Encoding.Latin1.GetString(buffer, 0, count));
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        }, token);
        public string Text { get { lock (_text) return _text.ToString(); } }
        /// <summary>The variable names of REPORT requests not yet answered, in wire order.</summary>
        public string[] TakeReported()
        {
            lock (_gate)
            {
                var matches = Regex.Matches(Text, "\u0001REPORT((?:\u0002[A-Z_]+)+)");
                var names = matches.Skip(_answered).SelectMany(match => match.Groups[1].Value.Split('\u0002', StringSplitOptions.RemoveEmptyEntries)).ToArray();
                _answered = matches.Count;
                return names;
            }
        }
        /// <summary>True once for every LIST REPORTABLE_VARIABLES request not yet answered.</summary>
        public bool TakeListed()
        {
            lock (_gate)
            {
                var count = Regex.Matches(Text, "\u0001LIST\u0002REPORTABLE_VARIABLES").Count;
                if (count <= _listed) return false;
                _listed = count;
                return true;
            }
        }
    }

    private static string Tip(Control card) => Assert.IsType<string>(ToolTip.GetTip(card));
    private static string[] Cards(ResourceBarsView strip) => strip.GetVisualDescendants().OfType<ResourceBar>().Select(Tip).ToArray();

    private sealed class CatalogHandler(Func<string> json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json()) });
    }

    /// <summary>The session as the owner plays it: the world is listed in the directory with its mapping and its pack, MSDP
    /// is negotiated at connect and every reportable variable is reported during the login handshake, the client asks again
    /// once play is public, the directory refreshes in the background, and fights come and go with one variable per packet.</summary>
    [AvaloniaFact]
    public async Task ListedWorldVitalsKeepFollowingTheWorldThroughFightsAndCatalogRefreshes()
    {
        var path = Path.Combine(Path.GetTempPath(), "wandur-listed-vitals-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var mapping = Mapping(port);
        var world = new WorldListing
        {
            Id = "mudverse:509", Name = "Legends of the Jedi", Host = "127.0.0.1", Port = port, ProtocolMapping = mapping,
            Scripts =
            [
                new() { Id = "opponent-watch", Name = "Opponent watch", Description = "Combat panel", Source = OpponentWatch, Provenance = "generated", Version = 1 },
                new() { Id = "skill-levels", Name = "Skill levels", Description = "Skills panel", Source = SkillLevels, Provenance = "generated", Version = 1 }
            ]
        };
        string Snapshot() => JsonSerializer.Serialize(new { schema_version = 2, format = "wandur.directory", fetched_at = DateTimeOffset.UtcNow, worlds = new[] { world } }, ModelJson.Options);
        var cache = Path.Combine(path, "directory.json");
        File.WriteAllText(cache, Snapshot());
        var store = new SettingsStore(Path.Combine(path, "settings.json"));
        var profile = world.ToProfile() with { Username = "tester", AutoLogin = true };
        using var http = new HttpClient(new CatalogHandler(Snapshot));
        using var catalog = new WorldCatalog(cache, http: http);
        var window = new MainWindow(new TranscriptDisplayFactory(), store, new MemoryPasswordVault(),
            new MemoryRoomMapStore(), new InlineScriptFactory(), new MemoryScriptLibraryStore(), catalog: catalog) { Width = 1200, Height = 800 };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        // What the world knows; a REPORT is answered from here, one variable per subnegotiation, and a change is sent unprompted.
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CHARACTERNAME"] = "Tester", ["CLAN"] = "None", ["HEALTH"] = "1000", ["HEALTHMAX"] = "1000", ["MOVEMENT"] = "1037", ["MOVEMENTMAX"] = "1040",
            ["OPPONENTNAME"] = "", ["OPPONENTHEALTH"] = "0", ["OPPONENTHEALTHMAX"] = "0",
            ["LEVELCOMBAT"] = "12", ["LEVELPILOTING"] = "3", ["LEVELSLICER"] = "0", ["WORLDTIME"] = "1400"
        };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            await window.Controller.SaveWorldAsync(profile, "secret", true);
            profile = Assert.Single(window.Controller.Settings.Profiles);
            await window.Sessions.OpenAsync(profile);
            using var server = await listener.AcceptTcpClientAsync(timeout.Token);
            var stream = server.GetStream();
            var sent = new Sent(stream, timeout.Token);
            var controller = window.Controller;
            Assert.NotNull(window.Sessions.Active.Profile!.ProtocolMapping);
            async Task World(params byte[][] parts) { foreach (var part in parts) await stream.WriteAsync(part, timeout.Token); }
            async Task WaitSent(string token) => await ScriptSessionTests.WaitFor(() => sent.Text.Contains(token, StringComparison.Ordinal));
            async Task AnswerRequests()
            {
                if (sent.TakeListed()) await World(MsdpArray("REPORTABLE_VARIABLES", values.Keys));
                foreach (var name in sent.TakeReported())
                    if (values.TryGetValue(name, out var value)) await World(Msdp(name, value));
            }
            async Task Change(string variable, string value) { values[variable] = value; await World(Msdp(variable, value)); }

            // Connect: the world offers MSDP and GMCP before the banner; the client lists and reports everything, and the
            // world answers while the login handshake still owns the session.
            await World([255, 251, 69], [255, 251, 201], Text("\r\nWelcome to the galaxy.\r\n\r\nEnter your name, or type NEW: "));
            await WaitSent("tester");
            await WaitSent("\u0001LIST\u0002REPORTABLE_VARIABLES");
            await AnswerRequests();
            await WaitSent("\u0001REPORT");
            await AnswerRequests();
            await ScriptSessionTests.WaitFor(() => { controller.FlushOutput(); return controller.ScriptState.TryGetMsdp("WORLDTIME") is not null; });
            await World([255, 251, 1], Text("\r\n(P)assword: "));
            await WaitSent("secret");
            await World([255, 252, 1], Text("\r\n(R)econnecting.\r\n"), Text("The Landing Pad\r\nA wide ferrocrete pad.\r\n\r\n" + Prompt));
            await ScriptSessionTests.WaitFor(() => !controller.IsPrivate && controller.ScriptLibrary.Items.Count(entry => entry.Runtime.IsRunning) == 2);
            // Public play resumed: the client asks for the mapped variables again and the world answers with what it has.
            await ScriptSessionTests.WaitFor(() => { AnswerRequests().GetAwaiter().GetResult(); controller.FlushOutput(); return controller.GameState.Character.Resources.GetValueOrDefault("movement")?.Current?.Value == 1037; });
            Assert.Equal(1000, controller.GameState.Character.Resources["health"].Current!.Value);
            var strip = Assert.Single(window.GetVisualDescendants().OfType<ResourceBarsView>());
            await ScriptSessionTests.WaitFor(() => Cards(strip).Length == 2);
            Assert.Equal(new[] { "Health: 1000 / 1000", "Movement: 1037 / 1040" }, Cards(strip));
            var combat = Assert.Single(controller.ScriptLibrary.Panels.Panels, candidate => candidate.Id == "opponent-watch");
            Assert.True(combat.IsVisible);

            // The directory refreshes in the background with the same listing, as it does every five minutes.
            await catalog.LoadAsync(force: true); Dispatcher.UIThread.RunJobs();
            await AnswerRequests();
            await Change("WORLDTIME", "1401");
            await ScriptSessionTests.WaitFor(() => { controller.FlushOutput(); return controller.GameState.World.Metrics.GetValueOrDefault("time")?.Number?.Value == 1401; });
            Assert.Equal(new[] { "Health: 1000 / 1000", "Movement: 1037 / 1040" }, Cards(strip));

            // A fight starts: the opponent variables change one packet at a time and the pack panel comes to the front.
            Assert.True(await controller.SendAsync("kill womprat"));
            await WaitSent("kill womprat");
            await World(Text("You attack a vicious womprat!\r\n"));
            await Change("OPPONENTNAME", "A Vicious Womprat"); await Change("OPPONENTHEALTHMAX", "100"); await Change("OPPONENTHEALTH", "100");
            await World(Text("- Enemy: [ 100% ] -\r\n" + Prompt));
            await ScriptSessionTests.WaitFor(() => { controller.FlushOutput(); return window.Workspace.IsScriptPanelVisible(combat) && combat.Widgets.Any(widget => widget.Properties.Text == "Opponent: A Vicious Womprat"); });
            await AnswerRequests();
            await ScriptSessionTests.WaitFor(() => { controller.FlushOutput(); return controller.GameState.Opponent.Resources.GetValueOrDefault("health")?.Percentage == 100; });
            await ScriptSessionTests.WaitFor(() => Cards(strip).Length == 3);
            Assert.Equal(new[] { "Health: 1000 / 1000", "Movement: 1037 / 1040", "Opponent · Health: 100 / 100" }, Cards(strip));

            // Rounds later the world reports what changed; the mapped cards and the opponent card follow.
            await World(Text("You hit a vicious womprat hard.\r\n"));
            await Change("OPPONENTHEALTH", "30"); await Change("HEALTH", "980"); await Change("MOVEMENT", "1018");
            await World(Text("- Enemy: [ 30% ] -\r\n" + Prompt));
            await ScriptSessionTests.WaitFor(() => { controller.FlushOutput(); return Cards(strip).SequenceEqual(["Health: 980 / 1000", "Movement: 1018 / 1040", "Opponent · Health: 30 / 100"]); });

            // The fight ends and another begins against a different opponent, whose health arrives before its name.
            await Change("OPPONENTNAME", ""); await Change("OPPONENTHEALTH", "0"); await Change("OPPONENTHEALTHMAX", "0");
            await World(Text("A vicious womprat is dead.\r\n" + Prompt));
            await ScriptSessionTests.WaitFor(() => { controller.FlushOutput(); return Cards(strip).Length == 2 && !combat.IsVisible; });
            Assert.True(await controller.SendAsync("kill stormtrooper"));
            await WaitSent("kill stormtrooper");
            await Change("OPPONENTHEALTHMAX", "250"); await Change("OPPONENTHEALTH", "250"); await Change("OPPONENTNAME", "A Stormtrooper");
            await World(Text("- Enemy: [ 100% ] -\r\n" + Prompt));
            await ScriptSessionTests.WaitFor(() => { controller.FlushOutput(); return combat.IsVisible && combat.Widgets.Any(widget => widget.Properties.Text == "Opponent: A Stormtrooper"); });
            await Change("OPPONENTHEALTH", "200"); await Change("MOVEMENT", "1010");
            await World(Text("- Enemy: [ 80% ] -\r\n" + Prompt));
            await ScriptSessionTests.WaitFor(() => { controller.FlushOutput(); return Cards(strip).SequenceEqual(["Health: 980 / 1000", "Movement: 1010 / 1040", "Opponent · Health: 200 / 250"]); });
            Assert.Equal("A Stormtrooper", controller.GameState.Opponent.Identity["name"].Value);
        }
        finally
        {
            await window.Sessions.DisposeAsync(); window.Close();
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    /// <summary>The reproduction. A GMCP login is offered and answered, and the world drops the player straight into the
    /// game without ever sending Char.Login.Result, so the auto-login attempt lingers (public, not private) until it times
    /// out. Mapped MSDP and GMCP vitals keep arriving and reach the script cache, which is gated by privacy alone, but the
    /// binding engine was gated behind the login as well, so GameState and the vitals strip froze at their last pre-login
    /// values while a fight went on. Both the mapped cards and the opponent card must follow the world through this window.</summary>
    [AvaloniaFact]
    public async Task MappedVitalsFollowTheWorldWhileAGmcpLoginLingers()
    {
        var path = Path.Combine(Path.GetTempPath(), "wandur-lingering-login-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var store = new SettingsStore(Path.Combine(path, "settings.json"));
        var profile = new ConnectionProfile { Name = "Jedi world", Host = "127.0.0.1", Port = port, Username = "tester", AutoLogin = true, ProtocolMapping = Mapping(port) };
        var controller = new WorkspaceController(new TranscriptDisplayFactory(), store, new MemoryPasswordVault(),
            new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await controller.SaveWorldAsync(profile, "secret", true);
            profile = Assert.Single(controller.Settings.Profiles);
            await controller.StartAsync(profile);
            using var server = await listener.AcceptTcpClientAsync(timeout.Token);
            var stream = server.GetStream();
            async Task World(params byte[][] parts) { foreach (var part in parts) await stream.WriteAsync(part, timeout.Token); }
            async Task Pump() { for (var i = 0; i < 20; i++) { await Task.Delay(15); Dispatcher.UIThread.RunJobs(); controller.FlushOutput(); } }
            byte[] Gmcp(string text) => [255, 250, 201, .. Encoding.UTF8.GetBytes(text), 255, 240];

            // The world offers GMCP and MSDP, then a GMCP password login, which the client answers. No Char.Login.Result
            // follows: the world just drops the player into the game, so the login attempt is still open and public.
            await World([255, 251, 201], [255, 251, 69], Gmcp("Char.Login.Default {\"type\":[\"password-credentials\"],\"version\":1}"));
            await Pump();
            Assert.True(controller.IsConnected);
            Assert.False(controller.IsPrivate);

            // Combat: the world reports the mapped vitals and the opponent, one MSDP variable per subnegotiation.
            await World(Msdp("HEALTH", "980"), Msdp("HEALTHMAX", "1000"), Msdp("MOVEMENT", "1018"), Msdp("MOVEMENTMAX", "1040"),
                Msdp("OPPONENTNAME", "A Vicious Womprat"), Msdp("OPPONENTHEALTHMAX", "100"), Msdp("OPPONENTHEALTH", "30"));
            await Pump();

            // The cache has the values: proof MSDP arrived and was accepted (it is gated by privacy alone, not the login).
            Assert.Equal("\"980\"", controller.ScriptState.TryGetMsdp("HEALTH"));
            Assert.Equal("\"30\"", controller.ScriptState.TryGetMsdp("OPPONENTHEALTH"));
            // The binding engine must have them too, although the login is still open.
            Assert.Equal(980, controller.GameState.Character.Resources["health"].Current!.Value);
            Assert.Equal(1018, controller.GameState.Character.Resources["movement"].Current!.Value);
            Assert.Equal(30, controller.GameState.Opponent.Resources["health"].Current!.Value);
            Assert.Equal("A Vicious Womprat", controller.GameState.Opponent.Identity["name"].Value);
        }
        finally
        {
            await controller.DisposeAsync();
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    /// <summary>Two fights in one session, with the variants of what a world sends when the first ends (all three opponent
    /// variables, only the name, or nothing) and of the wire order of the second fight's report (name first, or health
    /// and its maximum before the name, which is the alphabetical order most MSDP tables use), against the same opponent
    /// or a different one, and with a room change, a privacy blip or a mapping refresh between the fights. The opponent
    /// card must show in the second fight in every one of them.</summary>
    [AvaloniaTheory]
    [InlineData("all", false, true, "none")]
    [InlineData("all", true, true, "none")]
    [InlineData("all", false, false, "none")]
    [InlineData("all", true, false, "none")]
    [InlineData("name", false, true, "none")]
    [InlineData("name", true, true, "none")]
    [InlineData("name", false, false, "none")]
    [InlineData("name", true, false, "none")]
    [InlineData("none", false, true, "none")]
    [InlineData("none", true, true, "none")]
    [InlineData("none", false, false, "none")]
    [InlineData("none", true, false, "none")]
    [InlineData("all", true, false, "room")]
    [InlineData("none", true, false, "room")]
    [InlineData("all", true, false, "echo")]
    [InlineData("none", true, false, "echo")]
    [InlineData("all", true, false, "catalog")]
    [InlineData("none", true, false, "catalog")]
    [InlineData("all", true, false, "silent")]
    [InlineData("name", true, true, "silent")]
    public async Task TheOpponentCardShowsInTheSecondFight(string fightEnd, bool healthFirst, bool sameName, string between)
    {
        var path = Path.Combine(Path.GetTempPath(), "wandur-second-fight-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var mapping = Mapping(port);
        mapping = mapping with { Bindings = [.. mapping.Bindings, Bind("ROOMNAME", "character", "location", "room", "value", "text"), Bind("ROOMVNUM", "character", "location", "id", "value", "text")] };
        var world = new WorldListing { Id = "mudverse:509", Name = "Legends of the Jedi", Host = "127.0.0.1", Port = port, ProtocolMapping = mapping };
        string Snapshot() => JsonSerializer.Serialize(new { schema_version = 2, format = "wandur.directory", fetched_at = DateTimeOffset.UtcNow, worlds = new[] { world } }, ModelJson.Options);
        var cache = Path.Combine(path, "directory.json");
        File.WriteAllText(cache, Snapshot());
        var store = new SettingsStore(Path.Combine(path, "settings.json"));
        var profile = world.ToProfile() with { Username = "tester", AutoLogin = true };
        using var http = new HttpClient(new CatalogHandler(Snapshot));
        using var catalog = new WorldCatalog(cache, http: http);
        var window = new MainWindow(new TranscriptDisplayFactory(), store, new MemoryPasswordVault(),
            new MemoryRoomMapStore(), new InlineScriptFactory(), new MemoryScriptLibraryStore(), catalog: catalog) { Width = 1200, Height = 800 };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CHARACTERNAME"] = "Tester", ["HEALTH"] = "1000", ["HEALTHMAX"] = "1000", ["MOVEMENT"] = "1037", ["MOVEMENTMAX"] = "1040",
            ["OPPONENTNAME"] = "", ["OPPONENTHEALTH"] = "0", ["OPPONENTHEALTHMAX"] = "0", ["ROOMNAME"] = "The Landing Pad", ["ROOMVNUM"] = "100", ["WORLDTIME"] = "1400"
        };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            await window.Controller.SaveWorldAsync(profile, "secret", true);
            profile = Assert.Single(window.Controller.Settings.Profiles);
            await window.Sessions.OpenAsync(profile);
            using var server = await listener.AcceptTcpClientAsync(timeout.Token);
            var stream = server.GetStream();
            var sent = new Sent(stream, timeout.Token);
            var controller = window.Controller;
            async Task World(params byte[][] parts) { foreach (var part in parts) await stream.WriteAsync(part, timeout.Token); }
            async Task WaitSent(string token) => await ScriptSessionTests.WaitFor(() => sent.Text.Contains(token, StringComparison.Ordinal));
            async Task AnswerRequests()
            {
                if (sent.TakeListed()) await World(MsdpArray("REPORTABLE_VARIABLES", values.Keys));
                foreach (var name in sent.TakeReported())
                    if (values.TryGetValue(name, out var value)) await World(Msdp(name, value));
            }
            // A world sends a reported variable only when it changes.
            async Task Change(string variable, string value) { if (values[variable] == value) return; values[variable] = value; await World(Msdp(variable, value)); }
            ResourceBarsView? strip = null;
            string[] OpponentCards() => Cards(strip!).Where(card => card.StartsWith("Opponent", StringComparison.Ordinal)).ToArray();
            // A silent fight is reported by MSDP alone, with no transcript text in the same flush: the strip must re-render anyway.
            var silent = false;
            async Task Fight(string name, bool healthBeforeName, string enemy)
            {
                Assert.True(await controller.SendAsync("kill " + enemy));
                await WaitSent("kill " + enemy);
                if (!silent) await World(Text("You attack " + enemy + "!\r\n"));
                if (healthBeforeName) { await Change("OPPONENTHEALTH", "100"); await Change("OPPONENTHEALTHMAX", "100"); await Change("OPPONENTNAME", name); }
                else { await Change("OPPONENTNAME", name); await Change("OPPONENTHEALTHMAX", "100"); await Change("OPPONENTHEALTH", "100"); }
                if (!silent) await World(Text("- Enemy: [ 100% ] -\r\n" + Prompt));
                await ScriptSessionTests.WaitFor(() => { controller.FlushOutput(); return controller.GameState.Opponent.Identity.GetValueOrDefault("name")?.Value == name; });
                await WaitForCard("Opponent · Health: 100 / 100");
                if (!silent) await World(Text("You hit " + enemy + " hard.\r\n"));
                await Change("OPPONENTHEALTH", "30");
                if (!silent) await World(Text("- Enemy: [ 30% ] -\r\n" + Prompt));
                await WaitForCard("Opponent · Health: 30 / 100");
            }
            async Task WaitForCard(string expected)
            {
                var until = DateTime.UtcNow.AddSeconds(3);
                while (DateTime.UtcNow < until && !OpponentCards().SequenceEqual([expected])) { controller.FlushOutput(); Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
                var health = controller.GameState.Opponent.Resources.GetValueOrDefault("health");
                Assert.True(OpponentCards().SequenceEqual([expected]),
                    $"expected [{expected}], strip shows [{string.Join(", ", OpponentCards())}]; GameState opponent health current={health?.Current?.Value.ToString() ?? "null"} maximum={health?.Maximum?.Value.ToString() ?? "null"}; name={controller.GameState.Opponent.Identity.GetValueOrDefault("name")?.Value ?? "null"}");
            }

            await World([255, 251, 69], [255, 251, 201], Text("\r\nWelcome to the galaxy.\r\n\r\nEnter your name, or type NEW: "));
            await WaitSent("tester");
            await WaitSent("\u0001LIST\u0002REPORTABLE_VARIABLES");
            await AnswerRequests();
            await WaitSent("\u0001REPORT");
            await AnswerRequests();
            await World([255, 251, 1], Text("\r\n(P)assword: "));
            await WaitSent("secret");
            await World([255, 252, 1], Text("\r\n(R)econnecting.\r\n"), Text("The Landing Pad\r\nA wide ferrocrete pad.\r\n\r\n" + Prompt));
            await ScriptSessionTests.WaitFor(() => !controller.IsPrivate);
            await ScriptSessionTests.WaitFor(() => { AnswerRequests().GetAwaiter().GetResult(); controller.FlushOutput(); return controller.GameState.Character.Resources.GetValueOrDefault("movement")?.Current?.Value == 1037; });
            strip = Assert.Single(window.GetVisualDescendants().OfType<ResourceBarsView>());
            await ScriptSessionTests.WaitFor(() => Cards(strip).Length == 2);

            // Fight one, as every world reports it: the name first.
            await Fight("A Vicious Womprat", false, "womprat");

            // The fight ends. Some worlds clear the three opponent variables, some only the name, some nothing at all.
            await World(Text("A vicious womprat is dead.\r\n" + Prompt));
            if (fightEnd == "all") { await Change("OPPONENTHEALTH", "0"); await Change("OPPONENTHEALTHMAX", "0"); await Change("OPPONENTNAME", ""); }
            else if (fightEnd == "name") await Change("OPPONENTNAME", "");
            await World(Text(Prompt));
            await ScriptSessionTests.WaitFor(() => { controller.FlushOutput(); return fightEnd == "none" ? OpponentCards().Length == 1 : OpponentCards().Length == 0; });

            switch (between)
            {
                case "room":
                    Assert.True(await controller.SendAsync("north"));
                    await WaitSent("north");
                    await Change("ROOMNAME", "A Dusty Street"); await Change("ROOMVNUM", "101");
                    await World(Text("A Dusty Street\r\nSand everywhere.\r\nExits: south\r\n" + Prompt));
                    Assert.True(await controller.SendAsync("look"));
                    await WaitSent("look");
                    await World(Text("A Dusty Street\r\nSand everywhere.\r\nExits: south\r\n" + Prompt));
                    await ScriptSessionTests.WaitFor(() => { controller.FlushOutput(); return controller.GameState.Character.Location.GetValueOrDefault("room")?.Value == "A Dusty Street"; });
                    break;
                case "echo":
                    // A privacy blip: the world turns echo off and on again, which moves the privacy epochs and asks for a mapped refresh.
                    await World([255, 251, 1], Text("\r\nConfirm: "));
                    await ScriptSessionTests.WaitFor(() => { controller.FlushOutput(); return controller.IsPrivate; });
                    await World([255, 252, 1], Text("\r\n" + Prompt));
                    await ScriptSessionTests.WaitFor(() => { controller.FlushOutput(); return !controller.IsPrivate; });
                    await WaitSent("\u0001SEND");
                    await ScriptSessionTests.WaitFor(() => { AnswerRequests().GetAwaiter().GetResult(); controller.FlushOutput(); return sent.TakeReported().Length == 0; });
                    break;
                case "catalog":
                    await catalog.LoadAsync(force: true); Dispatcher.UIThread.RunJobs();
                    await AnswerRequests();
                    break;
            }
            await Change("WORLDTIME", "1401");
            await ScriptSessionTests.WaitFor(() => { controller.FlushOutput(); return controller.GameState.World.Metrics.GetValueOrDefault("time")?.Number?.Value == 1401; });
            Assert.Equal(new[] { "Health: 1000 / 1000", "Movement: 1037 / 1040" }, Cards(strip).Take(2));

            // Fight two.
            silent = between == "silent";
            await Fight(sameName ? "A Vicious Womprat" : "A Stormtrooper", healthFirst, sameName ? "womprat" : "stormtrooper");
        }
        finally
        {
            await window.Sessions.DisposeAsync(); window.Close();
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    [AvaloniaFact]
    public async Task MappedVitalsKeepFollowingTheWorldAfterAPackPanelShowsForAFight()
    {
        var path = Path.Combine(Path.GetTempPath(), "wandur-mapped-vitals-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var store = new SettingsStore(Path.Combine(path, "settings.json"));
        var profile = new ConnectionProfile { Name = "Jedi world", Host = "127.0.0.1", Port = port, Username = "tester", AutoLogin = true, ProtocolMapping = Mapping(port) };
        var scripts = new MemoryScriptLibraryStore();
        var worldKey = $"{profile.Host}:{profile.Port}:{profile.UseTls}";
        // The pack script is enabled before the session opens, as an installed pack is.
        scripts.Upsert(worldKey, Assert.Single(scripts.Load(worldKey)) with { Enabled = true, Source = OpponentWatch });
        var window = new MainWindow(new TranscriptDisplayFactory(), store, new MemoryPasswordVault(),
            new MemoryRoomMapStore(), new InlineScriptFactory(), scripts) { Width = 1200, Height = 800 };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CHARACTERNAME"] = "Tester", ["HEALTH"] = "1000", ["HEALTHMAX"] = "1000", ["MOVEMENT"] = "1037", ["MOVEMENTMAX"] = "1040"
        };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            await window.Controller.SaveWorldAsync(profile, "secret", true);
            profile = Assert.Single(window.Controller.Settings.Profiles);
            await window.Sessions.OpenAsync(profile);
            using var server = await listener.AcceptTcpClientAsync(timeout.Token);
            var stream = server.GetStream();
            var sent = new Sent(stream, timeout.Token);
            var controller = window.Controller;
            async Task World(params byte[][] parts) { foreach (var part in parts) await stream.WriteAsync(part, timeout.Token); }
            async Task WaitSent(string token) => await ScriptSessionTests.WaitFor(() => sent.Text.Contains(token, StringComparison.Ordinal));
            // The world answers every REPORT once with the current value of each named variable it knows, one per subnegotiation.
            async Task AnswerReports()
            {
                foreach (var name in sent.TakeReported())
                    if (values.TryGetValue(name, out var value)) await World(Msdp(name, value));
            }

            // Login, as the world does it: the name prompt, then the password behind WILL ECHO, then the reconnect line.
            await World(Text("\r\nWelcome to the galaxy.\r\n\r\nEnter your name, or type NEW: "));
            await WaitSent("tester");
            await World([255, 251, 1], Text("\r\n(P)assword: "));
            await WaitSent("secret");
            await World([255, 252, 1], Text("\r\n(R)econnecting.\r\n"), [255, 251, 69], [255, 251, 201],
                Text("The Landing Pad\r\nA wide ferrocrete pad.\r\n\r\n" + Prompt));
            await ScriptSessionTests.WaitFor(() => controller.ProtocolEvidence.Msdp == Wandur.Core.Protocol.TelnetOptionState.Enabled);
            await ScriptSessionTests.WaitFor(() => !controller.IsPrivate && controller.ScriptLibrary.Items[0].Runtime.IsRunning);
            // Public play resumed: the client asks for the mapped variables again and the world answers.
            await WaitSent("\u0001REPORT");
            await AnswerReports();
            await ScriptSessionTests.WaitFor(() => { controller.FlushOutput(); return controller.GameState.Character.Resources.GetValueOrDefault("movement")?.Current?.Value == 1037; });
            Assert.Equal(1000, controller.GameState.Character.Resources["health"].Current!.Value);
            var strip = Assert.Single(window.GetVisualDescendants().OfType<ResourceBarsView>());
            await ScriptSessionTests.WaitFor(() => Cards(strip).Length == 2);
            Assert.Equal(new[] { "Health: 1000 / 1000", "Movement: 1037 / 1040" }, Cards(strip));

            // The fight starts: the opponent variables arrive one per subnegotiation and the pack panel comes to the front.
            Assert.True(await controller.SendAsync("kill womprat"));
            await WaitSent("kill womprat");
            values["OPPONENTNAME"] = "A Vicious Womprat"; values["OPPONENTHEALTH"] = "100"; values["OPPONENTHEALTHMAX"] = "100";
            await World(Text("You attack a vicious womprat!\r\n"), Msdp("OPPONENTNAME", "A Vicious Womprat"), Msdp("OPPONENTHEALTHMAX", "100"),
                Msdp("OPPONENTHEALTH", "100"), Text("- Enemy: [ 100% ] -\r\n" + Prompt));
            var panel = Assert.Single(controller.ScriptLibrary.Panels.Panels, candidate => candidate.Id == "opponent-watch");
            await ScriptSessionTests.WaitFor(() => { controller.FlushOutput(); return panel.IsVisible && window.Workspace.IsScriptPanelVisible(panel); });
            await AnswerReports();
            await ScriptSessionTests.WaitFor(() => { controller.FlushOutput(); return controller.GameState.Opponent.Resources.GetValueOrDefault("health")?.Percentage == 100; });
            Assert.Equal("A Vicious Womprat", controller.GameState.Opponent.Identity["name"].Value);
            await ScriptSessionTests.WaitFor(() => Cards(strip).Length == 3);
            Assert.Equal(new[] { "Health: 1000 / 1000", "Movement: 1037 / 1040", "Opponent · Health: 100 / 100" }, Cards(strip));

            // A round later the world reports what changed: the strip must follow, on the mapped cards and the opponent card alike.
            values["OPPONENTHEALTH"] = "30"; values["HEALTH"] = "980"; values["MOVEMENT"] = "1018";
            await World(Text("You hit a vicious womprat hard.\r\n"), Msdp("OPPONENTHEALTH", "30"), Msdp("HEALTH", "980"), Msdp("MOVEMENT", "1018"),
                Text("- Enemy: [ 30% ] -\r\n" + Prompt));
            await AnswerReports();
            await ScriptSessionTests.WaitFor(() => { controller.FlushOutput(); return controller.GameState.Character.Resources.GetValueOrDefault("movement")?.Current?.Value == 1018; });
            Assert.Equal(980, controller.GameState.Character.Resources["health"].Current!.Value);
            Assert.Equal(30, controller.GameState.Opponent.Resources["health"].Current!.Value);
            await ScriptSessionTests.WaitFor(() => Cards(strip).SequenceEqual(["Health: 980 / 1000", "Movement: 1018 / 1040", "Opponent · Health: 30 / 100"]));
            Assert.True(strip.IsVisible);
        }
        finally
        {
            await window.Sessions.DisposeAsync(); window.Close();
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }
}
