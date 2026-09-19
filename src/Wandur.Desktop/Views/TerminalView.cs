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
    private readonly Button _latest;
    private readonly Border _welcome;
    private readonly ResourceBarsView _resources = new();
    private readonly TextBlock _footerHint = Ui.TextKey(nameof(L.CommandHistoryEnterSend), 11, "muted");
    private readonly CheckBox _privateToggle = new() { Name = "PrivateInputToggle", [!ContentControl.ContentProperty] = LocalizedText.Binding(nameof(L.PrivateInput2)), FontSize = 11, MinHeight = 0, Height = 22, Padding = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
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
        var entry = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
        entry.Children.Add(_input); Grid.SetColumn(_send, 1); entry.Children.Add(_send);
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
        _footerHint.VerticalAlignment = VerticalAlignment.Center;
        _footerHint.TextWrapping = TextWrapping.NoWrap;
        _footerHint.TextTrimming = TextTrimming.CharacterEllipsis;
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

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); _controller.Changed += Refresh; _controller.Display.ViewportChanged += RefreshScroll; Refresh(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { _controller.Changed -= Refresh; _controller.Display.ViewportChanged -= RefreshScroll; base.OnDetachedFromVisualTree(e); }


    private void Refresh()
    {
        // A sensitive draft must disappear before the password mask can be removed.
        if (_wasPrivate && !_controller.IsPrivate) _input.Text = "";
        _wasPrivate = _controller.IsPrivate;
        _send.IsEnabled = _controller.IsConnected && !_sending;
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
