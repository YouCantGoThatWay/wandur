using L = Wandur.Core.Localization.Strings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Wandur.Desktop.Views;

public sealed class TerminalView : UserControl
{
    private readonly WorkspaceController _controller;
    private readonly TextBox _input = new() { Name = "CommandInput", [!TextBox.PlaceholderTextProperty] = LocalizedText.Binding(nameof(L.EnterACommand)), FontFamily = new FontFamily("Menlo, Consolas, DejaVu Sans Mono"), MinHeight = 36, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly Button _send;
    private readonly Button _look;
    private readonly Button _quickCommands;
    private readonly List<Button> _liveButtons = [];
    private readonly Button _latest;
    private readonly Border _welcome;
    private readonly ResourceBarsView _resources = new();
    private readonly TextBlock _footerHint = Ui.TextKey(nameof(L.CommandHistoryEnterSend), 11, "muted");
    private readonly ToggleButton _privateToggle = new() { Name = "PrivateInputToggle", Width = double.NaN, Height = 26, MinHeight = 0, Padding = new Thickness(6, 0), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private bool _syncingPrivate;
    private bool _sending;
    private bool _wasPrivate;

    public TerminalView(WorkspaceController controller, Action<int>? editConfiguration = null)
    {
        _controller = controller;
        _latest = Ui.ButtonKey(nameof(L.LatestOutput), () => controller.Display.FollowTail());
        _latest.HorizontalAlignment = HorizontalAlignment.Right;
        _latest.VerticalAlignment = VerticalAlignment.Bottom;
        _latest.Margin = new Thickness(16);
        _latest.IsVisible = false;
        _send = Ui.ButtonKey(nameof(L.Send), async () => await Send(), "primary");
        _send.Name = "SendCommand";
        _send.Height = 36;
        _send.MinHeight = 0;
        _send.VerticalAlignment = VerticalAlignment.Stretch;
        _look = ComposerButton(new Button { Name = "LookButton" }, EyeGeometry, nameof(L.LookAround));
        _look.Click += async (_, _) => await controller.SendAsync("look");
        _quickCommands = ComposerButton(new Button { Name = "CommandsButton" }, GridGeometry, nameof(L.Controls));
        var commands = new Flyout { Placement = PlacementMode.TopEdgeAlignedLeft };
        commands.Content = QuickCommands(controller, commands);
        _quickCommands.Flyout = commands;
        _input.AddHandler(KeyDownEvent, (_, args) =>
        {
            if (controller.Pages.IsPlay && args.KeyModifiers == KeyModifiers.None && args.Key >= Key.F1 && args.Key <= Key.F12)
                args.Handled = controller.ScriptLibrary.HandleShortcut(args.Key.ToString());
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _input.KeyDown += async (_, args) =>
        {
            if (args.Key == Key.Enter && args.KeyModifiers == KeyModifiers.None) { args.Handled = true; await Send(); }
            else if (args.Key == Key.Up && !controller.IsPrivate) { _input.Text = controller.History.Previous(_input.Text ?? ""); _input.CaretIndex = _input.Text.Length; args.Handled = true; }
            else if (args.Key == Key.Down && !controller.IsPrivate) { _input.Text = controller.History.Next(); _input.CaretIndex = _input.Text.Length; args.Handled = true; }
        };
        _welcome = new Border
        {
            Padding = new Thickness(36), VerticalAlignment = VerticalAlignment.Center, MaxWidth = 520,
            Child = Ui.Stack(Ui.TextKey(nameof(L.ANOPENDOOR), 10, "eyebrow"),
                Ui.TextKey(nameof(L.YourNextWorldAwaits), 38),
                Ui.TextKey(nameof(L.ReturnToAWorldYouLoveOrFollowA), 14, "muted"),
                Ui.ButtonKey(nameof(L.TakeAWalkThroughTheDemo), async () => { await controller.StartAsync(); _input.Focus(); }, "primary"))
        };
        var display = controller.Display.View;
        if (display.Parent is Panel oldParent) oldParent.Children.Remove(display);
        display.Margin = new Thickness(8, 4, 4, 4);
        var output = new Grid { Children = { display, _welcome, _latest } };
        output.Bind(BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("TerminalBrush"));
        var actions = new StackPanel { Name = "ComposerActions", Orientation = Orientation.Horizontal, Spacing = 4, Children = { _look, _quickCommands } };
        var entry = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 10 };
        entry.Children.Add(actions);
        Grid.SetColumn(_input, 1); entry.Children.Add(_input);
        Grid.SetColumn(_send, 2); entry.Children.Add(_send);
        var composer = new Border { Name = "Composer", Padding = new Thickness(4, 4), BorderThickness = new Thickness(0, 1, 0, 0), Child = entry };
        composer.Bind(Border.BorderBrushProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("LineBrush"));
        var diagnostics = new ProtocolDiagnosticsView(controller.Diagnostics) { Name = "ProtocolDiagnostics" };
        diagnostics.Bind(IsVisibleProperty, new Binding(nameof(controller.Pages.IsDiagnostics)) { Source = controller.Pages });
        output.Bind(IsVisibleProperty, new Binding(nameof(controller.Pages.IsPlay)) { Source = controller.Pages });
        var terminalPane = new Grid { RowDefinitions = new RowDefinitions("*,Auto,Auto"), Children = { output, _resources, composer } };
        Grid.SetRow(_resources, 1);
        Grid.SetRow(composer, 2);
        terminalPane.Bind(IsVisibleProperty, new Binding(nameof(controller.Pages.IsPlay)) { Source = controller.Pages });
        TabStripItem ViewTab(string key, string name)
        {
            var tab = new TabStripItem { Name = name };
            tab.Classes.Add("output-footer-tab");
            tab.Bind(ContentControl.ContentProperty, LocalizedText.Binding(key));
            return tab;
        }
        var tabs = new TabStrip { Name = "OutputViewTabs", HorizontalAlignment = HorizontalAlignment.Left, Items =
        {
            ViewTab(nameof(L.SessionPlay), "TerminalViewTab"),
            ViewTab(nameof(L.DiagnosticsTab), "DiagnosticsViewTab")
        } };
        tabs.Bind(SelectingItemsControl.SelectedIndexProperty, new Binding(nameof(controller.Pages.SelectedIndex))
        { Source = controller.Pages, Mode = BindingMode.TwoWay });
        var tabBar = new Border { Name = "OutputFooter", Height = 28, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(12, 0) };
        tabBar.Bind(Border.BorderBrushProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("LineBrush"));
        tabBar.Bind(Border.BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("ShellBrush"));
        var automation = new SessionAutomationToolbar(controller.Pages.Automation, editConfiguration, controller.Agent) { Margin = new Thickness(12, 0, 0, 0) };
        _footerHint.Name = "CommandHint";
        _footerHint.VerticalAlignment = VerticalAlignment.Center;
        _footerHint.TextWrapping = TextWrapping.NoWrap;
        _footerHint.TextTrimming = TextTrimming.CharacterEllipsis;
        _footerHint.MaxWidth = 260;
        // The agent status keeps the room it needs: the hint is the first thing to go on a narrow bar.
        tabBar.SizeChanged += (_, args) => _footerHint.IsVisible = args.NewSize.Width >= 640;
        _privateToggle.Classes.Add("command-bar-button");
        var privateLabel = Ui.TextKey(nameof(L.PrivateInput2), 11);
        privateLabel.VerticalAlignment = VerticalAlignment.Center;
        _privateToggle.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center, Children = { Ui.Glyph(LockGeometry, _privateToggle), privateLabel } };
        _privateToggle.Bind(Avalonia.Automation.AutomationProperties.NameProperty, LocalizedText.Binding(nameof(L.PrivateInput2)));
        _privateToggle.Bind(ToolTip.TipProperty, LocalizedText.Binding(nameof(L.MasksYourInputAndKeepsItOutOfCommand)));
        _privateToggle.IsCheckedChanged += (_, _) => { if (!_syncingPrivate) controller.SetManualPrivate(_privateToggle.IsChecked == true); };
        var privateGroup = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Children = { _footerHint, _privateToggle } };
        var footerContent = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Children = { tabs, automation, privateGroup } };
        Grid.SetColumn(automation, 1); Grid.SetColumn(privateGroup, 2); tabBar.Child = null; tabBar.Child = footerContent;
        var outputViews = new Grid { Children = { terminalPane, diagnostics } };
        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Children = { outputViews, tabBar } };
        Grid.SetRow(tabBar, 1);
        root.Bind(BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("TerminalBrush"));
        Content = root;
        Refresh();
    }

    private const string EyeGeometry = "M 1,8 C 3.4,4.2 5.7,2.6 8,2.6 C 10.3,2.6 12.6,4.2 15,8 C 12.6,11.8 10.3,13.4 8,13.4 C 5.7,13.4 3.4,11.8 1,8 Z M 8,5.9 A 2.1,2.1 0 1 0 8,10.1 A 2.1,2.1 0 1 0 8,5.9 Z";
    private const string LockGeometry = "M 8,2 A 3,3 0 0 0 5,5 V 7 H 4 V 14 H 12 V 7 H 11 V 5 A 3,3 0 0 0 8,2 Z M 8,3.4 A 1.6,1.6 0 0 1 9.6,5 V 7 H 6.4 V 5 A 1.6,1.6 0 0 1 8,3.4 Z";
    private const string GridGeometry = "M 2,2 H 6.5 V 6.5 H 2 Z M 9.5,2 H 14 V 6.5 H 9.5 Z M 2,9.5 H 6.5 V 14 H 2 Z M 9.5,9.5 H 14 V 14 H 9.5 Z";

    private Button ComposerButton(Button button, string geometry, string key)
    {
        Ui.ToolbarIconKey(button, geometry, key);
        button.Width = 36; button.Height = 36; button.MinHeight = 0; button.Padding = new Thickness(0);
        _liveButtons.Add(button);
        return button;
    }

    /// <summary>The former Controls panel, reduced to a flyout beside the command box.</summary>
    private Control QuickCommands(WorkspaceController controller, Flyout flyout)
    {
        Button Sends(Button button, string command)
        {
            // Closing first keeps the flyout from lingering over the transcript the command changes.
            button.Click += async (_, _) => { flyout.Hide(); await controller.SendAsync(command); };
            ToolTip.SetTip(button, "Send: " + command);
            _liveButtons.Add(button);
            return button;
        }
        Button Labelled(string key, string command, string name)
        {
            var button = new Button { Name = name, Height = 30, MinHeight = 0, Padding = new Thickness(10, 0), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
            button.Classes.Add("app-button");
            button.Bind(ContentControl.ContentProperty, LocalizedText.Binding(key));
            button.Bind(Avalonia.Automation.AutomationProperties.NameProperty, LocalizedText.Binding(key));
            return Sends(button, command);
        }
        var shortcuts = new StackPanel { Name = "QuickCommands", Spacing = 4, Children =
        {
            Labelled(nameof(L.LookAround), "look", "QuickLook"),
            Labelled(nameof(L.WhoSHere), "who", "QuickWho"),
            Labelled(nameof(L.Inventory), "inventory", "QuickInventory"),
            Labelled(nameof(L.Help), "help", "QuickHelp")
        } };
        var compass = new Grid { Name = "CommandsCompass", RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto"), RowSpacing = 4, ColumnSpacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
        void Direction(string label, string command, string name, int row, int column)
        {
            var button = new Button { Name = name, Content = label, Width = 28, Height = 28, MinHeight = 0, Padding = new Thickness(0), FontSize = 12, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
            button.Classes.Add("app-button");
            Avalonia.Automation.AutomationProperties.SetName(button, command);
            Grid.SetRow(button, row); Grid.SetColumn(button, column);
            compass.Children.Add(Sends(button, command));
        }
        Direction("N", "north", "CompassNorth", 0, 1);
        Direction("W", "west", "CompassWest", 1, 0);
        Direction("\u25c8", "look", "CompassLook", 1, 1);
        Direction("E", "east", "CompassEast", 1, 2);
        Direction("S", "south", "CompassSouth", 2, 1);
        var vertical = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 4 };
        var up = Labelled(nameof(L.Up), "up", "CompassUp");
        var down = Labelled(nameof(L.Down), "down", "CompassDown");
        up.HorizontalContentAlignment = down.HorizontalContentAlignment = HorizontalAlignment.Center;
        vertical.Children.Add(up); Grid.SetColumn(down, 1); vertical.Children.Add(down);
        return new StackPanel { Name = "CommandsFlyout", Width = 176, Spacing = 8, Children =
        {
            Ui.TextKey(nameof(L.QUICKCOMMANDS), 10, "eyebrow"), shortcuts,
            Ui.TextKey(nameof(L.NAVIGATION), 10, "eyebrow"), compass, vertical,
            Ui.TextKey(nameof(L.CommandsDependOnYourWorld), 10, "muted")
        } };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); _controller.Changed += Refresh; _controller.Display.ViewportChanged += RefreshScroll; Refresh(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { _controller.Changed -= Refresh; _controller.Display.ViewportChanged -= RefreshScroll; base.OnDetachedFromVisualTree(e); }


    private void Refresh()
    {
        SessionOpenTrace.Count("terminal view refresh");
        // A sensitive draft must disappear before the password mask can be removed.
        if (_wasPrivate && !_controller.IsPrivate) _input.Text = "";
        _wasPrivate = _controller.IsPrivate;
        _send.IsEnabled = _controller.IsConnected && !_sending;
        var live = _controller.IsConnected && !_controller.IsPrivate;
        foreach (var button in _liveButtons) button.IsEnabled = live;
        _input.IsEnabled = _controller.IsConnected;
        _input.PasswordChar = _controller.IsPrivate ? '●' : '\0';
        _welcome.IsVisible = _controller.Terminal.PlainText.Length == 0 && !_controller.IsConnected && !_controller.IsConnecting;
        _resources.Update(_controller.GameState, _controller.IsConnected);
        _footerHint.Text = _controller.IsPrivate ? L.PrivateHiddenFromEchoAndHistory : L.CommandHistoryEnterSend;
        _syncingPrivate = true;
        _privateToggle.IsChecked = _controller.ManualPrivate;
        _syncingPrivate = false;
        RefreshScroll();
    }
    private void RefreshScroll() => _latest.IsVisible = !_controller.Display.IsFollowingTail;

    private async Task Send()
    {
        if (_sending || !_controller.IsConnected) return;
        var command = _input.Text ?? "";
        _sending = true; _send.IsEnabled = false;
        try { if (await _controller.SendAsync(command)) _input.Text = ""; }
        finally { _sending = false; Refresh(); _input.Focus(); }
    }
}
