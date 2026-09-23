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
    /// <summary>A world opens with a snapshot at most this old; an older one is refreshed first, so a pack changed on
    /// the server since the last fetch is applied on open instead of five minutes later.</summary>
    public static readonly TimeSpan OpenRefreshAge = TimeSpan.FromSeconds(60);
    /// <summary>How long an open waits for that refresh before it goes ahead with the cached copy.</summary>
    public static readonly TimeSpan OpenRefreshTimeout = TimeSpan.FromSeconds(5);
    private readonly TimeProvider _time;
    private long? _lastAttempt;
    private long? _lastLoaded;
    public IReadOnlyList<WorldListing> Worlds { get; private set; } = [];
    /// <summary>The snapshot's own timestamp, which describes the source listings, not when this process fetched it.</summary>
    public DateTimeOffset? FetchedAt { get; private set; }
    /// <summary>How long ago this process fetched the snapshot in use, or null when it came from the cache on disk.</summary>
    public TimeSpan? SnapshotAge => _lastLoaded is { } loaded ? _time.GetElapsedTime(loaded) : null;
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
            var previous = Worlds;
            await Task.Run(() => AdoptRenamedWorlds(previous, parsed.Worlds), token);
            (Worlds, FetchedAt) = (parsed.Worlds, parsed.FetchedAt);
            _lastLoaded = _time.GetTimestamp();
            if (response.Headers.TryGetValues("X-Wandur-Stale", out var values) && values.Contains("true"))
                Warning = L.ShowingTheSavedDirectoryWhileTheServerRefreshesIt;
            Wandur.Core.Diagnostics.ThemeTrace.Write("catalog.load",
                $"ok from {BaseUri}directory worlds={parsed.Worlds.Length} themed={parsed.Worlds.Count(w => w.Theme is not null)}");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or ArgumentException or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            Warning = ex is HttpRequestException && ex.Message.StartsWith("Set MUDVERSE") ? ex.Message :
                L.Format(L.DirectoryUnavailableAtStartTheDirectoryServerSavedWorlds, BaseUri);
            Wandur.Core.Diagnostics.ThemeTrace.Write("catalog.load",
                $"FAILED from {BaseUri}directory: {ex.GetType().Name}: {ex.Message}; serving {Worlds.Count} cached worlds");
        }
        finally { Loading = false; _loadLock.Release(); Changed?.Invoke(); }
    }

    /// <summary>Refreshes before a listed world opens when the snapshot in use is older than <see cref="OpenRefreshAge"/>
    /// or came from disk. Bounded by <see cref="OpenRefreshTimeout"/>; a failure or timeout leaves the cached copy in
    /// use, exactly as before. A world the snapshot does not list opens without waiting: the periodic refresh will
    /// list it, and the client never delays a private world on the directory's account.</summary>
    public async Task RefreshBeforeOpenAsync(string host, int port, bool tls, CancellationToken cancellationToken = default)
    {
        if (FindEndpoint(host, port, tls) is null || SnapshotAge is { } age && age < OpenRefreshAge) return;
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(OpenRefreshTimeout);
        try { await LoadAsync(force: true, bounded.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { /* The cached copy opens the world. */ }
    }

    /// <summary>Ids are the directory's opaque strings, so a snapshot may rename a world (the move from
    /// <c>mudverse:&lt;n&gt;</c> to slugs did that for every world). Anything this client keeps under an id is the
    /// artwork cache; a world that vanished by id but is listed at the same endpoint adopts the new id by
    /// copying its picture under the new key, so nothing is downloaded twice. A cache that cannot be read or
    /// written costs at most that copy; the snapshot itself is never held back.</summary>
    private void AdoptRenamedWorlds(IReadOnlyList<WorldListing> previous, IReadOnlyList<WorldListing> current)
    {
        if (previous.Count == 0 || current.Count == 0) return;
        var ids = current.Select(world => world.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var old in previous)
        {
            if (ids.Contains(old.Id) || old.WebOnly || old.Host.Length == 0 || (old.Port ?? old.TlsPort) is null) continue;
            var renamed = current.Where(world => !world.WebOnly && SameHost(world.Host, old.Host) && world.Port == old.Port && world.TlsPort == old.TlsPort).Take(2).ToArray();
            // Only the id moved: a picture regenerated for a changed description is still fetched afresh.
            if (renamed.Length != 1 || renamed[0].ArtKey == old.ArtKey || (old with { Id = renamed[0].Id }).ArtKey != renamed[0].ArtKey) continue;
            try
            {
                if (_cache.ReadArtwork(renamed[0].ArtKey) is null && _cache.ReadArtwork(old.ArtKey) is { } art) _cache.WriteArtwork(renamed[0].ArtKey, art);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
    }
    private static bool SameHost(string left, string right) => string.Equals(left.TrimEnd('.'), right.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    public WorldListing? FindEndpoint(string host, int port, bool tls)
    {
        var matches = Worlds.Where(w => !w.WebOnly && SameHost(w.Host, host) && (tls ? w.TlsPort == port : w.Port == port)).Take(2).ToArray();
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
