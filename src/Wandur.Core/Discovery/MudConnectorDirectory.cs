using L = Wandur.Core.Localization.Strings;
using System.Net;
using System.Text.RegularExpressions;
using Wandur.Core.Settings;

namespace Wandur.Core.Discovery;

public sealed record WorldNameSuggestion(string Name, Uri Listing);

public interface IWorldDirectory
{
    Task<WorldNameSuggestion?> LookupAsync(string host, int port, CancellationToken cancellationToken = default);
}

/// <summary>Best-effort adapter for TMC's public big list; it does not connect to game servers.</summary>
public sealed class MudConnectorDirectory(HttpClient http) : IWorldDirectory
{
    private sealed record Entry(string Name, string Host, int Port);
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private Entry[] _entries = [];
    private DateTimeOffset _expires;
    public static readonly Uri BigList = new("https://www.mudconnect.com/cgi-bin/search.cgi?mode=mobile_biglist");
    private static readonly Regex Links = new("\\bhref\\s*=\\s*[\"'](?<url>[^\"']+)[\"']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
    public static MudConnectorDirectory Shared { get; } = new(new HttpClient { Timeout = TimeSpan.FromSeconds(6), MaxResponseContentBufferSize = 1_048_576 });

    public async Task<WorldNameSuggestion?> LookupAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        host = host.Trim().TrimEnd('.').ToLowerInvariant();
        // Local addresses cannot have public directory listings.
        if (Uri.CheckHostName(host) != UriHostNameType.Dns || !host.Contains('.') || host.EndsWith(".local") || host.EndsWith(".localhost")) return null;
        await _refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_expires <= DateTimeOffset.UtcNow)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, BigList);
                request.Headers.UserAgent.ParseAdd("Wandur/0.1");
                using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (html.Length > 1_048_576) throw new HttpRequestException(L.DirectoryResponseWasTooLarge);
                var entries = new List<Entry>();
                foreach (Match match in Links.Matches(html))
                {
                    if (!Uri.TryCreate(new Uri("https://www.mudconnect.com"), WebUtility.HtmlDecode(match.Groups["url"].Value), out var link) ||
                        link.Host != "www.mudconnect.com" || link.AbsolutePath != "/cgi-bin/telnet.cgi") continue;
                    var query = new Dictionary<string, string>();
                    foreach (var part in link.Query.TrimStart('?').Split('&'))
                    {
                        var pair = part.Split('=', 2);
                        if (pair.Length == 2) query[WebUtility.UrlDecode(pair[0])] = WebUtility.UrlDecode(pair[1]);
                    }
                    if (!query.TryGetValue("mud", out var name) || string.IsNullOrWhiteSpace(name) || name.Length > 100 || name.Any(char.IsControl) ||
                        !query.TryGetValue("url", out var endpoint) || !WorldAddress.TryParse(endpoint, out var address) || address!.Port is null) continue;
                    entries.Add(new(name.Trim(), address.Host.TrimEnd('.'), address.Port.Value));
                }
                if (entries.Count == 0) throw new HttpRequestException(L.DirectoryListingsCouldNotBeRead);
                _entries = entries.ToArray();
                _expires = DateTimeOffset.UtcNow.AddMinutes(30);
            }
        }
        finally { _refresh.Release(); }
        cancellationToken.ThrowIfCancellationRequested();
        var matches = _entries.Where(e => string.Equals(e.Host, host, StringComparison.OrdinalIgnoreCase)).ToArray();
        var exact = matches.Where(e => e.Port == port).Select(e => e.Name).Distinct().ToArray();
        var names = exact.Length > 0 ? exact : matches.Select(e => e.Name).Distinct().ToArray();
        return names.Length == 1 ? new(names[0], new Uri("https://www.mudconnect.com/cgi-bin/search.cgi?mode=mud_listing&mud=" + Uri.EscapeDataString(names[0]))) : null;
    }
}
