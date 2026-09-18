using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wandur.Core.Discovery;

namespace Wandur.Core.Tests;

public sealed class ThemeImageTests
{
    private static string Fixture => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "world-theme-metallic.json"));
    private static byte[] Png => Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAgAAAAECAYAAACzzX7wAAAAEklEQVR4nGPY0mTzHx9moL0CABxATiGmNzirAAAAAElFTkSuQmCC");
    [Fact]
    public void MetallicSchemaRoundTripsAndBadImageDoesNotDiscardThePalette()
    {
        var theme = JsonSerializer.Deserialize<WorldTheme>(Fixture)!;
        Assert.Equal("metallic", theme.Surface); Assert.True(theme.Images.Chrome!.IsValid);
        Assert.Equal(theme, JsonSerializer.Deserialize<WorldTheme>(JsonSerializer.Serialize(theme)));
        foreach (var url in new[] { "file:///secret.png", "data:image/png;base64,data", "//other.test/a.png", "../secret.png", "https://user:secret@example.org/a.png" })
        {
            var node = JsonNode.Parse(Fixture)!; node["images"]!["chrome"]!["url"] = url;
            var parsed = JsonSerializer.Deserialize<WorldTheme>(node.ToJsonString())!;
            Assert.True(parsed.IsValid); Assert.Null(parsed.Images.Chrome);
        }
    }
    [Fact]
    public async Task ImagesAreCachedAndReusedOfflineByResolvedUrl()
    {
        var path = Path.Combine(Path.GetTempPath(), "wandur-theme-art-" + Guid.NewGuid(), "directory.json");
        var calls = 0;
        using var http = new HttpClient(new Handler(request => { calls++; Assert.Equal("http://localhost:8765/theme-assets/a.png", request.RequestUri!.AbsoluteUri); return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) }; }));
        var image = new WorldThemeImage { Url = "theme-assets/a.png" };
        using (var catalog = new WorldCatalog(path, new Uri("http://localhost:8765/"), http))
        {
            Assert.Equal(Png, await catalog.GetThemeImageAsync(image));
            Assert.Equal(Png, await catalog.GetThemeImageAsync(image));
            Assert.Equal(1, calls);
        }
        using var offline = new HttpClient(new Handler(_ => throw new HttpRequestException("offline")));
        using var reopened = new WorldCatalog(path, new Uri("http://localhost:8765/"), offline);
        Assert.Equal(Png, await reopened.GetThemeImageAsync(image));
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }
    [Fact]
    public async Task InvalidAndOversizedImagesAreRejected()
    {
        var path = Path.Combine(Path.GetTempPath(), "wandur-theme-bad-art-" + Guid.NewGuid(), "directory.json");
        foreach (var bytes in new[] { new byte[8], new byte[4 * 1024 * 1024 + 1], Png.Select((b, i) => i is >= 16 and < 20 ? (byte)255 : b).ToArray() })
        {
            using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
            using var catalog = new WorldCatalog(path, http: http);
            Assert.Null(await catalog.GetThemeImageAsync(new() { Url = "theme-assets/bad.png" }));
        }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(reply(request));
    }
}
