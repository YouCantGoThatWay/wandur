using L = Wandur.Core.Localization.Strings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Wandur.Desktop.Views;

public sealed class CommandPanelView : UserControl
{
    private readonly WorkspaceController _controller;
    private readonly List<Button> _commands = [];
    private readonly TextBlock _status = Ui.TextKey(nameof(L.NotConnected), 12);
    private readonly TextBlock _count = Ui.TextKey(nameof(L.Label0CommandsSent), 11, "muted");

    public CommandPanelView(WorkspaceController controller)
    {
        _controller = controller;
        var compass = new Grid { RowDefinitions = new RowDefinitions("*,*,*"), ColumnDefinitions = new ColumnDefinitions("*,*,*"), RowSpacing = 6, ColumnSpacing = 6, Height = 144, Width = 144, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6) };
        void Direction(string title, string command, int row, int column)
        {
            var button = Command(title, command, literal: true);
            Grid.SetRow(button, row); Grid.SetColumn(button, column);
            button.Padding = new Thickness(0);
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.VerticalAlignment = VerticalAlignment.Stretch;
            ToolTip.SetTip(button, "Send: " + command);
            compass.Children.Add(button);
        }
        Direction("N", "north", 0, 1); Direction("W", "west", 1, 0); Direction("◈", "look", 1, 1); Direction("E", "east", 1, 2); Direction("S", "south", 2, 1);
        var vertical = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 8 };
        var up = Command(nameof(L.Up), "up"); up.HorizontalAlignment = HorizontalAlignment.Stretch;
        var down = Command(nameof(L.Down), "down"); down.HorizontalAlignment = HorizontalAlignment.Stretch;
        vertical.Children.Add(up); Grid.SetColumn(down, 1); vertical.Children.Add(down);
        var shortcuts = new StackPanel { Spacing = 8, Children = { Shortcut(nameof(L.LookAround), "look", "look"), Shortcut(nameof(L.WhoSHere), "who", "who"), Shortcut(nameof(L.Inventory), "i", "inventory"), Shortcut(nameof(L.Help), "help", "help") } };
        Content = new ScrollViewer
        {
            Padding = new Thickness(18, 14),
            Content = Ui.Stack(Ui.TextKey(nameof(L.NAVIGATION), 10, "eyebrow"), compass, vertical,
                new Border { Height = 16 }, Ui.TextKey(nameof(L.QUICKCOMMANDS), 10, "eyebrow"), shortcuts,
                Ui.TextKey(nameof(L.CommandsDependOnYourWorld), 10, "muted"),
                new Border { Height = 18 }, Ui.TextKey(nameof(L.SessionHeading), 10, "eyebrow"), _status, _count)
        };
        Refresh();
    }

    private Button Shortcut(string label, string alias, string command)
    {
        var button = Command(label, command);
        var key = Ui.Text(alias, 10, "muted");
        key.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(key, 1);
        button.Bind(Avalonia.Automation.AutomationProperties.NameProperty, LocalizedText.Binding(label));
        button.Content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Children = { Ui.TextKey(label, 12), key } };
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.Padding = new Thickness(12, 11);
        return button;
    }

    private Button Command(string label, string command, bool literal = false)
    {
        var button = literal ? Ui.Button(label, async () => await _controller.SendAsync(command)) : Ui.ButtonKey(label, async () => await _controller.SendAsync(command));
        _commands.Add(button);
        return button;
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); _controller.Changed += Refresh; Refresh(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { _controller.Changed -= Refresh; base.OnDetachedFromVisualTree(e); }
    private void Refresh()
    {
        foreach (var button in _commands) button.IsEnabled = _controller.IsConnected && !_controller.IsPrivate;
        _status.Text = _controller.Status;
        _count.Text = L.Format(L.CommandsSent, _controller.CommandsSent);
    }
}
