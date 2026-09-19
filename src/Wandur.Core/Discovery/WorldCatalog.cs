using L = Wandur.Core.Localization.Strings;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Wandur.Core.Discovery;

/// <summary>A local snapshot searched in-process. Credentials stay on the directory server.</summary>
public sealed partial class WorldCatalog : IWorldDirectory, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly IWorldCatalogCache _cache;
    private readonly SemaphoreSlim _loadLock = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);
    private readonly TimeProvider _time;
    private long? _lastAttempt;
    public IReadOnlyList<WorldListing> Worlds { get; private set; } = [];
    public DateTimeOffset? FetchedAt { get; private set; }
    public string? Warning { get; private set; }
    public bool Loading { get; private set; }
    public Uri BaseUri { get; }
    public event Action? Changed;

    public WorldCatalog(string cachePath, Uri? baseUri = null, HttpClient? http = null, TimeProvider? timeProvider = null)
        : this(new FileWorldCatalogCache(cachePath), baseUri, http, timeProvider) { }

    public WorldCatalog(IWorldCatalogCache cache, Uri? baseUri = null, HttpClient? http = null, TimeProvider? timeProvider = null)
    {
        _cache = cache;
        _time = timeProvider ?? TimeProvider.System;
        BaseUri = baseUri ?? new Uri((Environment.GetEnvironmentVariable("WANDUR_DIRECTORY_URL") ?? "https://api.wandur.net").TrimEnd('/') + "/");
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        _ownsHttp = http is null;
        try
        {
            if (_cache.ReadSnapshot() is { } json)
            {
                var snapshot = WorldDirectorySnapshot.Parse(json, out var legacy);
                (Worlds, FetchedAt) = (snapshot.Worlds, snapshot.FetchedAt);
                if (legacy)
                {
                    // Migration is optional when the cache directory is read-only. Keep the usable snapshot.
                    try { _cache.WriteSnapshot(snapshot.ToJson()); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or UnauthorizedAccessException or InvalidOperationException or KeyNotFoundException or FormatException)
        { Warning = L.TheLocalDirectoryCacheCouldNotBeRead; }
    }

    public async Task LoadAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var token = linked.Token;
        await _loadLock.WaitAsync(token);
        try
        {
            // Provider timestamps describe source listings, not server-generated metadata.
            // Throttle local attempts (including failures), never the upstream snapshot age.
            if (!force && _lastAttempt is { } attempted && _time.GetElapsedTime(attempted) < RefreshInterval) return;
            _lastAttempt = _time.GetTimestamp();
            Loading = true; Warning = null; Changed?.Invoke();
            using var response = await _http.GetAsync(new Uri(BaseUri, "directory"), token);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(token);
                if (error.Contains("Set MUDVERSE_API_KEY")) throw new HttpRequestException(L.SetMUDVERSEAPIKEYInDirectoryServerEnvAnd);
                response.EnsureSuccessStatusCode();
            }
            var json = await response.Content.ReadAsStringAsync(token);
            var parsed = WorldDirectorySnapshot.Parse(json, out _);
            token.ThrowIfCancellationRequested();
            _cache.WriteSnapshot(parsed.ToJson());
            (Worlds, FetchedAt) = (parsed.Worlds, parsed.FetchedAt);
            if (response.Headers.TryGetValues("X-Wandur-Stale", out var values) && values.Contains("true"))
                Warning = L.ShowingTheSavedDirectoryWhileTheServerRefreshesIt;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or ArgumentException or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            Warning = ex is HttpRequestException && ex.Message.StartsWith("Set MUDVERSE") ? ex.Message :
                L.Format(L.DirectoryUnavailableAtStartTheDirectoryServerSavedWorlds, BaseUri);
        }
        finally { Loading = false; _loadLock.Release(); Changed?.Invoke(); }
    }

    public WorldListing? FindEndpoint(string host, int port, bool tls)
    {
        var matches = Worlds.Where(w => !w.WebOnly && string.Equals(w.Host.TrimEnd('.'), host.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)
            && (tls ? w.TlsPort == port : w.Port == port)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    public IReadOnlyList<WorldListing> Search(string? query)
    {
        var terms = Normalize(query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0) return Worlds;
        return Worlds.Select(w => (World: w, Score: Score(w, terms))).Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).ThenBy(x => x.World.Name).Select(x => x.World).ToArray();
    }
    private static string Normalize(string text) => Regex.Replace(text.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static int Score(WorldListing world, string[] terms)
    {
        var name = Normalize(world.Name); var tags = Normalize(world.SearchTags); var host = Normalize($"{world.Host} {world.Port} {world.TlsPort}");
        var details = Normalize(world.Summary + " " + world.Description);
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var score = 0;
        foreach (var term in terms)
        {
            if (name == term) score += 100;
            else if (name.Contains(term)) score += 60;
            else if (host.Contains(term)) score += 50;
            else if (tags.Contains(term)) score += 35;
            else if (details.Contains(term)) score += 10;
            else if (term.Length >= 4 && words.Any(word => Distance(term, word) <= (term.Length >= 7 ? 2 : 1))) score += 20;
            else return 0;
        }
        return score;
    }
    private static int Distance(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 2) return 3;
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var diagonal = row[0]; row[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var old = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                diagonal = old;
            }
        }
        return row[b.Length];
    }
    public async Task<WorldNameSuggestion?> LookupAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        await LoadAsync(cancellationToken: cancellationToken);
        if (Worlds.Count == 0 && Warning is not null) throw new HttpRequestException(Warning);
        var matches = Worlds.Where(w => string.Equals(w.Host.TrimEnd('.'), host.TrimEnd('.'), StringComparison.OrdinalIgnoreCase) && (w.Port == port || w.TlsPort == port)).ToArray();
        return matches.Length == 1 && Uri.TryCreate(matches[0].Source.ListingUrl, UriKind.Absolute, out var uri) ? new(matches[0].Name, uri) : null;
    }
    public async Task<byte[]?> GetArtAsync(WorldListing world, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (_cache.ReadArtwork(world.ArtKey) is { } cached) return cached;
        Uri artUri;
        if (world.HasSuppliedArtwork)
        {
            if (!Uri.TryCreate(world.BannerUrl, UriKind.Absolute, out var supplied) || supplied.Scheme is not ("http" or "https") || supplied.UserInfo.Length > 0) return null;
            artUri = supplied;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(world.GeneratedArtworkPath)) return null;
            artUri = new Uri(BaseUri, world.GeneratedArtworkPath);
            if (!BaseUri.IsBaseOf(artUri)) return null;
        }
        using var response = await _http.GetAsync(artUri, token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var data = await response.Content.ReadAsByteArrayAsync(token);
        token.ThrowIfCancellationRequested();
        _cache.WriteArtwork(world.ArtKey, data);
        return data;
    }
    public void Dispose() { _lifetime.Cancel(); if (_ownsHttp) _http.Dispose(); }
}
