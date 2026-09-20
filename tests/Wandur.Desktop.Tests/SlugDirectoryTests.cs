using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Wandur.Core.Discovery;
using Wandur.Core.Scripting;
using Wandur.Core.Settings;
using Wandur.Desktop.Services;
using Wandur.Models;

namespace Wandur.Desktop.Tests;

/// <summary>The directory identifies a world by a slug the client never parses. Opening one applies its pack, its
/// mapping (matched by endpoint, whatever world id the worker wrote) and fetches its art from the slug route, which
/// the saved worlds list then shrinks into a thumbnail.</summary>
public sealed class SlugDirectoryTests
{
    private sealed class DirectoryHandler(string snapshot, byte[] art) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(request.RequestUri.AbsolutePath switch
            {
                "/directory" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(snapshot) },
                "/worlds/legends-of-the-jedi/art" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(art) },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            });
        }
    }

    private static byte[] Picture()
    {
        using var bitmap = new WriteableBitmap(new PixelSize(160, 100), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var pixels = bitmap.Lock())
        {
            var row = new byte[pixels.RowBytes];
            for (var x = 0; x < 160; x++) { row[x * 4] = 0x9F; row[x * 4 + 1] = 0x6E; row[x * 4 + 2] = 0x3C; row[x * 4 + 3] = 0xFF; }
            for (var y = 0; y < 100; y++) System.Runtime.InteropServices.Marshal.Copy(row, 0, pixels.Address + y * pixels.RowBytes, row.Length);
        }
        using var stream = new MemoryStream();
        bitmap.Save(stream, new PngBitmapEncoderOptions());
        return stream.ToArray();
    }

    [AvaloniaFact]
    public async Task OpeningASlugListedWorldAppliesItsPackItsMappingByEndpointAndItsArtFromTheSlugRoute()
    {
        var path = Path.Combine(Path.GetTempPath(), "wandur-slug-directory-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        using var server = new TcpListener(IPAddress.Loopback, 0); server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        // The worker's mapping file still carries the old id; the listing carries the slug.
        var mapping = new WorldMapping { WorldId = "mudverse:509", Endpoint = new("127.0.0.1", port), SchemaFingerprint = new('a', 64), GeneratedAt = DateTimeOffset.UtcNow, Revision = 1,
            Bindings = [new() { Source = new("GMCP", "Char.Vitals", "/hp"), Target = new("character", "resource", "health", "current"), Label = "Health" }] };
        var world = new WorldListing
        {
            Id = "legends-of-the-jedi", Name = "Legends of the Jedi", Summary = "A galaxy far away", Host = "127.0.0.1", Port = port,
            GeneratedArtworkPath = "worlds/legends-of-the-jedi/art", ProtocolMapping = mapping,
            Scripts = [new() { Id = "ship-panel", Name = "Ship panel", Description = "Ship telemetry.", Source = "mud.panel('ship', { title: 'Ship' }).label('a', { text: 'v1' });", Provenance = ScriptPackInfo.Generated, Version = 1 }]
        };
        var snapshot = JsonSerializer.Serialize(new { schema_version = 2, format = "wandur.directory", fetched_at = DateTimeOffset.UtcNow, worlds = new[] { world } },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        using var handler = new DirectoryHandler(snapshot, Picture());
        var store = new SettingsStore(Path.Combine(path, "settings.json"));
        var profile = world.ToProfile() with { ProtocolMapping = null };
        store.Save(new ClientSettings { Profiles = [profile] });
        using var catalog = new WorldCatalog(Path.Combine(path, "cache.json"), new Uri("http://directory.invalid/"), new HttpClient(handler));
        await catalog.LoadAsync();
        Assert.Null(catalog.Warning);
        Assert.Equal("legends-of-the-jedi", Assert.Single(catalog.Worlds).Id);
        using var thumbnails = new WorldThumbnails(catalog);
        await using var sessions = new SessionWorkspace(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), store,
            new MemoryPasswordVault(), new MemoryRoomMapStore(), new InlineScriptFactory(), new MemoryScriptLibraryStore(), catalog: catalog);
        try
        {
            await sessions.OpenAsync(profile);
            using var socket = await server.AcceptTcpClientAsync();
            // The pack is keyed by the endpoint, so it attaches whatever the listing's id looks like.
            var pack = Assert.Single(sessions.Active.Controller.ScriptLibrary.Items, entry => entry.IsPack);
            Assert.Equal("ship-panel", pack.Pack!.PackId);
            Assert.Equal(ScriptPackInfo.IdFor($"127.0.0.1:{port}:False", "ship-panel"), pack.Id);
            // The mapping applied by endpoint and was saved with the profile, its world id untouched.
            Assert.Equal("mudverse:509", sessions.Active.Profile!.ProtocolMapping!.WorldId);
            Assert.Equal("mudverse:509", Assert.Single(store.Load().Settings.Profiles).ProtocolMapping!.WorldId);
            Assert.NotNull(sessions.Active.Controller.GameState);
            // The thumbnail comes from the slug route through the catalog's cache.
            var thumbnail = await thumbnails.GetAsync(profile);
            Assert.NotNull(thumbnail);
            Assert.Equal(WorldThumbnails.Width * 2, thumbnail!.PixelSize.Width);
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, (await catalog.GetArtAsync(world))![..4]);
            lock (handler.Requests) Assert.Equal(["/directory", "/worlds/legends-of-the-jedi/art"], handler.Requests);
        }
        finally
        {
            foreach (var tab in sessions.Tabs.ToArray()) await tab.Controller.DisconnectAsync();
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }
}
