using Wandur.Desktop.Services;
using Wandur.Core.Storage;
using Wandur.Core.Scripting;
using Wandur.Core.Mapping;
using L = Wandur.Core.Localization.Strings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
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
    private const double MacTitleBarHeight = 44;
    private readonly Button _disconnect;
    private readonly DesktopMenus _menus;
    private readonly ComboBox _worldPicker = new() { Name = "ToolbarWorlds", Width = 220, MinHeight = 28, Height = 28, FontSize = 12, Padding = new Thickness(9, 3), [!ComboBox.PlaceholderTextProperty] = LocalizedText.Binding(nameof(L.ChooseAWorld)) };
    private List<ConnectionProfile>? _profiles;
    private bool _closing;
    private bool _closed;
    private bool _refreshingProfiles;
    private Window? _dialog;
    private readonly IProfileAutomationFactory _profileAutomationFactory;
    private readonly IAgentClientServices? _agents;
    public ConnectionProfile? SelectedProfile => _worldPicker.SelectedItem as ConnectionProfile;
    public bool ToolbarVisible { get => _toolbar?.IsVisible ?? true; set { _toolbar.IsVisible = value; _menus.Refresh(); } }

    public MainWindow(Wandur.Desktop.Terminal.ITranscriptDisplayFactory displays, ISettingsStore store, IPasswordVault passwords, IRoomMapStore maps, IScriptRuntimeFactory scriptRuntimes, IWorldScriptLibraryStore scriptLibraryStore, IWorldKnowledgeStore? knowledge = null, WorldCatalog? catalog = null, IProfileAutomationFactory? profileAutomationFactory = null, IAgentClientServices? agents = null, Wandur.Core.Classification.RoomClassificationService? classification = null)
    {
        _agents = agents;
        _profileAutomationFactory = profileAutomationFactory ?? new ProfileAutomationFactory(scriptRuntimes, scriptLibraryStore);
        Catalog = catalog ?? new WorldCatalog(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(store.FilePath))!, "directory.json"));
        Sessions = new(displays, store, passwords, maps, scriptRuntimes, scriptLibraryStore, knowledge, Catalog, agents, classification);
        Title = "Wandur";
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
        _worldPicker.ItemTemplate = new FuncDataTemplate<ConnectionProfile>((profile, _) => new TextBlock { Text = profile?.Name, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12 });
        _worldPicker.SelectionChanged += (_, _) =>
        {
            _menus.Refresh();
            if (!_refreshingProfiles) Sessions.PreviewWorldProfile(_worldPicker, SelectedProfile);
        };
        ToolTip.SetTip(_worldPicker, L.ChooseASavedWorldToOpenInASession);
        Avalonia.Automation.AutomationProperties.SetName(_worldPicker, L.ChooseAWorld);
        _worldPicker.Classes.Add("toolbar-world-picker");
        var connect = ToolbarButton("M 4,2 L 14,8 L 4,14 Z", "Connect", nameof(L.ConnectToTheSelectedWorldInASessionTab), filled: true);
        connect.Command = _menus.Connect;
        _disconnect = ToolbarButton("M 3,3 H 13 V 13 H 3 Z", "Disconnect", nameof(L.DisconnectTheActiveSession), filled: true);
        _disconnect.Command = _menus.Disconnect;
        var browse = ToolbarButton("M 6.5,1 A 5.5,5.5 0 1 0 6.5,12 A 5.5,5.5 0 1 0 6.5,1 M 10.5,10.5 L 15,15", "FindMud", nameof(L.SearchTheDirectoryForAMUDServer));
        browse.Click += async (_, _) => await BrowseWorldsAsync();
        var divider = new Border { Width = 1, Height = 16, Margin = new Thickness(7, 0) };
        divider.Bind(Border.BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("LineBrush"));
        var connectionControls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(5, 0, 0, 0), Children = { connect, _disconnect } };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Center, Children = { _worldPicker, connectionControls, divider, browse } };
        _toolbarStatus.VerticalAlignment = VerticalAlignment.Center;
        _toolbarStatus.HorizontalAlignment = HorizontalAlignment.Right;
        _toolbarStatus.TextTrimming = TextTrimming.CharacterEllipsis;
        _toolbarStatus.TextWrapping = TextWrapping.NoWrap;
        Grid.SetColumn(_toolbarStatus, 1);
        _toolbar = new Border { Name = "MainToolbar", Padding = new Thickness(12, 5), BorderThickness = new Thickness(0, 0, 0, 1), Child = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 20, Children = { actions, _toolbarStatus } } };
        _toolbar.Bind(Border.BorderBrushProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("LineBrush"));
        _toolbar.Bind(Border.BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("ChromeBrush"));
        _menus.Fallback.IsVisible = !OperatingSystem.IsMacOS();
        // Keep a draggable native titlebar even when View > Toolbar hides its controls.
        var header = new Border
        {
            Name = "WindowHeader", MinHeight = OperatingSystem.IsMacOS() ? MacTitleBarHeight : 0,
            Child = new StackPanel { Children = { _menus.Fallback, _toolbar } }
        };
        header.Bind(Border.BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("ChromeBrush"));
        if (OperatingSystem.IsMacOS())
        {
            UpdateTitleBarInsets();
            PropertyChanged += (_, e) => { if (e.Property == WindowStateProperty) UpdateTitleBarInsets(); };
            header.PointerPressed += OnTitleBarPointerPressed;
        }

        var dismiss = Ui.Button("×", () => Controller.ShowNotice(null), "quiet");
        Grid.SetColumn(dismiss, 1);
        _notice = new Border { Padding = new Thickness(18, 7), Background = Brush.Parse("#483B2A"), IsVisible = false, Child = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { _noticeText, dismiss } } };
        _noticeText.Foreground = Brush.Parse("#FFE0B0");
        _noticeText.VerticalAlignment = VerticalAlignment.Center;
        var statusRight = Ui.TextKey(nameof(L.CtrlTabSwitchSessionsDragPanelHeadersToArrange), 10, "muted");
        statusRight.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(statusRight, 1);
        var footer = new Border { Padding = new Thickness(12, 3), BorderThickness = new Thickness(0, 1, 0, 0), Child = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { _status, statusRight } } };
        footer.Bind(Border.BorderBrushProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("LineBrush"));
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        root.Children.Add(header); Grid.SetRow(_notice, 1); root.Children.Add(_notice); Grid.SetRow(_dock, 2); root.Children.Add(_dock); Grid.SetRow(footer, 3); root.Children.Add(footer);
        Content = root;
        Sessions.Changed += Refresh;
        Wandur.Core.Localization.UiLanguage.Changed += RefreshLanguage;
        Closed += (_, _) => Wandur.Core.Localization.UiLanguage.Changed -= RefreshLanguage;
        Closing += OnClosing;
        _catalogRefreshTimer = new Avalonia.Threading.DispatcherTimer(WorldCatalog.RefreshInterval,
            Avalonia.Threading.DispatcherPriority.Background, async (_, _) => await RefreshCatalogAsync());
        Opened += async (_, _) => { _catalogRefreshTimer.Start(); await RefreshCatalogAsync(); };
        Closed += (_, _) => _catalogRefreshTimer.Stop();
        Refresh();
    }

    private void UpdateTitleBarInsets()
    {
        // Native traffic lights occupy the left side of the extended titlebar.
        _toolbar.Padding = new Thickness(WindowState == WindowState.FullScreen ? 12 : 88, 7, 12, 7);
        _toolbar.MinHeight = MacTitleBarHeight;
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || e.Source is not Visual source) return;
        if (source.GetSelfAndVisualAncestors().Any(v => v is Button or ComboBox or TextBox)) return;
        if (e.ClickCount == 2 && WindowState != WindowState.FullScreen)
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else if (e.ClickCount == 1) BeginMoveDrag(e);
        e.Handled = true;
    }

    private static Button ToolbarButton(string geometry, string name, string tip, bool filled = false)
    {
        var button = new Button { Name = name, HorizontalContentAlignment = HorizontalAlignment.Center };
        button.Classes.Add("command-bar-button");
        var icon = new Avalonia.Controls.Shapes.Path
        {
            Data = StreamGeometry.Parse(geometry), Width = 15, Height = 15,
            Stretch = Stretch.Uniform, StrokeThickness = filled ? 0 : 1.6,
            StrokeLineCap = PenLineCap.Round, IsHitTestVisible = false
        };
        icon.Bind(filled ? Avalonia.Controls.Shapes.Shape.FillProperty : Avalonia.Controls.Shapes.Shape.StrokeProperty,
            new Avalonia.Data.Binding(nameof(Foreground)) { Source = button });
        button.Content = icon;
        button.Bind(ToolTip.TipProperty, LocalizedText.Binding(tip));
        button.Bind(Avalonia.Automation.AutomationProperties.NameProperty, LocalizedText.Binding(tip));
        return button;
    }

    public void ResetLayout()
    {
        Workspace.CloseEditorDocuments();
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
        Refresh();
    }

    private void Refresh()
    {
        Workspace.PruneEditorDocuments();
        if (!ReferenceEquals(_profiles, Controller.Settings.Profiles))
        {
            var selected = SelectedProfile?.Id;
            _refreshingProfiles = true;
            _profiles = Controller.Settings.Profiles;
            _worldPicker.ItemsSource = _profiles;
            _worldPicker.SelectedItem = _profiles.FirstOrDefault(p => p.Id == selected) ?? _profiles.FirstOrDefault();
            _refreshingProfiles = false;
            Sessions.PreviewWorldProfile(_worldPicker, SelectedProfile, activate: false);
        }
        Title = Controller.HasSession ? $"{Controller.WorldName} — Wandur" : "Wandur";
        var connected = Sessions.Tabs.Count(t => t.Controller.IsConnected);
        var count = Sessions.Tabs.Count(t => t.Controller.HasSession);
        _status.Text = L.Format(count == 1 ? L.SessionsOne : L.SessionsMany, count, connected);
        _toolbarStatus.Text = (Controller.IsConnected ? "●  " : "○  ") + Controller.Status;
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
            Workspace.DetachNavigation();
            await Sessions.DisposeAsync();
            _dock.Layout?.Close.Execute(null);
        }
        finally { Catalog.Dispose(); Sessions.Changed -= Refresh; _closed = true; Close(); }
    }
}
