using L = Wandur.Core.Localization.Strings;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Wandur.Core.Settings;

namespace Wandur.Core.Discovery;

/// <summary>Wandur's provider-independent directory entry. Unknown measurements remain null.</summary>
public sealed record WorldListing
{
    [System.Text.Json.Serialization.JsonConverter(typeof(Wandur.Core.Discovery.WorldMappingConverter))]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Wandur.Models.WorldMapping? ProtocolMapping { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Summary { get; init; } = "";
    public string Description { get; init; } = "";
    public string Host { get; init; } = "";
    public int? Port { get; init; }
    public int? TlsPort { get; init; }
    public bool WebOnly { get; init; }
    public WorldSource Source { get; init; } = new();
    public WorldAvailability Availability { get; init; } = new();
    public WorldPopulation Population { get; init; } = new();
    public WorldFeatures Features { get; init; } = new();
    public WorldCommunity Community { get; init; } = new();
    public DateTimeOffset? EstablishedAt { get; init; }
    public string[] Tags { get; init; } = [];
    public string WebsiteUrl { get; init; } = "";
    public string DiscordUrl { get; init; } = "";
    public string PlayUrl { get; init; } = "";
    public string BannerUrl { get; init; } = "";
    // Relative to the configured Wandur directory service, never an upstream API credential URL.
    public string GeneratedArtworkPath { get; init; } = "";
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public WorldTheme? Theme { get; init; }
    // Optional generated script pack. A listing without it changes nothing about a world's library.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public WorldScriptListing[]? Scripts { get; init; }

    /// <summary>The supplied scripts this client is willing to attach, in listing order.</summary>
    [JsonIgnore] public IReadOnlyList<WorldScriptListing> SupportedScripts =>
        Scripts is null ? [] : Scripts.Where(script => script is { IsValid: true })
            .GroupBy(script => script.Id, StringComparer.Ordinal).Select(group => group.First())
            .Take(Wandur.Core.Scripting.WorldScriptLibraryStore.MaximumScripts).ToArray();

    [JsonIgnore] public string Address => WebOnly ? L.BrowserBasedWorld : string.IsNullOrEmpty(Host) ? L.ConnectionNotListed :
        (Port ?? TlsPort) is { } port ? $"{(Host.Contains(':') ? $"[{Host}]" : Host)}:{port}" : Host;
    [JsonIgnore] public bool CanConnect => !WebOnly && Uri.CheckHostName(Host) != UriHostNameType.Unknown && (Port ?? TlsPort) is > 0 and <= 65535;
    [JsonIgnore] public bool HasSuppliedArtwork => BannerUrl.Length > 0;
    [JsonIgnore] public string RatingSummary => Community.Rating is { } rating && Community.RatingCount is > 0
        ? L.Format(Community.RatingCount == 1 ? L.RatingOne : L.RatingMany, rating.ToString("0.#", CultureInfo.CurrentCulture), Community.RatingCount)
        : Community.RatingCount == 0 ? L.NoRatingsYet : L.RatingNotSupplied;
    [JsonIgnore] public string SearchTags => string.Join(" ", new[] { Features.Theme, Features.Kind, Features.Language,
        Features.Location, Features.Codebase, Features.Roleplaying, Features.PlayerKilling, Features.WorldSize,
        Features.DevelopmentStatus, Population.ReportedRange }.Concat(Tags));
    [JsonIgnore] public string StatusText => Availability.Archived ? L.ArchivedListing : Availability.Online switch
    {
        true => L.LastReportedOnline, false => L.NotConfirmedOnline, _ => L.AvailabilityUnknown
    };
    [JsonIgnore] public string PopulationSummary => Population.AverageCount is { } average
        ? L.Format(L.PlayersOnAverage, average.ToString("0.#", CultureInfo.CurrentCulture))
        : Population.ReportedRange is { Length: > 0 } range ? L.Format(L.PlayersListedRange, range)
        : Population.LatestCount is { } latest ? L.Format(L.PlayersLastObserved, latest) : L.PlayerCountUnknown;
    /// <summary>The artwork cache key: the directory's id, taken as an opaque string, plus what the picture was made
    /// from. A world whose id changes on the server keeps its picture through <see cref="WorldCatalog"/>, which
    /// re-keys cached art by endpoint when a snapshot renames a world.</summary>
    [JsonIgnore] public string ArtKey
    {
        get
        {
            var subject = HasSuppliedArtwork ? $"{Id}\nsupplied\n{BannerUrl}" : $"{Id}\n{Name}\n{Summary}\n{Description}";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(subject)));
        }
    }

    /// <summary>The listing's mapping when it was generated for this exact endpoint. The mapping's world id is the
    /// worker's label and is not compared with the listing's id: a mapping file may predate a rename.</summary>
    public Wandur.Models.WorldMapping? MappingForEndpoint(string host, int port, bool tls)
    {
        var endpoint = new Wandur.Models.WorldEndpoint(host, port, tls);
        return Wandur.Models.MappingValidation.Endpoint(endpoint) && Wandur.Models.MappingValidation.IsValid(ProtocolMapping)
            && ProtocolMapping!.Endpoint.Matches(endpoint) ? ProtocolMapping : null;
    }

    public ConnectionProfile ToProfile(bool tls = false)
    {
        var profile = new ConnectionProfile { Name = Name[..Math.Min(Name.Length, 100)], Host = Host,
            Port = tls ? TlsPort ?? throw new ArgumentException(L.ThisWorldHasNoTLSPort) : Port ?? TlsPort ?? 0,
            UseTls = tls || Port is null, Theme = Theme,
            Codebase = Features.Codebase is { Length: > 0 and <= 100 } codebase && !codebase.Any(char.IsControl) ? codebase : "" };
        if (!CanConnect) throw new ArgumentException(L.ThisListingHasNoSupportedMUDConnection);
        profile = profile with { ProtocolMapping = MappingForEndpoint(profile.Host, profile.Port, profile.UseTls) };
        profile.Validate();
        return profile;
    }
}

/// <summary>One script a directory supplies for a world. Source is never executed by the directory client.</summary>
public sealed record WorldScriptListing
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Source { get; init; } = "";
    public string Provenance { get; init; } = "";
    public int Version { get; init; }

    [JsonIgnore] public bool IsValid =>
        Id.Length is > 0 and <= 120 && !Id.Any(char.IsControl) &&
        Name.Trim().Length is > 0 and <= 120 && !Name.Any(char.IsControl) &&
        Description.Length <= 1024 && !Description.Any(c => char.IsControl(c) && c is not ('\n' or '\t')) &&
        Source.Length > 0 && Encoding.UTF8.GetByteCount(Source) <= Wandur.Core.Scripting.WorldScriptStore.MaximumBytes &&
        Wandur.Core.Scripting.ScriptPackInfo.IsSupported(Provenance) && Version >= 0;
}

/// <summary>Community measurements belong to the listing's source, not a global ranking.</summary>
public sealed record WorldCommunity
{
    public decimal? Rating { get; init; }
    public int? RatingCount { get; init; }
    public int? ReviewCount { get; init; }
    public int? Rank { get; init; }
    public int? MonthlyVotes { get; init; }
}

public sealed record WorldSource
{
    public string Provider { get; init; } = "";
    public string Name { get; init; } = "";
    public string RecordId { get; init; } = "";
    public string ListingUrl { get; init; } = "";
    public DateTimeOffset? UpdatedAt { get; init; }
    public DateTimeOffset? ListedAt { get; init; }
}

public sealed record WorldAvailability
{
    public bool? Online { get; init; }
    public bool Archived { get; init; }
    public string ArchiveReason { get; init; } = "";
    public DateTimeOffset? CheckedAt { get; init; }
    public DateTimeOffset? LastOnlineAt { get; init; }
}

public sealed record WorldPopulation
{
    public int? LatestCount { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
    public decimal? AverageCount { get; init; }
    public string ReportedRange { get; init; } = "";
}

public sealed record WorldFeatures
{
    public string Theme { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Language { get; init; } = "";
    public string Location { get; init; } = "";
    public string Codebase { get; init; } = "";
    public string Roleplaying { get; init; } = "";
    public string PlayerKilling { get; init; } = "";
    public string WorldSize { get; init; } = "";
    public string DevelopmentStatus { get; init; } = "";
}
