using System.Net;

namespace Wandur.Desktop.Tests;

/// <summary>
/// An <see cref="HttpMessageHandler"/> that never touches a socket: every request immediately
/// fails with 503 Service Unavailable. Use this for any test fixture that constructs a
/// <c>WorldCatalog</c> (or other HTTP-backed component) without caring about live network
/// behavior, so the test never depends on whether a local directory server happens to be running.
/// </summary>
public sealed class OfflineHttpHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
}

public static class OfflineHttp
{
    /// <summary>An <see cref="HttpClient"/> whose requests always fail fast with 503, without hitting the network.</summary>
    public static HttpClient Client() => new(new OfflineHttpHandler());
}
