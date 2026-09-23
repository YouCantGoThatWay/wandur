using Wandur.Desktop.Services;
using Wandur.Core.Storage;
using Wandur.Core.Scripting;
using Wandur.Core.Mapping;
using L = Wandur.Core.Localization.Strings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Wandur.Core.Settings;
using Wandur.Desktop.Security;
using Wandur.Core.Discovery;
using Wandur.Desktop.Views;
using Wandur.Desktop.ViewModels;

namespace Wandur.Desktop;

public sealed class MainWindow : Window
{
    public SessionWorkspace Sessions { get; }
    public WorldCatalog Catalog { get; }
    public WorkspaceController Controller => Sessions.Active.Controller;
    public WorkspaceFactory Workspace { get; private set; }
    private readonly DockControl _dock;
    private readonly Avalonia.Threading.DispatcherTimer _catalogRefreshTimer;
    private readonly TextBlock _status = Ui.TextKey(nameof(L.ReadyToWander), 11, "muted");
    private readonly TextBlock _toolbarStatus = Ui.TextKey(nameof(L.ReadyToWander), 11, "muted");
    private readonly TextBlock _noticeText = Ui.Text("", 12);
    private readonly Border _notice;
    private readonly Border _toolbar;
    private (Size Size, Rect Title)? _fleetToolbarSurfaceKey;
    private readonly StackPanel _toolbarActions;
    private readonly Border _footer;
    private readonly Border _windowHeader;
    private readonly Border _metalDrag;
    private readonly Border _plaqueTitleHost;
    /// <summary>The drawn nameplate behind the title. Renders before its child, so the title sits on it.</summary>
    private readonly ThemePlaque _plaque = new() { Name = "ThemePlaqueShape" };
    private string _plaqueLabel = "Wandur";
    private Grid _chrome = null!;
    private StackPanel _headerStack = null!;
    private bool _toolbarInBand;
    private readonly StackPanel _titleBarIdentity;
    private readonly TextBlock _appTitle;
    private readonly TextBlock _footerHint;
    private readonly ThemeBezelHost _bezel;
    private readonly ThemeWindowSkinHost _windowSkin;
    private readonly ThemeOrnamentLayer _ornaments;
    private Thickness _footerBasePadding = new(12, 3);
    private const double MacTitleBarHeight = 52;
    private const double ToolbarHeight = 40;
    private readonly Button _disconnect;
    private readonly Button _connect;
    private readonly Button _browse;
    private readonly Button _fleetSettings;
    private readonly DesktopMenus _menus;
    private readonly ComboBox _worldPicker = new() { Name = "ToolbarWorlds", Width = 220, MinHeight = 28, Height = 28, FontSize = 12, Padding = new Thickness(9, 3), [!ComboBox.PlaceholderTextProperty] = LocalizedText.Binding(nameof(L.ChooseAWorld)) };
    private List<ConnectionProfile>? _profiles;
    private Guid? _pickerSessionProfileId;
    private bool? _channelsPreference;
    private bool _closing;
    private bool _closed;
    private bool _skinTitleActive;
    /// <summary>A toolbar move is queued for after layout; one at a time, and it reads the state when it runs.</summary>
    private bool _toolbarMovePending;
    private bool _titleChromePending;
    private Window? _dialog;
    private readonly IProfileAutomationFactory _profileAutomationFactory;
    private readonly IAgentClientServices? _agents;
    public ConnectionProfile? SelectedProfile => _worldPicker.SelectedItem as ConnectionProfile;
    public bool ToolbarVisible { get => _toolbar?.IsVisible ?? true; set { _toolbar.IsVisible = value; if (FleetSkin.IsActive) UpdateTitleBarInsets(); _menus.Refresh(); } }

    public MainWindow(Wandur.Desktop.Terminal.ITranscriptDisplayFactory displays, ISettingsStore store, IPasswordVault passwords, IRoomMapStore maps, IScriptRuntimeFactory scriptRuntimes, IWorldScriptLibraryStore scriptLibraryStore, IWorldKnowledgeStore? knowledge = null, WorldCatalog? catalog = null, IProfileAutomationFactory? profileAutomationFactory = null, IAgentClientServices? agents = null, Wandur.Core.Classification.RoomClassificationService? classification = null, IWorldUsageStore? usage = null)
    {
        _agents = agents;
        _profileAutomationFactory = profileAutomationFactory ?? new ProfileAutomationFactory(scriptRuntimes, scriptLibraryStore);
        Catalog = catalog ?? new WorldCatalog(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(store.FilePath))!, "directory.json"));
        Sessions = new(displays, store, passwords, maps, scriptRuntimes, scriptLibraryStore, knowledge, Catalog, agents, classification, usage);
        Title = "Wandur";
        Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://Wandur/Assets/icon-256.png")));
        Width = 1380; Height = 900; MinWidth = 1040; MinHeight = 680;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (OperatingSystem.IsMacOS())
        {
            ExtendClientAreaToDecorationsHint = true;
            WindowDecorations = WindowDecorations.Full;
            ExtendClientAreaTitleBarHeightHint = MacTitleBarHeight;
        }
        Workspace = new(Sessions, async () => await EditWorldAsync(), async () => await BrowseWorldsAsync(), async profile => await EditWorldAsync(profile), catalog: Catalog, editAutomation: async (controller, section) => await EditSessionAutomationAsync(controller, section));
        var layout = Workspace.CreateLayout();
        Workspace.InitLayout(layout);
        _dock = new DockControl { Name = "WorkspaceDock", Factory = Workspace, Layout = layout, InitializeFactory = true, InitializeLayout = false };

        _menus = new DesktopMenus(this);
        _worldPicker.ItemTemplate = new FuncDataTemplate<ConnectionProfile>((profile, _) =>
        {
            var label = new TextBlock { Text = profile?.Name, TextTrimming = TextTrimming.CharacterEllipsis };
            label.Bind(TextBlock.FontSizeProperty, new Avalonia.Data.Binding(nameof(ComboBox.FontSize)) { Source = _worldPicker });
            return label;
        });
        // Choosing a world here only changes what Connect will open; the theme follows the session.
        _worldPicker.SelectionChanged += (_, _) => _menus.Refresh();
        ToolTip.SetTip(_worldPicker, L.ChooseASavedWorldToOpenInASession);
        Avalonia.Automation.AutomationProperties.SetName(_worldPicker, L.ChooseAWorld);
        _worldPicker.Classes.Add("toolbar-world-picker");
        var connect = _connect = ToolbarButton("M 4,2 L 14,8 L 4,14 Z", "Connect", nameof(L.ConnectToTheSelectedWorldInASessionTab), filled: true);
        connect.Command = _menus.Connect;
        _disconnect = ToolbarButton("M 3,3 H 13 V 13 H 3 Z", "Disconnect", nameof(L.DisconnectTheActiveSession), filled: true);
        _disconnect.Command = _menus.Disconnect;
        var browse = _browse = ToolbarButton(FleetIcons.Search, "FindMud", nameof(L.SearchTheDirectoryForAMUDServer));
        browse.Click += async (_, _) => await BrowseWorldsAsync();
        _fleetSettings = ToolbarButton(FleetIcons.Settings, "FleetSettings", nameof(L.SettingsTitle));
        _fleetSettings.Click += async (_, _) => await PreferencesAsync();
        _fleetSettings.IsVisible = false;
        var divider = new Border { Width = 1, Height = 16, Margin = new Thickness(7, 0) };
        divider.Bind(Border.BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("LineBrush"));
        var connectionControls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(5, 0, 0, 0), Children = { connect, _disconnect } };
        var actions = _toolbarActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Children = { _worldPicker, connectionControls, divider, browse, _fleetSettings } };
        _toolbarStatus.Name = "SessionStatus";
        _toolbarStatus.VerticalAlignment = VerticalAlignment.Center;
        _toolbarStatus.TextTrimming = TextTrimming.CharacterEllipsis;
        _toolbarStatus.TextWrapping = TextWrapping.NoWrap;
        // Default chrome: identity on the left, session controls on the right. A modular skin with a
        // top-center plaque moves the live title into that plaque (window-centered) and hides this brand.
        _appTitle = new TextBlock { Name = "AppTitle", Text = "Wandur", FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
        _appTitle.Bind(TextBlock.ForegroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("TextBrush"));
        _titleBarIdentity = new StackPanel { Name = "TitleBarIdentity", Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { AppLogo(), _appTitle } };
        Grid.SetColumn(actions, 2);
        _toolbar = new Border { Name = "MainToolbar", Padding = new Thickness(12, 5), MinHeight = OperatingSystem.IsMacOS() ? MacTitleBarHeight : ToolbarHeight, BorderThickness = new Thickness(0, 0, 0, 1), Child = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 20, Children = { _titleBarIdentity, actions } } };
        // Reparenting/native insets can move this row without resizing it. The cached brush key
        // includes its arranged origin relative to the plaque, so refresh after arrangement too.
        _toolbar.LayoutUpdated += (_, _) => ApplyFleetToolbarSurface();
        _toolbar.Bind(Border.BorderBrushProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("LineBrush"));
        BindToolbarBackground();
        _menus.Fallback.IsVisible = !OperatingSystem.IsMacOS();
        // Keep a draggable native titlebar even when View > Toolbar hides its controls.
        _windowHeader = new Border
        {
            Name = "WindowHeader", MinHeight = OperatingSystem.IsMacOS() ? MacTitleBarHeight : 0,
            Child = _headerStack = new StackPanel { Children = { _menus.Fallback, _toolbar } }
        };
        _windowHeader.Bind(Border.BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("ChromeBrush"));
        if (OperatingSystem.IsMacOS())
        {
            UpdateTitleBarInsets();
            PropertyChanged += (_, e) => { if (e.Property == WindowStateProperty) UpdateTitleBarInsets(); };
            _windowHeader.PointerPressed += OnTitleBarPointerPressed;
        }

        var dismiss = Ui.Button("×", () => Controller.ShowNotice(null), "quiet");
        Grid.SetColumn(dismiss, 1);
        _notice = new Border { Padding = new Thickness(18, 7), Background = Brush.Parse("#483B2A"), IsVisible = false, Child = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { _noticeText, dismiss } } };
        _noticeText.Foreground = Brush.Parse("#FFE0B0");
        _noticeText.VerticalAlignment = VerticalAlignment.Center;
        var statusRight = Ui.TextKey(nameof(L.CtrlTabSwitchSessionsDragPanelHeadersToArrange), 10, "muted");
        statusRight.HorizontalAlignment = HorizontalAlignment.Right;
        _footerHint = statusRight;
        Grid.SetColumn(statusRight, 1);
        var footerSeparator = Ui.Text("·", 10); footerSeparator.Classes.Add("muted"); footerSeparator.VerticalAlignment = VerticalAlignment.Center;
        _status.VerticalAlignment = VerticalAlignment.Center;
        var footerLeft = new StackPanel { Name = "FooterStatus", Orientation = Orientation.Horizontal, Spacing = 8, Children = { _status, footerSeparator, _toolbarStatus } };
        _footer = new Border { Name = "WindowFooter", Padding = _footerBasePadding, BorderThickness = new Thickness(0, 1, 0, 0), Child = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { footerLeft, statusRight } } };
        _footer.Bind(Border.BorderBrushProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("LineBrush"));
        _footer.Bind(Border.BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("FooterBrush"));
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        root.Children.Add(_windowHeader); Grid.SetRow(_notice, 1); root.Children.Add(_notice); Grid.SetRow(_dock, 2); root.Children.Add(_dock); Grid.SetRow(_footer, 3); root.Children.Add(_footer);
        // Clip the shell when a bezel sets content_radius; without a frame the radius stays zero.
        var shell = new Border { Name = "ShellContent", Child = root, ClipToBounds = true };
        _bezel = new ThemeBezelHost { Name = "ThemeBezel", Child = shell };
        _windowSkin = new ThemeWindowSkinHost { Name = "ThemeWindowSkin", Child = _bezel };
        _ornaments = new ThemeOrnamentLayer { Name = "ThemeOrnaments" };
        _ornaments.ClearanceChanged += ApplyFooterClearance;
        _ornaments.ClearanceChanged += RequestTitleChromeUpdate;
        // Transparent hit targets over the metal header / plaque so decorative bitmaps (IsHitTestVisible
        // false) never swallow drags, while toolbar buttons below the inset keep their normal hits.
        _metalDrag = new Border
        {
            Name = "MetalHeaderDrag", HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top, Background = Brushes.Transparent, IsVisible = false
        };
        _metalDrag.PointerPressed += OnTitleBarPointerPressed;
        _plaqueTitleHost = new Border
        {
            Name = "PlaqueTitleHost", HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top, Background = Brushes.Transparent, IsVisible = false,
            Padding = new Thickness(24, 0)
        };
        _plaqueTitleHost.PointerPressed += OnTitleBarPointerPressed;
        _chrome = new Grid { Name = "ThemeChrome" };
        var chrome = _chrome;
        chrome.Children.Add(_windowSkin);
        chrome.Children.Add(_metalDrag);
        chrome.Children.Add(_ornaments);
        chrome.Children.Add(_plaqueTitleHost);
        chrome.SizeChanged += (_, _) => RequestTitleChromeUpdate();
        Content = chrome;
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty || e.Property == WindowDecorationMarginProperty)
                RequestTitleChromeUpdate();
        };
        ThemeService.Applied += OnThemeApplied;
        // The theme in force was usually applied before this window existed to hear about it, so pick it up
        // now. Without this the frame, band and nameplate only appeared after the first theme change, and a
        // freshly launched window showed none of them.
        OnThemeApplied();
        Sessions.Changed += Refresh;
        Wandur.Core.Localization.UiLanguage.Changed += RefreshLanguage;
        Closed += (_, _) =>
        {
            ThemeService.Applied -= OnThemeApplied;
            _ornaments.ClearanceChanged -= ApplyFooterClearance;
            _ornaments.ClearanceChanged -= RequestTitleChromeUpdate;
            Wandur.Core.Localization.UiLanguage.Changed -= RefreshLanguage;
        };
        Closing += OnClosing;
        _catalogRefreshTimer = new Avalonia.Threading.DispatcherTimer(WorldCatalog.RefreshInterval,
            Avalonia.Threading.DispatcherPriority.Background, async (_, _) => await RefreshCatalogAsync());
        Opened += async (_, _) => { _catalogRefreshTimer.Start(); await RefreshCatalogAsync(); };
        Closed += (_, _) => _catalogRefreshTimer.Stop();
        Refresh();
    }

    private void OnThemeApplied()
    {
        Classes.Set("fleet", FleetSkin.IsActive);
        _fleetToolbarSurfaceKey = null;
        _fleetSettings.IsVisible = FleetSkin.IsActive;
        foreach (var button in new[] { _connect, _browse, _fleetSettings }) button.Classes.Set("fleet-action", FleetSkin.IsActive);
        _connect.Content = FleetSkin.IsActive ? FleetIcons.Action(FleetIcons.Connect, nameof(L.Connect)) : Ui.ChromeGlyph("M 4,2 L 14,8 L 4,14 Z", true);
        _browse.Content = FleetSkin.IsActive ? FleetIcons.Action(FleetIcons.Search, nameof(L.FindAMUD)) : Ui.ChromeGlyph(FleetIcons.Search);
        _fleetSettings.Content = FleetIcons.Action(FleetIcons.Settings, nameof(L.SettingsTitle), true);
        // On Windows the fallback menu remains available, below the overlapping title/toolbar pair.
        _headerStack.Children.Remove(_menus.Fallback);
        _headerStack.Children.Insert(FleetSkin.IsActive ? _headerStack.Children.Count : 0, _menus.Fallback);
        _headerStack.Margin = default;
        _worldPicker.FontSize = FleetSkin.IsActive ? 14 : 12;
        _worldPicker.Height = FleetSkin.IsActive ? 32 : 28;
        if (_toolbar.Child is Grid toolbarGrid)
        {
            if (FleetSkin.IsActive && _worldPicker.Parent == _toolbarActions)
            {
                _toolbarActions.Children.Remove(_worldPicker);
                Grid.SetColumn(_worldPicker, 0);
                toolbarGrid.Children.Add(_worldPicker);
            }
            else if (!FleetSkin.IsActive && _worldPicker.Parent == toolbarGrid)
            {
                toolbarGrid.Children.Remove(_worldPicker);
                _toolbarActions.Children.Insert(0, _worldPicker);
            }
        }
        if (!FleetSkin.IsActive)
        {
            WindowDecorationProperties.SetElementRole(_metalDrag, WindowDecorationsElementRole.User);
            WindowDecorationProperties.SetElementRole(_plaqueTitleHost, WindowDecorationsElementRole.User);
            _plaque.IsHitTestVisible = true;
            if (!OperatingSystem.IsMacOS()) ExtendClientAreaToDecorationsHint = false;
        }
        _windowSkin.ApplyFromTheme();
        _bezel.ApplyFromTheme();
        _ornaments.ApplyFromTheme();
        ApplyFooterClearance();
        ApplyTitleChrome();
        if (!_toolbarInBand) { BindToolbarBackground(); UpdateTitleBarInsets(); }
    }

    /// <summary>
    /// When a skin paints a top-center plaque, the live window title sits in that plaque (centered on the
    /// window, not the content column) and the left toolbar brand is removed so it is not duplicated.
    /// </summary>
    /// <summary>
    /// The titlebar slot. A skin that hosts the toolbar moves the row into the painted band, so the
    /// frame's metal is the toolbar's background instead of a second bar being drawn underneath it.
    /// The row keeps its own hit testing; only its placement and backing move.
    /// </summary>
    /// <summary>
    /// Paints the nameplate from the theme, falling back to the palette for anything it leaves out. A
    /// theme that names no plaque gets none, and the title is drawn straight onto the band as before.
    /// </summary>
    private void ApplyPlaque(Wandur.Core.Discovery.WorldThemeSkinPlaque? plaque)
    {
        if (plaque is null)
        {
            _plaque.Fill = null;
            _plaque.Edge = null;
            _plaque.Accent = null;
            _plaque.WingFill = null;
            _plaque.WingEdge = null;
            _plaque.WingExtend = 0;
            _plaque.ShadowColor = null;
            _plaque.Padding = default;
            _plaque.InvalidateVisual();
            return;
        }
        IBrush? Named(string? colour, string resource) =>
            colour is { Length: > 0 } && Color.TryParse(colour, out var parsed)
                ? new SolidColorBrush(parsed)
                : Application.Current?.Resources[resource] as IBrush;

        _plaque.Shape = plaque.Shape;
        _plaque.Cap = plaque.Cap;
        _plaque.Fill = Named(plaque.Fill, "ChromeBrush");
        // The title's colour follows the plate, not the palette: white on a dark plate, the text colour on a
        // light one. TerminalTextBrush was wrong whenever the transcript and the plate disagreed.
        if (_plaque.Fill is ISolidColorBrush plateBrush)
        {
            var c = plateBrush.Color;
            var dark = 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B < 140 * 1.0;
            Application.Current!.Resources["PlaqueTextBrush"] = dark
                ? new SolidColorBrush(Color.Parse("#F2F5F8"))
                : Application.Current.Resources["TextBrush"];
        }
        _plaque.Edge = plaque.Edge is { Length: > 0 } ? Named(plaque.Edge, "BorderBrush") : null;
        _plaque.Accent = Named(plaque.Accent, "AccentBrush");

        var wings = plaque.Wings;
        _plaque.WingExtend = wings?.Extend ?? 0;
        _plaque.WingFill = wings is null ? null : Named(wings.Fill, "ChromeBrush");
        _plaque.WingEdge = wings?.Edge is { Length: > 0 } ? Named(wings.Edge, "BorderBrush") : null;

        if (plaque.Shadow is { } shadow && Color.TryParse(shadow.Color, out var shade))
        {
            _plaque.ShadowColor = shade;
            _plaque.ShadowOpacity = shadow.Opacity;
            _plaque.ShadowBlur = shadow.Blur;
            _plaque.ShadowOffset = shadow.Y;
        }
        else _plaque.ShadowColor = null;

        // The plate sits inside the wings, so the title's room is the theme's padding plus the wing reach.
        // Without the wing term a long title runs out over the bracket.
        var reach = plaque.Shape == "fleet" ? 0 : wings?.Extend ?? 0;
        var pad = plaque.Padding;
        var intrinsic = plaque.Shape == "fleet" ? FleetTitleLayout.TextInset : 0;
        _plaque.Padding = new Thickness(Math.Max(intrinsic, reach + pad.Left), pad.Top,
            Math.Max(intrinsic, reach + pad.Right), pad.Bottom);
        _plaque.InvalidateVisual();
    }

    /// <summary>The toolbar's own bar, bound to the theme. Kept so it can be disposed when the band hosts it.</summary>
    private IDisposable? _toolbarBackground;

    private void BindToolbarBackground()
    {
        _toolbarBackground?.Dispose();
        _toolbarBackground = null;
        _fleetToolbarSurfaceKey = null;
        _toolbar.ClearValue(Border.BackgroundProperty);
        if (FleetSkin.IsActive)
        {
            ApplyFleetToolbarSurface();
            return;
        }
        _toolbarBackground = _toolbar.Bind(Border.BackgroundProperty,
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("ToolbarBrush"));
    }

    /// <summary>The world's name as the nameplate shows it: engraved in capitals when there is a plate.</summary>
    private string PlateTitle() =>
        ThemeService.AppliedSkin?.Layout?.TitleBar?.Plaque is not null ? _plaqueLabel.ToUpperInvariant() : _plaqueLabel;

    private void ApplyToolbarSlot(WorldThemeSkinTitleBar? slot)
    {
        var inBand = slot is { HostsToolbar: true } && !_ornaments.IsCompact;
        _toolbarInBand = inBand;
        // Decided from where the toolbar actually is, not from a flag recording where it was last sent. A
        // flag drifts whenever a move is still queued as the next theme arrives, and a switched window then
        // settled with the toolbar in one place and the title chrome laid out as if it were in the other.
        var isInBand = _chrome.Children.Contains(_toolbar);
        if (inBand == isInBand) { if (inBand) PositionBandToolbar(slot!); return; }
        if (_toolbarMovePending) return;
        _toolbarMovePending = true;
        // This runs from SizeChanged, so the tree cannot be reparented here: moving a child mid-layout
        // leaves the Grid arranging against definitions it has already measured, which throws. The move
        // reads the state current when it runs, and the title chrome is laid out again once it has landed,
        // since whether the nameplate is shown depends on where the toolbar ended up.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _toolbarMovePending = false;
            if (_closed) return;
            MoveToolbar(_toolbarInBand, ThemeService.AppliedSkin?.Layout?.TitleBar);
            ApplyTitleChrome();
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private void MoveToolbar(bool inBand, WorldThemeSkinTitleBar? slot)
    {
        if (_closed || inBand != _toolbarInBand) return;
        if (inBand && slot is not null)
        {
            _headerStack.Children.Remove(_toolbar);
            if (!_chrome.Children.Contains(_toolbar))
            {
                Grid.SetRow(_toolbar, 0);
                Grid.SetColumn(_toolbar, 0);
                _chrome.Children.Add(_toolbar);
            }
            // Drop the resource binding before going transparent. A local value set over a live binding only
            // masks it, and so does ClearValue, which clears the local value and leaves the binding running:
            // the next theme change fired it and repainted the toolbar's own bar over the band, so a window
            // that switched theme differed from one opened in that theme. Only disposing the binding stops it.
            _toolbarBackground?.Dispose();
            _toolbarBackground = null;
            _toolbar.Background = Brushes.Transparent;
            _toolbar.BorderThickness = default;
            PositionBandToolbar(slot);
        }
        else
        {
            _chrome.Children.Remove(_toolbar);
            if (!_headerStack.Children.Contains(_toolbar))
            {
                if (FleetSkin.IsActive) _headerStack.Children.Insert(0, _toolbar);
                else _headerStack.Children.Add(_toolbar);
            }
            BindToolbarBackground();
            _toolbar.BorderThickness = new Thickness(0, 0, 0, 1);
            _toolbar.VerticalAlignment = VerticalAlignment.Stretch;
            _toolbar.HorizontalAlignment = HorizontalAlignment.Stretch;
            _toolbar.Margin = default;
            _toolbar.Height = double.NaN;
            UpdateTitleBarInsets();
        }
    }

    private void PositionBandToolbar(WorldThemeSkinTitleBar slot)
    {
        _toolbar.VerticalAlignment = VerticalAlignment.Top;
        _toolbar.HorizontalAlignment = slot.ToolbarAlign switch
        {
            "left" => HorizontalAlignment.Left,
            "center" => HorizontalAlignment.Center,
            _ => HorizontalAlignment.Right
        };
        _toolbar.Margin = new Thickness(slot.Padding.Left, slot.Padding.Top, slot.Padding.Right, slot.Padding.Bottom);
        // The band's height is the skin's top inset; the row centres in whatever is left of it.
        _toolbar.Height = Math.Max(0, _windowSkin.Inset.Top - slot.Padding.Top - slot.Padding.Bottom);
        _toolbar.MinHeight = 0;
        _toolbar.Padding = new Thickness(0);
    }

    private void RequestTitleChromeUpdate()
    {
        if (_titleChromePending || _closed) return;
        _titleChromePending = true;
        // Native margin callbacks can occur inside a property setter. Coalesce their notifications
        // onto a later dispatcher pass instead of nesting another title layout on that call stack.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _titleChromePending = false;
            if (!_closed) ApplyTitleChrome();
        });
    }

    private void ApplyTitleChrome()
    {
        _appTitle.FontWeight = FontWeight.SemiBold;
        SkinSize? header = null;
        SkinRect? headerText = null;
        if (!_ornaments.IsCompact && ThemeSkinResources.FromApplied() is { } skin)
        {
            foreach (var (_, entry) in skin.Overlays)
            {
                if (entry.Overlay.Anchor == "top-center")
                {
                    header = entry.Overlay.Size;
                    headerText = entry.Overlay.TextArea;
                    break;
                }
            }
        }

        // A skin with a clean title band needs no nameplate overlay: the band IS the title area, so the
        // title is placed straight onto it. An overlay only wins when the art has a plate moulded in.
        var slot = _ornaments.IsCompact ? null : ThemeService.AppliedSkin?.Layout?.TitleBar;
        // The band's depth is the theme's where it states one, otherwise whatever the window art leaves.
        // A theme with no art at all still gets a band, which is what a drawn nameplate needs to sit in.
        var band = slot?.Height ?? _windowSkin.Inset.Top;
        // Measured from the chrome grid, whose SizeChanged is what brings this here. The window's own Bounds
        // lag behind it and can still be empty on the passes that decide whether the plate is shown.
        var width = _chrome.Bounds.Width > 0 ? _chrome.Bounds.Width : Bounds.Width;
        if (header is null && slot is not null && band > 0)
        {
            header = new SkinSize(Math.Max(0, width - slot.Padding.Left - slot.Padding.Right), band);
            headerText = null;
        }

        ApplyToolbarSlot(slot);

        var active = header is { Width: > 0, Height: > 0 };
        Wandur.Core.Diagnostics.ThemeTrace.Write("title.chrome",
            $"chrome={_chrome.Bounds.Width:F0}x{_chrome.Bounds.Height:F0} compact={_ornaments.IsCompact} slot={(slot is null ? "null" : $"h={slot.Height} hosts={slot.HostsToolbar}")} band={band} header={(header is null ? "null" : $"{header.Value.Width:F0}x{header.Value.Height:F0}")} active={active} wasActive={_skinTitleActive}");
        if (active != _skinTitleActive || active)
        {
            _skinTitleActive = active;
            if (active && header is { } size)
            {
                if (!ReferenceEquals(_appTitle.Parent, _plaque))
                {
                    _titleBarIdentity.Children.Remove(_appTitle);
                    _plaque.Child = _appTitle;
                    _plaqueTitleHost.Child = _plaque;
                }
                ApplyPlaque(slot?.Plaque);
                _titleBarIdentity.IsVisible = false;
                _metalDrag.IsVisible = true;
                _metalDrag.Height = Math.Max(_windowSkin.Inset.Top, size.Height);
                _plaqueTitleHost.IsVisible = true;
                // The plaque bitmap is mostly metal: only its inner window is dark enough to read a
                // title on. Sizing the host to the whole plate put the title on the shoulders, where it
                // was white on light metal, and clipped it mid-word for want of an ellipsis.
                var window = headerText is { } area
                    ? new Rect(area.X * size.Width, area.Y * size.Height, area.Width * size.Width, area.Height * size.Height)
                    : new Rect(0, 0, size.Width, size.Height);
                // A nameplate is an object on the bar, not the bar itself: it hugs the title, bounded so a
                // long world name cannot push it past the toolbar and a short one still reads as a plate.
                if (slot?.Plaque is not null)
                {
                    var plate = Math.Clamp(window.Height * (slot.Plaque.Wings is null ? 0.62 : 0.86), 24, 72);
                    window = new Rect(window.X, window.Y + (window.Height - plate) / 2, window.Width, plate);
                    _plaqueTitleHost.Width = double.NaN;
                    // Wide enough to read as a mounted plate rather than a label, capped so a long world name
                    // cannot push it into the toolbar.
                    _plaqueTitleHost.MinWidth = Math.Min(Math.Max(320, size.Width * 0.36), size.Width * 0.5);
                    _plaqueTitleHost.MaxWidth = Math.Max(320, size.Width * 0.5);
                }
                else
                {
                    _plaqueTitleHost.MinWidth = 0;
                    _plaqueTitleHost.MaxWidth = double.PositiveInfinity;
                    _plaqueTitleHost.Width = window.Width;
                }
                _plaqueTitleHost.Height = window.Height;
                _plaqueTitleHost.Margin = new Thickness(0, window.Y, 0, 0);
                _plaqueTitleHost.HorizontalAlignment = (slot?.TitleAlign ?? "center") switch
                {
                    "left" => HorizontalAlignment.Left,
                    "right" => HorizontalAlignment.Right,
                    _ => HorizontalAlignment.Center
                };
                _plaqueTitleHost.Padding = slot?.Plaque is null ? new Thickness(8, 0) : default;
                var plated = slot?.Plaque is not null;
                // A nameplate is engraved: capitals, spaced, on whichever text colour reads on its plate.
                _appTitle.Text = PlateTitle();
                _appTitle.FontSize = plated ? 15 : window.Height >= 56 ? 15 : 13;
                _appTitle.LetterSpacing = plated ? 2.2 : 0.6;
                _appTitle.TextTrimming = TextTrimming.CharacterEllipsis;
                _appTitle.TextWrapping = TextWrapping.NoWrap;
                _appTitle.VerticalAlignment = VerticalAlignment.Center;
                _appTitle.HorizontalAlignment = HorizontalAlignment.Center;
                _appTitle.Bind(TextBlock.ForegroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(plated ? "PlaqueTextBrush" : "TerminalTextBrush"));
                if (FleetSkin.IsActive) ApplyFleetTitle(width);
            }
            else
            {
                if (!ReferenceEquals(_appTitle.Parent, _titleBarIdentity))
                {
                    // The title now hangs off the plaque rather than the host, so clearing the host alone
                    // leaves it parented and the move to the identity row throws.
                    _plaque.Child = null;
                    _plaqueTitleHost.Child = null;
                    if (!_titleBarIdentity.Children.Contains(_appTitle))
                        _titleBarIdentity.Children.Add(_appTitle);
                }
                _titleBarIdentity.IsVisible = true;
                _metalDrag.IsVisible = false;
                _plaqueTitleHost.IsVisible = false;
                _plaqueTitleHost.Margin = default;
                _appTitle.Text = Title;
                _appTitle.FontSize = 13;
                _appTitle.LetterSpacing = 0;
                _appTitle.TextTrimming = TextTrimming.None;
                _appTitle.VerticalAlignment = VerticalAlignment.Center;
                _appTitle.HorizontalAlignment = HorizontalAlignment.Left;
                _appTitle.Bind(TextBlock.ForegroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("TextBrush"));
            }
        }

        // Resolve the native height once. macOS reports changed decoration margins synchronously,
        // which re-enters this method; competing Fleet (50) and legacy (52) writes recurse forever.
        if (FleetSkin.IsActive)
            ExtendClientAreaTitleBarHeightHint = FleetTitleLayout.BandHeight;
        else if (OperatingSystem.IsMacOS())
        {
            ExtendClientAreaTitleBarHeightHint = _skinTitleActive
                ? Math.Max(MacTitleBarHeight, _windowSkin.BorderBitmap is not null ? _windowSkin.Inset.Top : _windowSkin.BandHeight)
                : MacTitleBarHeight;
        }
        if (OperatingSystem.IsMacOS()) UpdateTitleBarInsets();
    }

    private void ApplyFooterClearance()
    {
        var clearance = _ornaments.EffectiveFooterClearance;
        _footer.Padding = new Thickness(
            _footerBasePadding.Left + clearance.Left,
            _footerBasePadding.Top,
            _footerBasePadding.Right + clearance.Right,
            _footerBasePadding.Bottom);
        _footer.MinHeight = clearance.MinHeight > 0
            ? Math.Max(0, clearance.MinHeight + _footerBasePadding.Top + _footerBasePadding.Bottom)
            : 0;
        _footerHint.IsVisible = clearance.Left == 0 && clearance.Right == 0;
    }

    private void UpdateTitleBarInsets()
    {
        if (FleetSkin.IsActive)
        {
            _toolbar.Padding = new Thickness(12, 13, 12, 5);
            _toolbar.MinHeight = 54;
            _windowHeader.MinHeight = 0;
            _headerStack.Margin = _toolbar.IsVisible ? default : new Thickness(0, 14, 0, 0);
            return;
        }
        if (!OperatingSystem.IsMacOS())
        {
            _toolbar.Padding = new Thickness(12, 5);
            _toolbar.MinHeight = ToolbarHeight;
            _windowHeader.MinHeight = 0;
            return;
        }
        // Native traffic lights occupy the left of the extended titlebar. With a skin plaque they sit in
        // the metal header above the white toolbar, so the toolbar only needs ordinary side padding.
        var left = WindowState == WindowState.FullScreen ? 12 : _skinTitleActive ? 12 : 88;
        var vertical = _skinTitleActive ? 5 : 7;
        _toolbar.Padding = new Thickness(left, vertical, 12, vertical);
        _toolbar.MinHeight = _skinTitleActive ? ToolbarHeight : MacTitleBarHeight;
        _windowHeader.MinHeight = _skinTitleActive ? 0 : MacTitleBarHeight;
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Fleet uses Avalonia's non-client hit testing, including native double-click behavior.
        if (FleetSkin.IsActive) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || e.Source is not Visual source) return;
        if (source.GetSelfAndVisualAncestors().Any(v => v is Button or ComboBox or TextBox or MenuItem)) return;
        if (e.ClickCount == 2 && WindowState != WindowState.FullScreen)
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else if (e.ClickCount == 1) BeginMoveDrag(e);
        e.Handled = true;
    }

    private void ApplyFleetTitle(double width)
    {
        Classes.Set("fleet-compact", width < 1100);
        // Run on every platform, not just the macOS native-decoration callback.
        UpdateTitleBarInsets();
        _appTitle.FontSize = 20;
        _appTitle.FontWeight = FontWeight.Normal;
        _appTitle.LetterSpacing = 1.8;
        _plaque.Fill = FleetSkin.Plaque;
        _plaque.WingFill = ThemeService.AppliedWorldTheme?.Skin?.Layout?.TitleBar?.Plaque?.Wings?.Fill is { } wingColor
            ? FleetSkin.Shade(Color.Parse(wingColor)) : FleetSkin.Wings;
        var measure = new TextBlock { Text = _appTitle.Text, FontFamily = _appTitle.FontFamily,
            FontSize = _appTitle.FontSize, LetterSpacing = _appTitle.LetterSpacing, FontWeight = _appTitle.FontWeight };
        measure.Measure(new Size(double.PositiveInfinity, FleetTitleLayout.PlaqueHeight));
        // OS traffic lights are native, not Avalonia children. Reserve their platform-safe span;
        // decoration margins can enlarge it (for example when the native frame changes).
        var left = Math.Max(WindowDecorationMargin.Left, OperatingSystem.IsMacOS() && WindowState != WindowState.FullScreen ? 88 : 0);
        var right = Math.Max(WindowDecorationMargin.Right, OperatingSystem.IsWindows() ? 144 : 0);
        var place = FleetTitleLayout.Calculate(width, left, right, measure.DesiredSize.Width);
        _windowSkin.TitleModuleBounds = place.Bounds;
        ApplyFleetToolbarSurface();
        _plaqueTitleHost.HorizontalAlignment = HorizontalAlignment.Left;
        _plaqueTitleHost.MinWidth = 0;
        _plaqueTitleHost.MaxWidth = double.PositiveInfinity;
        _plaqueTitleHost.Width = place.Bounds.Width;
        _plaqueTitleHost.Height = place.Bounds.Height;
        _plaqueTitleHost.Margin = new Thickness(place.Bounds.X, place.Bounds.Y, 0, 0);
        _plaque.Padding = new Thickness(place.PlainTitle ? 4 : FleetTitleLayout.TextInset, 0);
        if (place.PlainTitle)
        {
            _plaque.Fill = null;
            _appTitle.Bind(TextBlock.ForegroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("TextBrush"));
        }
        ToolTip.SetTip(_appTitle, _plaqueLabel);
        Avalonia.Automation.AutomationProperties.SetName(_appTitle, _plaqueLabel);
        ExtendClientAreaToDecorationsHint = true;
        WindowDecorationProperties.SetElementRole(_metalDrag, WindowDecorationsElementRole.TitleBar);
        WindowDecorationProperties.SetElementRole(_plaqueTitleHost, WindowDecorationsElementRole.TitleBar);
        _plaque.IsHitTestVisible = false;
        _footer.MinHeight = 30;
    }

    private void ApplyFleetToolbarSurface()
    {
        if (!FleetSkin.IsActive || _chrome is null || _windowSkin is null ||
            _toolbar.TranslatePoint(default, _chrome) is not { } origin) return;
        var title = _windowSkin.TitleModuleBounds.Translate(new Vector(-origin.X, -origin.Y));
        var key = (_toolbar.Bounds.Size, title);
        if (_fleetToolbarSurfaceKey == key) return;
        _toolbarBackground?.Dispose();
        _toolbarBackground = null;
        _toolbar.Background = FleetToolbarSurface.Create(key.Size, title);
        _fleetToolbarSurfaceKey = key;
    }

    // Toolbar brand mark when no skin plaque owns the title.
    private static Image AppLogo()
    {
        using var stream = AssetLoader.Open(new Uri("avares://Wandur/Assets/icon-256.png"));
        var logo = new Image
        {
            Name = "AppLogo",
            Source = Bitmap.DecodeToHeight(stream, 64),
            Width = 26, Height = 26,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 9, 0),
        };
        RenderOptions.SetBitmapInterpolationMode(logo, BitmapInterpolationMode.HighQuality);
        ToolTip.SetTip(logo, "Wandur");
        return logo;
    }

    private static Button ToolbarButton(string geometry, string name, string tip, bool filled = false)
    {
        var button = new Button { Name = name, HorizontalContentAlignment = HorizontalAlignment.Center };
        button.Classes.Add("command-bar-button");
        button.Content = Ui.ChromeGlyph(geometry, filled);
        button.Bind(ToolTip.TipProperty, LocalizedText.Binding(tip));
        button.Bind(Avalonia.Automation.AutomationProperties.NameProperty, LocalizedText.Binding(tip));
        return button;
    }

    public void ResetLayout()
    {
        Workspace.CloseEditorDocuments();
        Workspace.CloseScriptPanels();
        Workspace.DetachNavigation();
        _dock.Layout?.Close.Execute(null);
        Workspace = new(Sessions, async () => await EditWorldAsync(), async () => await BrowseWorldsAsync(), async profile => await EditWorldAsync(profile), catalog: Catalog, editAutomation: async (controller, section) => await EditSessionAutomationAsync(controller, section));
        var layout = Workspace.CreateLayout();
        Workspace.InitLayout(layout);
        _dock.Factory = Workspace; _dock.Layout = layout;
        _menus.Refresh();
    }

    public bool IsPanelVisible()
        => Workspace.WorldsTool is { Owner: Dock.Model.Core.IDock owner } tool && owner.VisibleDockables?.Contains(tool) == true;

    public void TogglePanel()
    {
        if (Workspace.WorldsTool is not { } tool) return;
        if (IsPanelVisible()) Workspace.HideDockable(tool);
        else Workspace.RestoreDockable(tool);
        _menus.Refresh();
    }

    public bool IsMapVisible => Workspace.MapTool is { Owner: Dock.Model.Core.IDock owner } tool && owner.VisibleDockables?.Contains(tool) == true;
    public void ToggleMap()
    {
        if (Workspace.MapTool is not { } tool) return;
        if (IsMapVisible) Workspace.HideDockable(tool);
        else Workspace.RestoreDockable(tool);
        _menus.Refresh();
    }

    public bool IsChannelsVisible => Workspace.ChannelsTool is { Owner: Dock.Model.Core.IDock owner } tool && owner.VisibleDockables?.Contains(tool) == true;
    public void ToggleChannels() { SetChannelsVisible(!IsChannelsVisible); _menus.Refresh(); }

    private void SetChannelsVisible(bool visible)
    {
        if (Workspace.ChannelsTool is not { } tool || IsChannelsVisible == visible) return;
        if (visible) Workspace.RestoreDockable(tool); else Workspace.HideDockable(tool);
    }

    public void FocusCommandInput() => this.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Name == "CommandInput" && t.IsEffectivelyVisible)?.Focus();
    public Task ConnectSelectedAsync() => SelectedProfile is { } profile ? Sessions.OpenAsync(profile) : Task.CompletedTask;
    internal Task EditWorldAsync(ConnectionProfile? profile = null) => ShowDialogAsync(() => new ProfileDialog(new ProfileEditorViewModel(Controller, Catalog, profile, _profileAutomationFactory, _agents)));
    internal Task BrowseWorldsAsync() { Workspace.ShowSearch(); return Task.CompletedTask; }
    internal Task PreferencesAsync() => ShowDialogAsync(() =>
    {
        var dialog = new OptionsDialog(new PreferencesViewModel(Controller, Sessions.PreviewAppearanceSettings));
        dialog.Closed += (_, _) => Sessions.EndAppearanceSettingsPreview();
        return dialog;
    });
    internal Task ShowScriptsAsync() => EditSessionAutomationAsync(Controller, 2);
    internal Task EditSessionAutomationAsync(WorkspaceController controller, int section)
    {
        var tab = Sessions.Tabs.FirstOrDefault(t => ReferenceEquals(t.Controller, controller));
        if (tab?.Profile is not { } original) return Task.CompletedTask;
        var profile = controller.Profiles.FirstOrDefault(p => p.Id == original.Id)
            ?? controller.Profiles.FirstOrDefault(p => p.Host.Equals(original.Host, StringComparison.OrdinalIgnoreCase) && p.Port == original.Port && p.UseTls == original.UseTls)
            ?? original;
        return ShowDialogAsync(() => new ProfileDialog(new ProfileEditorViewModel(controller, Catalog, profile, _profileAutomationFactory, _agents) { SectionIndex = section }));
    }

    private async Task ShowDialogAsync(Func<Window> create)
    {
        if (_dialog is not null) { _dialog.Activate(); return; }
        _dialog = create();
        try { await _dialog.ShowDialog(this); }
        finally { _dialog = null; }
    }
    internal Task ShowInformationAsync(string title, string heading, string message) => ShowDialogAsync(() =>
    {
        var dialog = new Window { Title = title, Width = 480, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var done = Ui.ButtonKey(nameof(L.Done), dialog.Close, "primary");
        done.HorizontalAlignment = HorizontalAlignment.Right;
        dialog.Content = new Border { Padding = new Thickness(28), Child = Ui.Stack(Ui.Text(heading, 24), Ui.Text(message, 13, "muted"), done) };
        return dialog;
    });

    private void RefreshLanguage()
    {
        if (Workspace.WorldsTool is { } worlds) worlds.Title = L.Workspace;
        if (Workspace.MapTool is { } map) map.Title = L.Map;
        if (Workspace.ChannelsTool is { } channels) channels.Title = L.Channels;
        Refresh();
    }

    private void Refresh()
    {
        SessionOpenTrace.Count("window refresh");
        // The preference owns the panel until the reader overrides it from the View menu, which does not save.
        if (_channelsPreference != Controller.Settings.ShowChannelsPanel)
        { _channelsPreference = Controller.Settings.ShowChannelsPanel; SetChannelsVisible(_channelsPreference.Value); }
        Workspace.PruneEditorDocuments();
        if (!ReferenceEquals(_profiles, Controller.Settings.Profiles))
        {
            var selected = SelectedProfile?.Id;
            _profiles = Controller.Settings.Profiles;
            _worldPicker.ItemsSource = _profiles;
            _worldPicker.SelectedItem = _profiles.FirstOrDefault(p => p.Id == selected) ?? _profiles.FirstOrDefault();
        }
        // When the active session's saved profile changes (open / tab switch), point the picker at that
        // world so the toolbar matches the session. Manual picks still stick until the next session change.
        var sessionProfileId = Sessions.Active.Profile?.Id;
        if (sessionProfileId != _pickerSessionProfileId)
        {
            _pickerSessionProfileId = sessionProfileId;
            if (_profiles is not null && sessionProfileId is not null &&
                _profiles.FirstOrDefault(p => p.Id == sessionProfileId) is { } match)
                _worldPicker.SelectedItem = match;
        }
        // Character first: it is what tells two sessions on one world apart at a glance; then the world, then the app.
        Title = !Controller.HasSession ? "Wandur" : Controller.CharacterName.Length == 0 ? $"{Controller.WorldName} · Wandur" : $"{Controller.CharacterName} · {Controller.WorldName} · Wandur";
        // The plaque's readable window is a couple of inches of dark metal, so it carries the world's
        // name alone. The full "character · world · Wandur" stays on the OS title, where there is room.
        var oldPlaqueLabel = _plaqueLabel;
        _plaqueLabel = !Controller.HasSession || Controller.WorldName.Length == 0 ? "Wandur" : Controller.WorldName;
        _appTitle.Text = _skinTitleActive ? PlateTitle() : Title;
        if (FleetSkin.IsActive && oldPlaqueLabel != _plaqueLabel) ApplyTitleChrome();
        var connected = Sessions.Tabs.Count(t => t.Controller.IsConnected);
        var count = Sessions.Tabs.Count(t => t.Controller.HasSession);
        _status.Text = L.Format(count == 1 ? L.SessionsOne : L.SessionsMany, count, connected);
        // The session bar: world, character and connection state of the active session, in one muted line.
        _toolbarStatus.Text = (Controller.IsConnected ? "●  " : "○  ") + (Controller.HasSession ? $"{Controller.SessionLabel}  ·  " : "") + Controller.Status;
        var disconnectTip = Controller.IsConnecting ? L.Cancel : L.DisconnectTheActiveSession;
        ToolTip.SetTip(_disconnect, disconnectTip);
        Avalonia.Automation.AutomationProperties.SetName(_disconnect, disconnectTip);
        _notice.IsVisible = Controller.Notice is not null;
        _noticeText.Text = Controller.Notice;
        _menus.Refresh();
    }

    internal async Task ExportAsync()
    {
        // Capture the selected transcript before opening a dialog that can outlive a tab switch.
        var controller = Controller;
        controller.FlushOutput();
        var transcript = controller.Display.PlainText;
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = L.SaveSessionTranscript, SuggestedFileName = $"wandur-{DateTime.Now:yyyy-MM-dd-HHmm}.txt", DefaultExtension = "txt", FileTypeChoices = [new FilePickerFileType(L.TextFile) { Patterns = ["*.txt"] }] });
            if (file is null) return;
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(transcript);
        }
        catch (Exception ex) { controller.ShowNotice(L.Format(L.CouldNotSaveTranscript, ex.Message)); }
    }

    private async Task RefreshCatalogAsync()
    {
        if (_closing || Catalog.Loading) return;
        try { await Catalog.LoadAsync(); }
        catch (OperationCanceledException) { }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        if (_closed) return;
        args.Cancel = true;
        if (_closing) return;
        _closing = true;
        _catalogRefreshTimer.Stop();
        try
        {
            Workspace.CloseEditorDocuments();
            Workspace.CloseScriptPanels();
            Workspace.DetachNavigation();
            await Sessions.DisposeAsync();
            _dock.Layout?.Close.Execute(null);
        }
        finally { Catalog.Dispose(); Sessions.Changed -= Refresh; _closed = true; Close(); }
    }
}
