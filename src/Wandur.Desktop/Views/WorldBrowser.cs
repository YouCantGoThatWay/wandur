using L = Wandur.Core.Localization.Strings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Wandur.Core.Discovery;
using Wandur.Desktop.ViewModels;
using System.ComponentModel;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Styling;

namespace Wandur.Desktop.Views;

public sealed partial class WorldBrowserView : UserControl
{
    private readonly WorldCatalog _catalog;
    private readonly WorldBrowserViewModel _model;
    private bool _renderingResults;
    private bool _restoringScroll;
    private int _detailRenderVersion;
    private readonly TextBox _search = new() { Name = "DirectorySearch", [!TextBox.PlaceholderTextProperty] = LocalizedText.Binding(nameof(L.SearchWorldsThemesOrAnAddress)), MaxLength = 150, FontSize = 14, MinHeight = 36 };
    private readonly ListBox _list = new() { Name = "DirectoryResults", Background = Brushes.Transparent };
    private readonly TextBlock _count = Ui.Text("", 12, "muted");
    private readonly TextBlock _status = Ui.Text("", 11, "muted");
    private readonly TextBlock _feedback = Ui.Text("", 12, "muted");
    private readonly ComboBox _connectionFilter = LocalizedChoice("DirectoryConnectionFilter", nameof(L.AllWorlds), nameof(L.MUDConnections), nameof(L.BrowserOnlyWorlds));
    private readonly CheckBox _onlineFilter = new() { Name = "DirectoryOnlineFilter", [!ContentControl.ContentProperty] = LocalizedText.Binding(nameof(L.ReportedOnline)), FontSize = 12 };
    private readonly StackPanel _details = new() { Spacing = 8 };
    private readonly Image _image = new() { Name = "DirectoryArtwork", Stretch = Stretch.Uniform, IsVisible = false };
    private readonly TextBlock _artStatus = Ui.TextKey(nameof(L.LoadingArtwork), 11, "muted");
    // Muted by colour rather than opacity: dimming text over a light panel costs more contrast than it looks.
    private readonly TextBlock _artPlaceholder = new() { Text = L.LoadingArtwork, FontSize = 13, Classes = { "muted" }, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _artFrame;
    private readonly ScrollViewer _detailScroll;
    private readonly WrapPanel _listingActions = new() { Orientation = Orientation.Horizontal };
    private readonly Border _listingToolbar;
    private CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly Button _refresh;
    private CancellationTokenSource? _selection;
    private Bitmap? _bitmap;
    private bool _artLoading;
    private bool _closed = true;

    public WorldBrowserView(WorldCatalog catalog, SessionWorkspace sessions)
        : this(new WorldBrowserViewModel(catalog, sessions), catalog) { }

    public WorldBrowserView(WorldBrowserViewModel model, WorldCatalog catalog)
    {
        _catalog = catalog; _model = model; DataContext = model;
        var heading = Ui.TextKey(nameof(L.FindAMUD), 22);
        heading.Name = "DirectoryHeading";
        heading.FontWeight = FontWeight.SemiBold;
        heading.VerticalAlignment = VerticalAlignment.Center;
        _refresh = Ui.ToolbarIconKey(new Button { Name = "RefreshDirectory", Command = _model.RefreshCommand },
            "M 13,5 A 5.5,5.5 0 1 0 13.2,10 M 13,1 V 5 H 9", nameof(L.RefreshDirectory), inset: true);
        _refresh.VerticalAlignment = VerticalAlignment.Center;
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 20, Children = { heading, _refresh } };
        Grid.SetColumn(_refresh, 1);
        _search.TextChanged += (_, _) => Filter();
        _connectionFilter.SelectionChanged += (_, _) => Filter();
        _onlineFilter.IsCheckedChanged += (_, _) => Filter();
        _onlineFilter.Bind(ToolTip.TipProperty, LocalizedText.Binding(nameof(L.BasedOnTheDirectorySLatestReportNotA)));
        StyleResults();
        _list.ItemTemplate = new FuncDataTemplate<WorldListing>((world, _) => world is null ? null : ResultCard(world));
        _list.SelectionChanged += (_, _) =>
        {
            if (_renderingResults) return;
            _model.SelectedWorld = _list.SelectedItem as WorldListing;
            ShowSelection();
        };
        var sortRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 4, Children = { _onlineFilter, _sort } };
        Grid.SetColumn(_sort, 1);
        _sort.Bind(ToolTip.TipProperty, LocalizedText.Binding(nameof(L.SortWorldsByRelevanceNamePopulationRatingOrDate)));
        _sort.Bind(Avalonia.Automation.AutomationProperties.NameProperty, LocalizedText.Binding(nameof(L.SortWorldsBy)));
        var filters = new StackPanel { Spacing = 6, Children = { _connectionFilter, sortRow } };
        _sort.SelectionChanged += (_, _) => Filter();
        var left = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), RowSpacing = 8, Children = { filters, _count, _list } };
        Grid.SetRow(_count, 1); Grid.SetRow(_list, 2);
        _artFrame = Ui.Card(new Grid { Children = { _image, _artPlaceholder } }, 0);
        _artFrame.Name = "DirectoryArtworkFrame";
        _artFrame.ClipToBounds = true;
        _artFrame.MinHeight = 72;
        _detailScroll = new ScrollViewer { Name = "DirectoryDetailsScroll", Content = _details, Margin = new Thickness(12), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        _details.SizeChanged += (_, _) => SizeArtwork();
        _listingToolbar = Ui.Toolbar(_listingActions, "DirectoryListingToolbar");
        _listingToolbar.IsVisible = false;
        _detailScroll.ScrollChanged += (_, _) => { if (!_closed && !_restoringScroll) _model.DetailScrollOffset = _detailScroll.Offset.Y; };
        var detailLayout = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Children = { _listingToolbar, _detailScroll } };
        Grid.SetRow(_detailScroll, 1);
        var detailCard = Ui.Card(detailLayout, 0);
        detailCard.ClipToBounds = true;
        Grid.SetColumn(detailCard, 1);
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("5*,7*"), ColumnSpacing = 12, Children = { left, detailCard } };
        body.ColumnDefinitions[0].MaxWidth = 360;
        var footer = new StackPanel { Spacing = 4, Children = { _feedback, _status } };
        var advanced = CreateAdvancedSearch();
        var contents = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), RowSpacing = 6, Margin = new Thickness(10), Children = { _search, advanced, body, footer } };
        Grid.SetRow(advanced, 1); Grid.SetRow(body, 2); Grid.SetRow(footer, 3);
        Grid.SetRow(contents, 1);
        Content = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Children = { Ui.Toolbar(header, "DirectoryTitleBar"), contents } };
        _timer.Tick += async (_, _) => { if (_bitmap is null && !_artLoading && _list.SelectedItem is WorldListing world) await LoadArtAsync(world); };
        _ready = true; ApplyQueryToControls();
    }

    private void StyleResults()
    {
        // Reuse the app's keyboard/focus-aware row template; keep all card styling local to this list.
        _list.Classes.Add("world-list");
        _list.Styles.Add(new Style(s => s.OfType<ListBox>().Class("world-list").Descendant().OfType<ListBoxItem>())
        {
            Setters = { new Setter(PaddingProperty, new Thickness(0)), new Setter(MarginProperty, new Thickness(0, 0, 0, 6)) }
        });
        _list.Styles.Add(new Style(s => s.OfType<Border>().Class("directory-result"))
        {
            Setters = { new Setter(Border.BackgroundProperty, new DynamicResourceExtension("PanelBrush")),
                new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("LineBrush")) }
        });
        _list.Styles.Add(new Style(s => s.OfType<ListBoxItem>().Class(":pointerover").Descendant().OfType<Border>().Class("directory-result"))
        {
            Setters = { new Setter(Border.BackgroundProperty, new DynamicResourceExtension("ButtonHoverBrush")) }
        });
        _list.Styles.Add(new Style(s => s.OfType<ListBoxItem>().Class(":selected").Descendant().OfType<Border>().Class("directory-result"))
        {
            Setters = { new Setter(Border.BackgroundProperty, new DynamicResourceExtension("WorldSelectionBrush")),
                new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("AccentBrush")) }
        });
        _list.Styles.Add(new Style(s => s.OfType<ListBoxItem>().Class(":focus-visible").Descendant().OfType<Border>().Class("directory-result"))
        {
            Setters = { new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("TextBrush")) }
        });
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _closed = false; _restoringScroll = true;
        if (_lifetime.IsCancellationRequested) { _lifetime.Dispose(); _lifetime = new(); }
        _model.PropertyChanged += ModelChanged;
        _model.Attach(action => Dispatcher.UIThread.Post(action));
        // Initial values may be unchanged (especially the shared empty results array).
        // Render the complete snapshot instead of depending on change notifications.
        ModelChanged(_model, new PropertyChangedEventArgs(null));
        var resultsOffset = _model.ResultsScrollOffset;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_closed && _list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } scroll)
                scroll.Offset = new Vector(0, resultsOffset);
        }, DispatcherPriority.Loaded);
        _timer.Start();
        _ = _model.LoadAsync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (!_restoringScroll) _model.DetailScrollOffset = _detailScroll.Offset.Y;
        _model.ResultsScrollOffset = _list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()?.Offset.Y ?? 0;
        _closed = true; _timer.Stop();
        _model.PropertyChanged -= ModelChanged;
        _model.Detach();
        _lifetime.Cancel(); _selection?.Cancel(); _selection?.Dispose(); _selection = null;
        _image.Source = null; _bitmap?.Dispose(); _bitmap = null;
        base.OnDetachedFromVisualTree(e);
    }


    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_closed) return;
        if (e.PropertyName is null or nameof(WorldBrowserViewModel.Query)) ApplyQueryToControls();
        if (e.PropertyName is null or nameof(WorldBrowserViewModel.FacetOptions)) RefreshFilterOptions();
        if (e.PropertyName is null or nameof(WorldBrowserViewModel.Results))
        {
            _renderingResults = true;
            _list.ItemsSource = _model.Results;
            _list.SelectedItem = _model.SelectedWorld;
            _renderingResults = false;
            ShowSelection();
        }
        _refresh.IsEnabled = _model.CanRefresh;
        _count.Text = _model.Count;
        _status.Text = _model.Status;
        _feedback.Text = _model.Feedback;
        _feedback.IsVisible = !string.IsNullOrWhiteSpace(_model.Feedback);
        _status.IsVisible = !string.IsNullOrWhiteSpace(_model.Status);
        _filterSummary.Text = _model.FilterSummary;
        _filterHint.Text = _model.FilterHint;
    }
    private void Filter()
    {
        if (!_ready || _updatingFilters) return;
        _model.Query = new WorldBrowserQuery
        {
            Search = _search.Text ?? "", Connection = _connectionFilter.SelectedIndex,
            OnlineOnly = _onlineFilter.IsChecked == true, Sort = _sort.SelectedIndex,
            Facets = _facets.Where(f => f.Input.SelectedIndex > 0).ToDictionary(f => f.Key, f => f.Input.SelectedItem as string ?? ""),
            MinimumPlayers = _minimumPlayers.Value, MaximumPlayers = _maximumPlayers.Value,
            Rating = _rating.SelectedIndex, TlsOnly = _tlsFilter.IsChecked == true
        };
    }
    private void ShowSelection()
    {
        _selection?.Cancel(); _selection?.Dispose();
        _selection = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _bitmap?.Dispose(); _bitmap = null; _image.Source = null; _image.IsVisible = false;
        _artLoading = false;
        var selectedId = (_list.SelectedItem as WorldListing)?.Id;
        var sameWorld = selectedId == _model.ScrollWorldId;
        var offset = sameWorld ? _model.DetailScrollOffset : 0;
        var version = ++_detailRenderVersion;
        _restoringScroll = true;
        _model.ScrollWorldId = selectedId;
        _model.DetailScrollOffset = offset;
        _detailScroll.Offset = new Vector(0, offset);
        Dispatcher.UIThread.Post(() =>
        {
            if (!_closed && version == _detailRenderVersion)
            {
                _detailScroll.Offset = new Vector(0, offset);
                _restoringScroll = false;
            }
        }, DispatcherPriority.Loaded);
        _artPlaceholder.IsVisible = true; _artPlaceholder.Text = L.LoadingArtwork;
        _artFrame.MinHeight = 72;
        _details.Children.Clear();
        _listingActions.Children.Clear();
        _listingToolbar.IsVisible = _list.SelectedItem is WorldListing;
        if (_list.SelectedItem is not WorldListing world)
        {
            _details.Children.Add(Ui.Text(_model.EmptyTitle, 17));
            _details.Children.Add(Ui.Text(_model.EmptyDescription, 13, "muted"));
            return;
        }
        _artStatus.Text = world.HasSuppliedArtwork ? L.LoadingSuppliedArtwork : L.LoadingIllustration;
        var title = Ui.Text(world.Name, 22);
        title.Name = "DirectoryWorldTitle"; title.FontWeight = FontWeight.SemiBold;
        _details.Children.Add(title);
        _details.Children.Add(_artFrame);
        var tags = new[] { world.Features.Theme, world.Features.Kind, world.Features.Language }
            .Concat(world.Tags).Append(world.PopulationSummary);
        if (world.Population.LatestCount is { } count) tags = tags.Append(L.Format(L.PlayersLastObserved, count));
        var badges = TagBadges(tags.Distinct(StringComparer.OrdinalIgnoreCase));
        badges.Name = "DirectoryWorldTags";
        _details.Children.Add(badges);
        if (!string.IsNullOrWhiteSpace(world.Summary)) _details.Children.Add(Ui.Text(world.Summary, 13));
        var description = FormattedDescription(world.Description);
        description.Name = "DirectoryWorldDescription";
        _details.Children.Add(description);
        _details.Children.Add(Ui.Text(world.StatusText + "\n" + world.Address, 11, "muted"));
        var tls = new CheckBox { [!ContentControl.ContentProperty] = LocalizedText.Binding(nameof(L.UseTLS)), IsVisible = world.TlsPort.HasValue && world.CanConnect, IsChecked = _model.UseTls,
            FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 4, 0) };
        tls.IsCheckedChanged += (_, _) => _model.UseTls = tls.IsChecked == true;
        var add = new Button { Name = "AddDirectoryWorld", [!ContentControl.ContentProperty] = LocalizedText.Binding(nameof(L.AddToMyWorlds)), Command = _model.SaveCommand, IsEnabled = world.CanConnect,
            Width = double.NaN, Height = 30, FontSize = 12, Padding = new Thickness(10, 4) };
        add.Classes.Add("command-bar-button");
        var connect = new Button { Name = "ConnectDirectoryWorld", [!ContentControl.ContentProperty] = LocalizedText.Binding(nameof(L.Connect2)), Command = _model.ConnectCommand, IsEnabled = world.CanConnect,
            Width = double.NaN, Height = 30, FontSize = 12, Padding = new Thickness(10, 4) };
        connect.Classes.Add("command-bar-button");
        _listingActions.Children.Add(connect);
        _listingActions.Children.Add(add);
        _listingActions.Children.Add(tls);
        _details.Children.Add(_artStatus);
        _details.Children.Add(GameplayBadges(world));
        _details.Children.Add(Highlights(world));
        _details.Children.Add(Ui.TextKey(nameof(L.ActivityCommunity), 17));
        _details.Children.Add(ActivityFacts(world));
        _details.Children.Add(Ui.TextKey(nameof(L.GameplayConnection), 17));
        _details.Children.Add(CreateFacts(world));
        var links = new WrapPanel { Orientation = Orientation.Horizontal };
        AddLink(links, L.Website, world.WebsiteUrl); AddLink(links, L.Discord, world.DiscordUrl);
        AddLink(links, L.PlayInBrowser, world.PlayUrl);
        AddLink(links, L.Format(L.SourceListing, world.Source.Name), world.Source.ListingUrl);
        _details.Children.Add(links);
        var attribution = world.Source.UpdatedAt is { } updated
            ? L.Format(L.SourceDetailsUpdated, world.Source.Name, updated.ToLocalTime()) : L.Format(L.SourceDetails, world.Source.Name);
        _details.Children.Add(Ui.Text(attribution, 11, "muted"));
        _ = LoadArtAsync(world);
    }
    private static Grid CreateFacts(WorldListing world)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("155,*"), RowSpacing = 8, ColumnSpacing = 12 };
        void Fact(string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var title = Ui.Text(label, 11, "muted"); var content = Ui.Text(value, 12);
            Grid.SetRow(title, row); Grid.SetRow(content, row); Grid.SetColumn(content, 1);
            grid.Children.Add(title); grid.Children.Add(content);
        }
        Fact(L.Development, world.Features.DevelopmentStatus);
        Fact(L.Theme, world.Features.Theme);
        Fact(L.GameType, world.Features.Kind);
        Fact(L.Language, world.Features.Language);
        Fact(L.Established, world.EstablishedAt?.Year.ToString());
        Fact(L.TLSConnection, world.TlsPort is { } port ? $"{world.Host}:{port}" : null);
        Fact(L.Roleplaying, world.Features.Roleplaying);
        Fact(L.PlayerKilling, world.Features.PlayerKilling);
        Fact(L.WorldSize, world.Features.WorldSize);
        Fact(L.Codebase, world.Features.Codebase);
        Fact(L.Location, world.Features.Location);
        return grid;
    }

    private void AddLink(Panel links, string label, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0) return;
        var button = Ui.Button(label, async () =>
        {
            try { if (TopLevel.GetTopLevel(this) is { } topLevel) await topLevel.Launcher.LaunchUriAsync(uri); }
            catch (Exception) { _model.ReportLinkFailure(); }
        }, "quiet");
        button.Margin = new Thickness(0, 0, 8, 8);
        links.Children.Add(button);
    }
    private async Task LoadArtAsync(WorldListing world)
    {
        var selection = _selection;
        if (selection is null || selection.IsCancellationRequested) return;
        var token = selection.Token;
        _artLoading = true;
        try
        {
            var bytes = await _catalog.GetArtAsync(world, token);
            if (token.IsCancellationRequested || _closed || _selection != selection) return;
            if (bytes is null) { _artPlaceholder.Text = L.NoIllustrationAvailableYet; _artStatus.Text = L.YouCanStillBrowseTheDetailsAndConnect; return; }
            using var stream = new MemoryStream(bytes);
            var bitmap = new Bitmap(stream);
            _bitmap?.Dispose(); _bitmap = bitmap; _image.Source = bitmap; _image.IsVisible = true;
            _artPlaceholder.IsVisible = false; _artFrame.MinHeight = 0; SizeArtwork();
            _artStatus.Text = world.HasSuppliedArtwork ? L.Format(L.SuppliedArtwork, world.Source.Name) : L.AIIllustrationInspiredByThisWorldSDescription;
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!token.IsCancellationRequested && !_closed && _selection == selection)
            { _artPlaceholder.Text = L.ArtworkUnavailable; _artStatus.Text = L.WorldDetailsAreStillAvailableBelow; }
        }
        finally { if (_selection == selection) _artLoading = false; }
    }

    private void SizeArtwork()
    {
        if (_bitmap is null || _details.Bounds.Width <= 0) return;
        // Keep supplied banners compact and show generated illustrations without cropping.
        _image.Height = Math.Min(160, _details.Bounds.Width * _bitmap.PixelSize.Height / _bitmap.PixelSize.Width);
    }
}
