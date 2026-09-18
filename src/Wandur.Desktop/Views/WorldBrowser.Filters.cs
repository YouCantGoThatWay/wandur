using L = Wandur.Core.Localization.Strings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;

namespace Wandur.Desktop.Views;

public sealed partial class WorldBrowserView
{
    private readonly List<(ComboBox Input, string Key)> _facets = [];
    private readonly ComboBox _sort = LocalizedChoice("DirectorySort", nameof(L.BestMatch), nameof(L.NameAZ), nameof(L.LastObservedPlayers), nameof(L.HighestRated), nameof(L.RecentlyUpdated), nameof(L.NewestWorlds));
    private readonly ComboBox _rating = LocalizedChoice("DirectoryRatingFilter", nameof(L.AnyRating), nameof(L.Label3Stars), nameof(L.Label4Stars), nameof(L.Label45Stars));
    private readonly NumericUpDown _minimumPlayers = PlayerNumber("DirectoryMinimumPlayers");
    private readonly NumericUpDown _maximumPlayers = PlayerNumber("DirectoryMaximumPlayers");
    private readonly CheckBox _tlsFilter = new() { Name = "DirectoryTlsFilter", [!ContentControl.ContentProperty] = LocalizedText.Binding(nameof(L.TLSAvailable)), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _filterSummary = Ui.TextKey(nameof(L.AdvancedSearch), 13);
    private readonly TextBlock _filterHint = Ui.TextKey(nameof(L.AllPreferencesCombinePlayerCountsAreLastObservedNot), 11, "muted");
    private bool _ready;
    private bool _updatingFilters;

    private static ComboBox Choice(string name, params string[] items) => new()
    { Name = name, ItemsSource = items, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
    private static ComboBox LocalizedChoice(string name, params string[] keys)
    {
        var choice = Choice(name, keys);
        choice.ItemTemplate = new FuncDataTemplate<string>((key, _) => key is null ? null : Ui.TextKey(key, 12));
        return choice;
    }
    private static NumericUpDown PlayerNumber(string name) => new()
    { Name = name, Minimum = 0, Maximum = int.MaxValue, Increment = 1, FormatString = "0", [!NumericUpDown.PlaceholderTextProperty] = LocalizedText.Binding(nameof(L.Any)), HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };

    private Control CreateAdvancedSearch()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), ColumnSpacing = 12, RowSpacing = 12 };
        var index = 0;
        void Add(string title, Control input)
        {
            var field = Ui.FieldKey(title, input);
            Grid.SetColumn(field, index % 5); Grid.SetRow(field, index / 5); grid.Children.Add(field); index++;
        }
        foreach (var facet in _model.Facets)
        {
            var input = Choice("Directory" + facet.Key + "Filter", L.Any);
            _facets.Add((input, facet.Key));
            input.SelectionChanged += (_, _) => Filter();
            Add(facet.LabelKey, input);
        }
        Add(nameof(L.MinObservedPlayers), _minimumPlayers);
        Add(nameof(L.MaxObservedPlayers), _maximumPlayers);
        Add(nameof(L.MinimumRating), _rating);
        Add(nameof(L.ConnectionSecurity), _tlsFilter);
        _minimumPlayers.ValueChanged += (_, _) => Filter();
        _maximumPlayers.ValueChanged += (_, _) => Filter();
        _rating.SelectionChanged += (_, _) => Filter();
        _tlsFilter.IsCheckedChanged += (_, _) => Filter();
        var content = Ui.Stack(grid, _filterHint);
        var expander = new Expander { Name = "DirectoryAdvancedSearch", Header = _filterSummary,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new ScrollViewer { Content = content, MaxHeight = 255, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
        var reset = Ui.ButtonKey(nameof(L.ClearSearchFilters), () => _model.ResetFiltersCommand.Execute(null), "quiet");
        reset.Name = "DirectoryResetFilters";
        reset.VerticalAlignment = VerticalAlignment.Top;
        // Reset stays visible even when the filters are collapsed.
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12, Children = { expander, reset } };
        Grid.SetColumn(reset, 1);
        // Let the fields use the full width; keep reset next to the collapsed header.
        expander.PropertyChanged += (_, e) =>
        {
            if (e.Property == Expander.IsExpandedProperty)
            {
                Grid.SetColumnSpan(expander, expander.IsExpanded ? 2 : 1);
                reset.IsVisible = !expander.IsExpanded;
            }
        };
        var resetExpanded = Ui.ButtonKey(nameof(L.ClearSearchFilters), () => _model.ResetFiltersCommand.Execute(null), "quiet");
        resetExpanded.Name = "DirectoryResetAdvancedFilters";
        Add(nameof(L.StartAgain), resetExpanded);
        return layout;
    }

    private void RefreshFilterOptions()
    {
        _updatingFilters = true;
        foreach (var (input, key) in _facets)
        {
            var selected = _model.Query.Facets.GetValueOrDefault(key);
            input.ItemsSource = new[] { L.Any }.Concat(_model.FacetOptions.GetValueOrDefault(key) ?? []).ToArray();
            input.SelectedIndex = 0;
            if (selected is not null) input.SelectedItem = selected;
        }
        _updatingFilters = false;
    }

    private void ApplyQueryToControls()
    {
        _updatingFilters = true;
        var query = _model.Query;
        _search.Text = query.Search; _connectionFilter.SelectedIndex = query.Connection;
        _onlineFilter.IsChecked = query.OnlineOnly; _sort.SelectedIndex = query.Sort;
        foreach (var (input, key) in _facets)
        {
            input.SelectedIndex = 0;
            if (query.Facets.TryGetValue(key, out var selected)) input.SelectedItem = selected;
        }
        _minimumPlayers.Value = query.MinimumPlayers; _maximumPlayers.Value = query.MaximumPlayers;
        _rating.SelectedIndex = query.Rating; _tlsFilter.IsChecked = query.TlsOnly;
        _updatingFilters = false;
    }
}
