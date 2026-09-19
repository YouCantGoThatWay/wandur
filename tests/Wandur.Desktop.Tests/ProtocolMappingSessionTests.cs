using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Wandur.Core.Discovery;
using Wandur.Core.Settings;
using Wandur.Desktop.ViewModels;
using Wandur.Models;

namespace Wandur.Desktop.Tests;

public sealed class ProtocolMappingSessionTests
{
    [AvaloniaFact]
    public async Task CatalogMappingFeedsStateHonorsPrivacyAndResetsOnReconnect()
    {
        var path = Path.Combine(Path.GetTempPath(), "wandur-mapping-session-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        using var server = new TcpListener(IPAddress.Loopback, 0); server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        var mapping = new WorldMapping { WorldId = "test", Endpoint = new("127.0.0.1", port), SchemaFingerprint = new('a', 64), GeneratedAt = DateTimeOffset.UtcNow,
            Bindings = [new() { Source = new("GMCP", "Char.Vitals", "/hp"), Target = new("character", "resource", "health", "current"), Label = "Health" },
                new() { Source = new("MSDP", "MSDP", "/ROOM_NAME"), Target = new("world", "location", "name", "value"), Label = "Room name", Conversion = "text" },
                new() { Source = new("MSDP", "MSDP", "/ROOM_VNUM"), Target = new("world", "location", "id", "value"), Label = "Room ID", Conversion = "text" }] };
        var world = new WorldListing { Id = "test", Name = "Test", Host = "127.0.0.1", Port = port, ProtocolMapping = mapping };
        File.WriteAllText(Path.Combine(path, "directory.json"), JsonSerializer.Serialize(new { schema_version = 2, format = "wandur.directory", fetched_at = DateTimeOffset.UtcNow, worlds = new[] { world } }, ModelJson.Options));
        var store = new SettingsStore(Path.Combine(path, "settings.json"));
        var profile = world.ToProfile() with { ProtocolMapping = null };
        store.Save(new ClientSettings { Theme = "Paper", Profiles = [profile] });
        using var catalog = new WorldCatalog(Path.Combine(path, "directory.json"), http: OfflineHttp.Client());
        await using var sessions = new SessionWorkspace(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), store, new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore(), catalog: catalog);
        await sessions.OpenAsync(profile);
        using var socket = await server.AcceptTcpClientAsync();
        var controller = sessions.Active.Controller;
        Assert.NotNull(sessions.Active.Profile!.ProtocolMapping);
        Assert.NotNull(Assert.Single(store.Load().Settings.Profiles).ProtocolMapping);
        Assert.Equal("Paper", store.Load().Settings.Theme);
        static byte[] Message(byte option, string text) => new byte[] { 255, 250, option }.Concat(Encoding.UTF8.GetBytes(text)).Concat(new byte[] { 255, 240 }).ToArray();
        async Task Output(byte[] bytes)
        {
            await socket.GetStream().WriteAsync(bytes);
            for (var i = 0; i < 10; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); controller.FlushOutput(); }
        }
        await Output(new byte[] { 255, 251, 201, 255, 251, 69 }.Concat(Message(201, "Char.Vitals {\"hp\":42}")).ToArray());
        Assert.Equal(42, controller.GameState.Character.Resources["health"].Current!.Value);
        controller.SetManualPrivate(true);
        await Output(Message(201, "Char.Vitals {\"hp\":99}"));
        controller.SetManualPrivate(false);
        Assert.Equal(42, controller.GameState.Character.Resources["health"].Current!.Value);
        await Output(Message(201, "Char.Vitals {\"hp\":null}"));
        Assert.Null(controller.GameState.Character.Resources["health"].Current);
        await Output(Message(69, "\u0001ROOM_NAME\u0002Separate room name"));
        await Output(Message(69, "\u0001ROOM_VNUM\u000210"));
        Assert.Equal("Separate room name", controller.GameState.World.Location["name"].Value);
        Assert.Equal("10", controller.GameState.World.Location["id"].Value);
        // Independent state observations do not manufacture a coherent mapper room.
        Assert.Null(controller.Map.Snapshot.CurrentRoomId);
        var saved = Assert.Single(store.Load().Settings.Profiles);
        using (var editor = new ProfileEditorViewModel(controller, catalog, saved))
        {
            editor.WorldName = "Renamed"; await editor.SaveCommand.ExecuteAsync(null);
            Assert.Empty(editor.Error);
        }
        saved = Assert.Single(store.Load().Settings.Profiles);
        Assert.NotNull(saved.ProtocolMapping);
        using (var editor = new ProfileEditorViewModel(controller, catalog, saved))
        {
            editor.Port = port == 65535 ? port - 1 : port + 1; await editor.SaveCommand.ExecuteAsync(null);
            Assert.Empty(editor.Error);
        }
        Assert.Null(Assert.Single(store.Load().Settings.Profiles).ProtocolMapping);
        await controller.DisconnectAsync();
        await controller.StartAsync(world.ToProfile());
        using var second = await server.AcceptTcpClientAsync();
        Assert.Empty(controller.GameState.Character.Resources);
        Assert.Empty(controller.GameState.World.Location);
    }

    private sealed class CatalogHandler(Func<string> json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json()) });
    }

    [AvaloniaFact]
    public async Task RefreshUpdatesSavedAndConnectedWorldWithoutReaddingOrReplayingPrivatePackets()
    {
        var path = Path.Combine(Path.GetTempPath(), "wandur-live-mapping-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        using var server = new TcpListener(IPAddress.Loopback, 0); server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        var mapping = new WorldMapping { WorldId = "test", Endpoint = new("127.0.0.1", port), SchemaFingerprint = new('a', 64), GeneratedAt = DateTimeOffset.UtcNow,
            Bindings = [new() { Source = new("GMCP", "Char.Vitals", "/hp"), Target = new("character", "resource", "health", "current"), Label = "Health" }] };
        var world = new WorldListing { Id = "test", Name = "Test", Host = "127.0.0.1", Port = port };
        var fetched = DateTimeOffset.UtcNow;
        string Snapshot() => JsonSerializer.Serialize(new { schema_version = 2, format = "wandur.directory", fetched_at = fetched, worlds = new[] { world } }, ModelJson.Options);
        var cache = Path.Combine(path, "directory.json");
        File.WriteAllText(cache, Snapshot());
        var profile = world.ToProfile() with { Name = "My saved name", Encoding = "latin1" };
        var store = new SettingsStore(Path.Combine(path, "settings.json"));
        store.Save(new ClientSettings { Theme = "Paper", Profiles = [profile] });
        using var http = new HttpClient(new CatalogHandler(Snapshot));
        using var catalog = new WorldCatalog(cache, http: http);
        await using var sessions = new SessionWorkspace(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), store, new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore(), catalog: catalog);
        await sessions.OpenAsync(profile);
        using var socket = await server.AcceptTcpClientAsync();
        var controller = sessions.Active.Controller;
        async Task Output(byte[] bytes)
        {
            await socket.GetStream().WriteAsync(bytes);
            for (var i = 0; i < 10; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); controller.FlushOutput(); }
        }
        static byte[] Message(string text) => new byte[] { 255, 250, 201 }.Concat(Encoding.UTF8.GetBytes(text)).Concat(new byte[] { 255, 240 }).ToArray();
        await Output(new byte[] { 255, 251, 201 });
        var drain = new byte[8192];
        while (socket.Available > 0) _ = await socket.GetStream().ReadAsync(drain);
        controller.SetManualPrivate(true);
        await Output(Message("Char.Vitals {\"hp\":99}"));
        world = world with { ProtocolMapping = mapping };
        await catalog.LoadAsync(force: true); Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, sessions.Active.Profile!.ProtocolMapping?.Revision);
        Assert.Empty(controller.GameState.Character.Resources);
        Assert.Equal(0, socket.Available);
        controller.SetManualPrivate(false);
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
        {
            var length = await socket.GetStream().ReadAsync(drain, timeout.Token);
            Assert.Contains("Core.Supports.Set", Encoding.UTF8.GetString(drain, 0, length));
        }
        await Output(Message("Char.Vitals {\"hp\":42}"));
        Assert.Equal(42, controller.GameState.Character.Resources["health"].Current!.Value);
        var previousObservation = controller.GameState.Character.Resources["health"].Current;
        world = world with { ProtocolMapping = mapping with { Revision = 2, Bindings = [.. mapping.Bindings,
            new() { Source = new("GMCP", "Char.Maxstats", "/maxhp"), Target = new("character", "resource", "health", "maximum"), Label = "Health" }] } };
        await catalog.LoadAsync(force: true); Dispatcher.UIThread.RunJobs();
        Assert.Equal(previousObservation, controller.GameState.Character.Resources["health"].Current);
        Assert.Null(controller.GameState.Character.Resources["health"].Maximum);
        await Output(Message("Char.Maxstats {\"maxhp\":100}"));
        Assert.Equal(42, controller.GameState.Character.Resources["health"].Percentage);
        while (socket.Available > 0) _ = await socket.GetStream().ReadAsync(drain);
        await catalog.LoadAsync(force: true); Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, socket.Available);
        var saved = Assert.Single(store.Load().Settings.Profiles);
        Assert.Equal(profile.Id, saved.Id);
        Assert.Equal("My saved name", saved.Name);
        Assert.Equal("latin1", saved.Encoding);
        Assert.Equal("Paper", store.Load().Settings.Theme);
        Assert.True(controller.IsConnected);
        Assert.Single(sessions.Tabs);
        Assert.Equal(2, saved.ProtocolMapping?.Revision);
        world = world with { ProtocolMapping = mapping with { Endpoint = new("wrong.example", port), Revision = 3 } };
        await catalog.LoadAsync(force: true); Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, sessions.Active.Profile!.ProtocolMapping?.Revision);
        Assert.Equal(42, controller.GameState.Character.Resources["health"].Percentage);
        world = world with { ProtocolMapping = null };
        await catalog.LoadAsync(force: true); Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, Assert.Single(store.Load().Settings.Profiles).ProtocolMapping?.Revision);
        await controller.DisconnectAsync();
        await controller.StartAsync(saved);
        using var second = await server.AcceptTcpClientAsync();
        Assert.Empty(controller.GameState.Character.Resources);
    }

    [AvaloniaFact]
    public async Task RefreshUpdatesUnopenedProfilesAndLeavesOtherEndpointsUntouched()
    {
        var path = Path.Combine(Path.GetTempPath(), "wandur-saved-refresh-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        var mapping = new WorldMapping { WorldId = "test", Endpoint = new("mud.example", 4000), SchemaFingerprint = new('a', 64), GeneratedAt = DateTimeOffset.UtcNow,
            Bindings = [new() { Source = new("GMCP", "Char.Vitals", "/hp"), Target = new("character", "resource", "health", "current"), Label = "Health" }] };
        var world = new WorldListing { Id = "test", Name = "Test", Host = "mud.example", Port = 4000, ProtocolMapping = mapping };
        var json = JsonSerializer.Serialize(new { schema_version = 2, format = "wandur.directory", fetched_at = DateTimeOffset.UtcNow, worlds = new[] { world } }, ModelJson.Options);
        var profile = world.ToProfile() with { ProtocolMapping = null, Name = "Custom name" };
        var other = profile with { Id = Guid.NewGuid(), Host = "other.example" };
        var store = new SettingsStore(Path.Combine(path, "settings.json"));
        store.Save(new ClientSettings { Theme = "Paper", Profiles = [profile, other] });
        using var http = new HttpClient(new CatalogHandler(() => json));
        using var catalog = new WorldCatalog(Path.Combine(path, "directory.json"), http: http);
        await using var sessions = new SessionWorkspace(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), store, new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore(), catalog: catalog);
        await catalog.LoadAsync(); Dispatcher.UIThread.RunJobs();
        var saved = store.Load().Settings;
        Assert.Equal(2, saved.Profiles.Count);
        Assert.Equal(profile.Id, saved.Profiles[0].Id);
        Assert.Equal("Custom name", saved.Profiles[0].Name);
        Assert.NotNull(saved.Profiles[0].ProtocolMapping);
        Assert.Equal(other, saved.Profiles[1]);
        Assert.Equal("Paper", saved.Theme);
        Assert.False(sessions.Active.Controller.HasSession);
        Assert.Empty(sessions.Active.Controller.GameState.Character.Resources);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingCatalogMappingRetainsCacheAndNewCatalogMappingReplacesIt(bool hasNewMapping)
    {
        var path = Path.Combine(Path.GetTempPath(), "wandur-mapping-cache-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        using var server = new TcpListener(IPAddress.Loopback, 0); server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        var cached = new WorldMapping { WorldId = "test", Endpoint = new("127.0.0.1", port), SchemaFingerprint = new('a', 64), GeneratedAt = DateTimeOffset.UtcNow,
            Bindings = [new() { Source = new("GMCP", "Char.Vitals", "/hp"), Target = new("character", "resource", "health", "current"), Label = "Health" }] };
        var latest = cached with { Revision = 2, Bindings = [cached.Bindings[0] with { Source = new("GMCP", "Char.Vitals", "/hp_new") }] };
        var world = new WorldListing { Id = "test", Name = "Test", Host = "127.0.0.1", Port = port, ProtocolMapping = hasNewMapping ? latest : null };
        File.WriteAllText(Path.Combine(path, "directory.json"), JsonSerializer.Serialize(new { schema_version = 2, format = "wandur.directory", fetched_at = DateTimeOffset.UtcNow, worlds = new[] { world } }, ModelJson.Options));
        var profile = world.ToProfile() with { ProtocolMapping = cached, Name = "My saved name" };
        var store = new SettingsStore(Path.Combine(path, "settings.json"));
        store.Save(new ClientSettings { Theme = "Paper", Profiles = [profile] });
        using var catalog = new WorldCatalog(Path.Combine(path, "directory.json"), http: OfflineHttp.Client());
        await using var sessions = new SessionWorkspace(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), store, new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore(), catalog: catalog);
        await sessions.OpenAsync(profile);
        using var socket = await server.AcceptTcpClientAsync();
        var expectedRevision = hasNewMapping ? 2 : 1;
        Assert.Equal(expectedRevision, sessions.Active.Profile!.ProtocolMapping?.Revision);
        var packet = new byte[] { 255, 251, 201, 255, 250, 201 }.Concat(Encoding.UTF8.GetBytes("Char.Vitals {\"hp\":42,\"hp_new\":99}")).Concat(new byte[] { 255, 240 }).ToArray();
        await socket.GetStream().WriteAsync(packet);
        for (var i = 0; i < 10; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); sessions.Active.Controller.FlushOutput(); }
        Assert.Equal(hasNewMapping ? 99 : 42, sessions.Active.Controller.GameState.Character.Resources["health"].Current!.Value);
        using var browser = new WorldBrowserViewModel(catalog, sessions);
        browser.Attach(action => action());
        var saved = browser.SaveSelectedWorld();
        Assert.NotNull(saved);
        Assert.Equal(expectedRevision, saved.ProtocolMapping?.Revision);
        Assert.Equal(expectedRevision, Assert.Single(store.Load().Settings.Profiles).ProtocolMapping?.Revision);
        Assert.Equal("My saved name", saved.Name);
        Assert.Equal("Paper", store.Load().Settings.Theme);
    }

}
