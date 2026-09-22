using L = Wandur.Core.Localization.Strings;
using System.Text.Json;

namespace Wandur.Core.Discovery;

/// <summary>Versioned disk/wire contract owned by Wandur; provider responses stop at the mapper.</summary>
internal sealed record WorldDirectorySnapshot
{
    public int SchemaVersion { get; init; } = 2;
    public string Format { get; init; } = "wandur.directory";
    public required DateTimeOffset FetchedAt { get; init; }
    public required WorldListing[] Worlds { get; init; }
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };
    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static WorldDirectorySnapshot Parse(string json, out bool legacy)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var version = root.GetProperty("schema_version").GetInt32();
        WorldDirectorySnapshot snapshot;
        legacy = version == 1 && !root.TryGetProperty("format", out _);
        if (legacy)
        {
            if (root.TryGetProperty("source", out var source) && source.GetString() != "MUDVerse")
                throw new JsonException(L.UnsupportedDirectorySource);
            snapshot = new() { FetchedAt = root.GetProperty("fetched_at").GetDateTimeOffset(),
                Worlds = root.GetProperty("games").EnumerateArray().Select(MudVerseMapper.Map).ToArray() };
        }
        else
        {
            if (version != 2 || root.GetProperty("format").GetString() != "wandur.directory")
                throw new JsonException(L.UnsupportedDirectoryVersion);
            snapshot = JsonSerializer.Deserialize<WorldDirectorySnapshot>(json, Options) ?? throw new JsonException(L.EmptyDirectory);
        }
        // The directory wire format often uses JSON null for empty strings; treat them as blank so a
        // missing banner or artwork path never discards an otherwise valid listing.
        snapshot = snapshot with
        {
            Worlds = (snapshot.Worlds ?? Array.Empty<WorldListing>()).Select(w => w with
            {
                Summary = w.Summary ?? "",
                Description = w.Description ?? "",
                Host = w.Host ?? "",
                BannerUrl = w.BannerUrl ?? "",
                GeneratedArtworkPath = w.GeneratedArtworkPath ?? "",
                Tags = w.Tags ?? [],
                Availability = w.Availability is null ? new() : w.Availability with { ArchiveReason = w.Availability.ArchiveReason ?? "" },
                Population = w.Population is null ? new() : w.Population with { ReportedRange = w.Population.ReportedRange ?? "" },
                Features = w.Features is null ? new() : NormalizeFeatures(w.Features),
                Community = w.Community ?? new(),
                Source = w.Source is null ? new() : w.Source with
                {
                    Provider = w.Source.Provider ?? "",
                    Name = w.Source.Name ?? "",
                    RecordId = w.Source.RecordId ?? "",
                    ListingUrl = w.Source.ListingUrl ?? ""
                }
            }).ToArray()
        };
        if (snapshot.Worlds is null || snapshot.Worlds.Any(w => w is null || string.IsNullOrWhiteSpace(w.Id) || string.IsNullOrWhiteSpace(w.Name)
            || w.Summary is null || w.Description is null || w.Host is null || w.Source is null || w.Availability is null || w.Population is null
            || w.Features is null || w.Community is null || w.Community.Rating is < 0 or > 5
            || w.Community.RatingCount is < 0 || w.Community.ReviewCount is < 0 || w.Community.MonthlyVotes is < 0 || w.Community.Rank is <= 0
            || w.Tags is null || w.Tags.Any(t => t is null) || w.BannerUrl is null || w.GeneratedArtworkPath is null)
            || snapshot.Worlds.Select(w => w.Id).Distinct(StringComparer.Ordinal).Count() != snapshot.Worlds.Length)
            throw new JsonException(L.InvalidDirectoryEntries);
        return snapshot with { Worlds = snapshot.Worlds.OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase).ToArray() };
    }

    private static WorldFeatures NormalizeFeatures(WorldFeatures features) => features with
    {
        Theme = features.Theme ?? "",
        Kind = features.Kind ?? "",
        Language = features.Language ?? "",
        Location = features.Location ?? "",
        Codebase = features.Codebase ?? "",
        Roleplaying = features.Roleplaying ?? "",
        PlayerKilling = features.PlayerKilling ?? "",
        WorldSize = features.WorldSize ?? "",
        DevelopmentStatus = features.DevelopmentStatus ?? ""
    };
}
