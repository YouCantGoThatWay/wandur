using System.Collections.Specialized;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Wandur.Core.Channels;
using Wandur.Desktop.Terminal;
using Wandur.Desktop.ViewModels;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.Views;

/// <summary>
/// The docked Channels panel. It mirrors what the classifier recognized: the transcript still holds every
/// line, and this shows a copy of the conversation, one tab per channel, with a box to answer on it.
/// </summary>
public sealed class ChannelsView : UserControl
{
    public ChannelsViewModel Model { get; }

    public ChannelsView(ChannelsViewModel model)
    {
        Model = model; DataContext = model;
        Name = "ChannelsView";
        var note = Ui.TextKey(nameof(L.ChannelsMirrorNote), 11, "muted");
        note.Name = "ChannelsMirrorNote";
        note.Margin = new Thickness(10, 8);
        note.Bind(IsVisibleProperty, new Binding(nameof(model.IsMirrorNoteVisible)));
        var tabs = new ListBox
        {
            Name = "ChannelTabs", Background = Brushes.Transparent, Padding = new Thickness(4, 2),
            ItemsPanel = new FuncTemplate<Panel?>(() => new WrapPanel { Orientation = Orientation.Horizontal }),
            ItemTemplate = new FuncDataTemplate<ChannelTabViewModel>((tab, _) => tab is null ? null : TabHeader(tab))
        };
        tabs.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(model.Tabs)));
        tabs.Bind(SelectingItemsControl.SelectedIndexProperty, new Binding(nameof(model.SelectedIndex)) { Mode = BindingMode.TwoWay });
        var messages = new ChannelMessageList(model) { Name = "ChannelMessages" };
        var reply = new TextBox { Name = "ChannelReply", MaxLength = 1024, FontSize = 12, MinHeight = 30, AcceptsReturn = false };
        reply.Bind(TextBox.TextProperty, new Binding(nameof(model.Draft)) { Mode = BindingMode.TwoWay });
        reply.Bind(TextBox.PlaceholderTextProperty, new Binding(nameof(model.ReplyHint)));
        reply.Bind(IsEnabledProperty, new Binding(nameof(model.CanReply)));
        reply.Bind(Avalonia.Automation.AutomationProperties.NameProperty, new Binding(nameof(model.ReplyHint)));
        ThemeService.SyncTerminalField(reply);
        ThemeService.Applied += SyncReplyField;
        DetachedFromVisualTree += (_, _) => ThemeService.Applied -= SyncReplyField;
        void SyncReplyField() => ThemeService.SyncTerminalField(reply);
        reply.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None) return;
            e.Handled = true;
            if (model.SendCommand.CanExecute(null)) model.SendCommand.Execute(null);
        };
        var send = new Button { Name = "ChannelSend", Command = model.SendCommand, FontSize = 11, Padding = new Thickness(10, 6) };
        send.Classes.Add("app-button");
        send.Bind(ContentControl.ContentProperty, LocalizedText.Binding(nameof(L.ChannelsSend)));
        Grid.SetColumn(send, 1);
        var replyRow = new Border
        {
            Name = "ChannelReplyBar", Padding = new Thickness(8, 6), BorderThickness = new Thickness(0, 1, 0, 0),
            Child = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6, Children = { reply, send } }
        };
        replyRow.Bind(Border.BackgroundProperty, new DynamicResourceExtension("TerminalBrush"));
        replyRow.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("LineBrush"));
        var header = Ui.Toolbar(tabs, "ChannelsToolbar");
        Grid.SetRow(note, 1); Grid.SetRow(messages, 2); Grid.SetRow(replyRow, 3);
        Content = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), Children = { header, note, messages, replyRow } };
    }

    /// <summary>A tab wears its unread count until the reader looks at it.</summary>
    private static Control TabHeader(ChannelTabViewModel tab)
    {
        var title = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        title.Bind(TextBlock.TextProperty, new Binding(nameof(tab.Title)));
        if (tab.IsPrivate) title.FontWeight = FontWeight.SemiBold;
        var count = new TextBlock { FontSize = 9, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -1, 0, 0) };
        count.Bind(TextBlock.TextProperty, new Binding(nameof(tab.UnreadLabel)));
        var badge = new Border
        {
            Name = "ChannelUnread", CornerRadius = new CornerRadius(7), Padding = new Thickness(5, 1),
            VerticalAlignment = VerticalAlignment.Center, Child = count
        };
        badge.Bind(IsVisibleProperty, new Binding(nameof(tab.HasUnread)));
        badge.Bind(Border.BackgroundProperty, new DynamicResourceExtension("AccentBrush"));
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { title, badge } };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); Model.Attach(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { Model.Detach(); base.OnDetachedFromVisualTree(e); }
}

/// <summary>
/// The messages of the selected tab, drawn with the transcript's own run renderer so a line keeps the color
/// the world gave it. Newest at the bottom, and the view follows new arrivals unless the reader scrolled up.
/// </summary>
internal sealed class ChannelMessageList : Border
{
    private readonly ChannelsViewModel _model;
    private readonly StackPanel _rows = new() { Spacing = 3, Margin = new Thickness(10, 6) };
    private readonly ScrollViewer _viewer;
    private static readonly StyledProperty<IBrush?> MutedProperty = AvaloniaProperty.Register<ChannelMessageList, IBrush?>("Muted");
    private readonly List<IDisposable> _bindings = [];
    private System.Collections.Specialized.INotifyCollectionChanged? _watched;
    private bool _follow = true;

    public ChannelMessageList(ChannelsViewModel model)
    {
        _model = model;
        _viewer = new ScrollViewer { Content = _rows, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Child = _viewer;
        ClipToBounds = true;
        Bind(BackgroundProperty, new DynamicResourceExtension("TerminalBrush"));
        _bindings.Add(this.Bind(TextElement.ForegroundProperty, new DynamicResourceExtension("TerminalTextBrush")));
        _bindings.Add(this.Bind(MutedProperty, new DynamicResourceExtension("MutedBrush")));
        TerminalPalette.Bind(this, _bindings);
        _viewer.ScrollChanged += (_, _) =>
            _follow = _viewer.Offset.Y >= _viewer.Extent.Height - _viewer.Viewport.Height - 2;
    }

    /// <summary>The text on screen, one entry per message.</summary>
    public IReadOnlyList<string> Rows =>
        [.. _rows.Children.OfType<TextBlock>().Select(block => string.Concat(block.Inlines!.OfType<Run>().Select(run => run.Text)))];

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _model.PropertyChanged += ModelChanged;
        ThemeService.Applied += Rebuild;
        Watch();
        Rebuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _model.PropertyChanged -= ModelChanged;
        ThemeService.Applied -= Rebuild;
        if (_watched is not null) _watched.CollectionChanged -= Arrived;
        _watched = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void ModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(ChannelsViewModel.SelectedIndex) or nameof(ChannelsViewModel.IsEmpty))) return;
        Watch();
        Rebuild();
    }

    private void Watch()
    {
        var messages = _model.Selected.Messages;
        if (ReferenceEquals(_watched, messages)) return;
        if (_watched is not null) _watched.CollectionChanged -= Arrived;
        _watched = messages;
        _watched.CollectionChanged += Arrived;
        _follow = true;
    }

    private void Arrived(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        var messages = _model.Selected.Messages;
        while (_rows.Children.Count > messages.Count) _rows.Children.RemoveAt(_rows.Children.Count - 1);
        while (_rows.Children.Count < messages.Count) _rows.Children.Add(NewRow());
        for (var i = 0; i < messages.Count; i++) Fill((TextBlock)_rows.Children[i], messages[i]);
        if (_follow) Avalonia.Threading.Dispatcher.UIThread.Post(_viewer.ScrollToEnd, Avalonia.Threading.DispatcherPriority.Background);
    }

    private static TextBlock NewRow() => new()
    {
        FontFamily = new FontFamily(TerminalPalette.Monospace), FontSize = 12,
        TextWrapping = TextWrapping.Wrap, Inlines = []
    };

    private void Fill(TextBlock block, ChannelMessage message)
    {
        var inlines = block.Inlines!;
        inlines.Clear();
        var stamp = new Run(message.Timestamp.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture) + " ") { FontSize = 10 };
        if (GetValue(MutedProperty) is { } muted) stamp.Foreground = muted;
        inlines.Add(stamp);
        if (message.Speaker.Length > 0) inlines.Add(new Run(message.Speaker + ": ") { FontWeight = FontWeight.Bold });
        foreach (var run in message.Runs) inlines.Add(Styled(run));
        if (message.Runs.Count == 0) inlines.Add(new Run(message.Text));
    }

    private Run Styled(Wandur.Core.Terminal.TextRun run)
    {
        var span = new Run(run.Text);
        if (TerminalPalette.Resolve(this, run.Style.ForegroundIndex, run.Style.Foreground) is { } foreground) span.Foreground = foreground;
        if (TerminalPalette.Resolve(this, run.Style.BackgroundIndex, run.Style.Background) is { } background) span.Background = background;
        if (run.Style.Bold) span.FontWeight = FontWeight.Bold;
        if (run.Style.Italic) span.FontStyle = FontStyle.Italic;
        if (run.Style.Underline) span.TextDecorations = TextDecorations.Underline;
        return span;
    }
}
