using L = Wandur.Core.Localization.Strings;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Wandur.Core.Discovery;

/// <summary>The only place that interprets MUDVerse's listing fields.</summary>
internal static class MudVerseMapper
{
    public static WorldListing Map(JsonElement game)
    {
        if (Number(game, "id") is not > 0 || string.IsNullOrWhiteSpace(Text(game, "name")))
            throw new JsonException(L.InvalidDirectoryIdentity);
        var id = Number(game, "id")!.Value.ToString(CultureInfo.InvariantCulture);
        var connection = Child(game, "connection"); var urls = Child(game, "urls");
        var status = Child(game, "status"); var dates = Child(game, "dates");
        var reviews = Child(game, "reviews"); var ranking = Child(game, "ranking");
        var tags = Child(game, "tags"); var categories = Child(tags, "categories");
        string Category(string key) => Text(Child(categories, key), "name");
        var banner = Url(urls, "banner");
        if (Uri.TryCreate(banner, UriKind.Absolute, out var image) && image.Host == "assets.mudverse.com"
            && image.AbsolutePath == "/listings/default_connectbanner.png") banner = "";
        var custom = Child(tags, "custom");
        return new WorldListing
        {
            Id = "mudverse:" + id, Name = Text(game, "name"), Summary = Text(game, "intro"), Description = Text(game, "description"),
            Host = Text(connection, "host").ToLowerInvariant().TrimEnd('.'), Port = Port(connection, "port"), TlsPort = Port(connection, "tls_port"),
            WebOnly = Boolean(status, "web_only") == true,
            EstablishedAt = Date(dates, "created"),
            Community = new() { Rating = Child(reviews, "average_rating") is { ValueKind: JsonValueKind.Number } rating && rating.TryGetDecimal(out var value) && value is >= 0 and <= 5 ? value : null,
                RatingCount = NonNegative(reviews, "rating_count"), ReviewCount = NonNegative(reviews, "count"),
                Rank = Number(ranking, "rank") is > 0 and var rank ? rank : null, MonthlyVotes = NonNegative(ranking, "monthly_votes") },
            Source = new() { Provider = "mudverse", Name = "MUDVerse", RecordId = id, ListingUrl = Url(urls, "mudverse"),
                UpdatedAt = Date(dates, "updated"), ListedAt = Date(dates, "listed") },
            Availability = new() { Online = Boolean(status, "confirmed_online"), Archived = Boolean(status, "archived") == true,
                ArchiveReason = Text(status, "archive_reason"), CheckedAt = Date(status, "last_crawled"), LastOnlineAt = Date(status, "last_successful_connect") },
            Population = new() { LatestCount = Number(status, "latest_players") is >= 0 and var count ? count : null,
                ObservedAt = Date(status, "mssp_collected_at"), ReportedRange = Category("play_count") },
            Features = new() { Theme = Category("theme"), Kind = Category("type"), Language = Category("language"),
                Location = Category("location"), Codebase = Category("codebase"), Roleplaying = Category("roleplaying"),
                PlayerKilling = Category("player_killing"), WorldSize = Category("game_size"), DevelopmentStatus = Category("game_status") },
            Tags = custom.ValueKind == JsonValueKind.Array ? custom.EnumerateArray().Select(t => Text(t, "name"))
                .Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() : [],
            WebsiteUrl = Url(urls, "website"), DiscordUrl = Url(urls, "discord"), PlayUrl = Url(urls, "play"), BannerUrl = banner,
            GeneratedArtworkPath = $"games/{id}/art"
        };
    }

    private static JsonElement Child(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var child) ? child : default;
    private static int? Number(JsonElement value, string name) => Child(value, name) is { ValueKind: JsonValueKind.Number } n && n.TryGetInt32(out var i) ? i : null;
    private static int? NonNegative(JsonElement value, string name) => Number(value, name) is >= 0 and var n ? n : null;
    private static int? Port(JsonElement value, string name) => Number(value, name) is > 0 and <= 65535 and var port ? port : null;
    private static bool? Boolean(JsonElement value, string name) => Child(value, name).ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
    private static DateTimeOffset? Date(JsonElement value, string name) => Child(value, name) is { ValueKind: JsonValueKind.String } d
        && DateTimeOffset.TryParse(d.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) ? time : null;
    private static string Url(JsonElement value, string name)
    {
        var text = Child(value, name) is { ValueKind: JsonValueKind.String } s ? WebUtility.HtmlDecode(s.GetString() ?? "").Trim() : "";
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 ? text : "";
    }
    private static string Text(JsonElement value, string name)
    {
        var text = Child(value, name) is { ValueKind: JsonValueKind.String } s ? s.GetString() ?? "" : "";
        text = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\\'", "'");
        // Render the provider's description markup as plain text, retaining paragraph breaks.
        text = Regex.Replace(text, @"<(script|style)\b[^>]*>[\s\S]*?</\1\s*>", "", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
        text = Regex.Replace(text, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
        text = Regex.Replace(text, @"</(?:p|div|li|h[1-6])\s*>", "\n\n", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
        text = Regex.Replace(text, @"</?[a-zA-Z][^>]*>", "", RegexOptions.None, TimeSpan.FromMilliseconds(100));
        text = WebUtility.HtmlDecode(text);
        text = string.Join("\n", text.Split('\n').Select(line => line.Trim()));
        return Regex.Replace(text, @"\n{3,}", "\n\n", RegexOptions.None, TimeSpan.FromMilliseconds(100)).Trim();
    }
}
