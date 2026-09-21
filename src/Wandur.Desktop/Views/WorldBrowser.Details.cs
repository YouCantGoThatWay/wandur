using L = Wandur.Core.Localization.Strings;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Wandur.Core.Discovery;

namespace Wandur.Desktop.Views;

public sealed partial class WorldBrowserView
{
    private static Control ResultCard(WorldListing world)
    {
        var panel = new StackPanel { Spacing = 2, Margin = new Thickness(3, 4) };
        panel.Children.Add(new TextBlock { Text = world.Name, FontSize = 12, FontWeight = FontWeight.SemiBold, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
        var categories = string.Join(" · ", new[] { world.Features.Theme, world.Features.Kind, world.Features.Language }.Where(s => s.Length > 0));
        if (categories.Length > 0) panel.Children.Add(Ui.Text(categories, 10, "muted"));
        var population = world.Population.LatestCount is { } n ? L.Format(L.PlayersLastObserved, n) : world.PopulationSummary;
        panel.Children.Add(Ui.Text(population + " · " + world.StatusText, 10, "muted"));
        return panel;
    }

    private static Control TagBadges(IEnumerable<string> tags)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var tag in tags.Where(t => !string.IsNullOrWhiteSpace(t)))
        {
            var badge = Ui.Card(Ui.Text(tag, 11), 8);
            badge.Margin = new Thickness(0, 0, 6, 6);
            badge.Bind(Border.CornerRadiusProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SmallCornerRadius"));
            panel.Children.Add(badge);
        }
        return panel;
    }

    private static Control GameplayBadges(WorldListing w) => TagBadges(new[]
    {
        w.Features.Roleplaying.Length > 0 ? L.Format(L.RoleplayingValue, w.Features.Roleplaying) : "",
        w.Features.PlayerKilling.Length > 0 ? L.Format(L.PlayerKillingValue, w.Features.PlayerKilling) : "",
        w.Features.DevelopmentStatus
    });

    private static Control Highlights(WorldListing world)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 10 };
        var population = world.Population.LatestCount?.ToString() ?? L.Unknown;
        var players = Ui.Card(Ui.Stack(Ui.TextKey(nameof(L.LastObservedPlayersHeading), 10, "muted"), Ui.Text(population, 23),
            Ui.Text(world.Population.ObservedAt is { } time ? time.ToLocalTime().ToString("g") : L.NoObservationTimeSupplied, 10, "muted")), 12);
        var community = Ui.Card(Ui.Stack(Ui.TextKey(nameof(L.COMMUNITYRATING), 10, "muted"), Ui.Text(world.RatingSummary, 14),
            Ui.Text(world.Source.Name.Length > 0 ? L.Format(L.SourceRating, world.Source.Name) : L.DirectoryRating, 10, "muted")), 12);
        grid.Children.Add(players); grid.Children.Add(community); Grid.SetColumn(community, 1);
        return grid;
    }

    private static Control FormattedDescription(string description)
    {
        if (string.IsNullOrWhiteSpace(description)) return Ui.TextKey(nameof(L.NoFurtherDescriptionProvided), 13, "muted");
        var panel = new StackPanel { Spacing = 9 };
        var paragraph = new List<string>();
        void Flush()
        {
            if (paragraph.Count == 0) return;
            panel.Children.Add(new SelectableTextBlock { Text = string.Join("\n", paragraph), TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 21 });
            paragraph.Clear();
        }
        // Interpret only plain-text paragraph, list and heading conventions. No HTML execution.
        foreach (var raw in description.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) { Flush(); continue; }
            var bullet = Regex.Match(line, @"^(?:[-*•]\s+|(?<number>\d+[.)])\s+)(?<text>.+)$", RegexOptions.None, TimeSpan.FromMilliseconds(100));
            if (bullet.Success)
            {
                Flush();
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("24,*"), ColumnSpacing = 4 };
                row.Children.Add(Ui.Text(bullet.Groups["number"].Success ? bullet.Groups["number"].Value : "•", 13, "muted"));
                var text = new SelectableTextBlock { Text = bullet.Groups["text"].Value, TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 21 };
                row.Children.Add(text); Grid.SetColumn(text, 1); panel.Children.Add(row);
            }
            else if (line.Length <= 80 && (line.EndsWith(':') || line.StartsWith("## ")))
            {
                Flush(); var heading = Ui.Text(line.TrimStart('#', ' ').TrimEnd(':'), 14);
                heading.FontWeight = FontWeight.SemiBold; heading.Margin = new Thickness(0, 8, 0, 2); panel.Children.Add(heading);
            }
            else paragraph.Add(raw);
        }
        Flush(); return panel;
    }

    private static Control ActivityFacts(WorldListing world)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("155,*"), RowSpacing = 8, ColumnSpacing = 12 };
        void Fact(string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var row = grid.RowDefinitions.Count; grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var title = Ui.Text(label, 11, "muted"); var content = Ui.Text(value, 12);
            Grid.SetRow(title, row); Grid.SetRow(content, row); Grid.SetColumn(content, 1); grid.Children.Add(title); grid.Children.Add(content);
        }
        Fact(L.ListedPlayerRange, world.Population.ReportedRange);
        Fact(L.AveragePlayers, world.Population.AverageCount?.ToString("0.#"));
        Fact(L.LastObservedPlayers, world.Population.LatestCount?.ToString());
        if (world.Population.LatestCount.HasValue) Fact(L.PlayersObserved, world.Population.ObservedAt?.ToLocalTime().ToString("g"));
        Fact(L.StatusChecked, world.Availability.CheckedAt?.ToLocalTime().ToString("g"));
        Fact(L.LastReached, world.Availability.LastOnlineAt?.ToLocalTime().ToString("g"));
        if (world.Availability.Archived) Fact(L.ArchiveReason, world.Availability.ArchiveReason);
        var source = world.Source.Name.Length > 0 ? world.Source.Name : L.Directory;
        Fact(L.Format(L.SourceReviews, source), world.Community.ReviewCount?.ToString());
        Fact(L.Format(L.SourceRank, source), world.Community.Rank is { } rank ? "#" + rank : null);
        Fact(L.MonthlyVotes, world.Community.MonthlyVotes?.ToString());
        Fact(L.ListingUpdated, world.Source.UpdatedAt?.ToLocalTime().ToString("d"));
        return grid;
    }
}
