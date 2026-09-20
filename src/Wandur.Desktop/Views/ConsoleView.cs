using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Wandur.Core.Diagnostics;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.Views;

/// <summary>
/// The Diagnostics console: the raw text stream and sent commands from a <see cref="ConsoleLog"/>, rendered in a
/// plain read-only editor. The view subscribes while it is in the tree, but while it is off screen or paused a
/// change only sets a flag; the document is brought up to date when the view shows again or the pause ends.
/// Updates are incremental in both directions: new entries are appended, and entries the ring evicted are
/// removed from the top, so a long session never re-renders the whole buffer. It follows the end unless paused
/// or the reader has scrolled up.
/// </summary>
public sealed class ConsoleView : UserControl
{
    private readonly ConsoleLog _log;
    private readonly DiagnosticsBodyEditor _editor;
    private readonly ToggleButton _pause;
    private readonly TextBlock _count;
    private readonly List<(long Sequence, int Length)> _shown = [];
    private bool _dirty = true;
    private bool _follow = true;
    private bool _subscribed;

    public ConsoleView(ConsoleLog log)
    {
        _log = log;
        _count = Ui.Text("", 11, "muted");
        _count.VerticalAlignment = VerticalAlignment.Center;
        var hint = Ui.TextKey(nameof(L.ConsolePrivate), 11, "muted");
        hint.VerticalAlignment = VerticalAlignment.Center; hint.TextTrimming = TextTrimming.CharacterEllipsis;
        var labels = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { _count, hint } };
        _pause = TextToggle("ConsolePause", nameof(L.ConsolePause));
        _pause.IsCheckedChanged += (_, _) => { if (_pause.IsChecked != true) Refresh(); };
        var wrap = TextToggle("ConsoleWrap", nameof(L.ConsoleWrap));
        wrap.IsChecked = true;
        var copy = Ui.ToolbarIconKey(new Button { Name = "ConsoleCopy" },
            "M 5,5 V 2 H 14 V 11 H 11 M 2,5 H 11 V 14 H 2 Z", nameof(L.ConsoleCopy));
        var clear = Ui.ToolbarIconKey(new Button { Name = "ConsoleClear" },
            "M 3,4 H 13 M 6,4 V 2 H 10 V 4 M 4,4 L 5,14 H 11 L 12,4 M 7,6 V 12 M 9,6 V 12", nameof(L.ConsoleClear));
        clear.Click += (_, _) => _log.Clear();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _pause, wrap, copy, clear } };
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(12, 4), Children = { labels, actions } };
        Grid.SetColumn(actions, 1);
        _editor = new DiagnosticsBodyEditor(wordWrap: true, plain: true) { Name = "ConsoleText" };
        copy.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(_editor.Document.Text);
        };
        wrap.IsCheckedChanged += (_, _) =>
        {
            _editor.WordWrap = wrap.IsChecked == true;
            _editor.HorizontalScrollBarVisibility = _editor.WordWrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        };
        _editor.TextArea.TextView.ScrollOffsetChanged += (_, _) =>
            _follow = _editor.VerticalOffset + _editor.ViewportHeight >= _editor.ExtentHeight - 2;
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Children = { bar, _editor } };
        Grid.SetRow(_editor, 1);
        Content = root;
        UpdateCount();
    }

    public bool IsPaused => _pause.IsChecked == true;

    private static ToggleButton TextToggle(string name, string key)
    {
        var toggle = new ToggleButton { Name = name, Width = double.NaN, Height = 26, MinHeight = 0, Padding = new Thickness(6, 0), FontSize = 11 };
        toggle.Classes.Add("command-bar-button");
        toggle.Bind(ContentControl.ContentProperty, LocalizedText.Binding(key));
        toggle.Bind(ToolTip.TipProperty, LocalizedText.Binding(key));
        toggle.Bind(Avalonia.Automation.AutomationProperties.NameProperty, LocalizedText.Binding(key));
        return toggle;
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (!_subscribed)
        {
            _log.Changed += LogChanged; Wandur.Core.Localization.UiLanguage.Changed += RefreshLanguage;
            // The Diagnostics page hides by IsVisible rather than detaching, so the layout pass that shows it again is the cue.
            LayoutUpdated += LayoutChanged; _subscribed = true;
        }
        Refresh();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        if (_subscribed) { _log.Changed -= LogChanged; Wandur.Core.Localization.UiLanguage.Changed -= RefreshLanguage; LayoutUpdated -= LayoutChanged; _subscribed = false; }
        base.OnUnloaded(e);
    }

    private void LayoutChanged(object? sender, EventArgs e) { if (_dirty && !IsPaused && IsEffectivelyVisible) Refresh(); }

    private void LogChanged()
    {
        if (Dispatcher.UIThread.CheckAccess()) Refresh();
        else Dispatcher.UIThread.Post(Refresh, DispatcherPriority.Background);
    }

    /// <summary>Brings the document up to date, or only notes that it is stale while paused or off screen.</summary>
    public void Refresh()
    {
        _dirty = true;
        if (IsPaused || !IsEffectivelyVisible) return;
        var entries = _log.Snapshot();
        UpdateCount(entries.Count);
        var document = _editor.Document;
        if (entries.Count == 0)
        {
            if (document.TextLength > 0) document.Text = "";
            _shown.Clear(); _dirty = false; return;
        }
        // Entries the ring evicted leave the top of the document; the shown list mirrors the ring exactly.
        var evicted = 0; var removed = 0;
        while (evicted < _shown.Count && _shown[evicted].Sequence < entries[0].Sequence) removed += _shown[evicted++].Length;
        if (evicted > 0) { _shown.RemoveRange(0, evicted); document.Remove(0, Math.Min(removed, document.TextLength)); }
        if (_shown.Count > 0 && _shown[0].Sequence != entries[0].Sequence) { document.Text = ""; _shown.Clear(); }
        var last = _shown.Count > 0 ? _shown[^1].Sequence : -1;
        var builder = new System.Text.StringBuilder();
        foreach (var entry in entries)
        {
            if (entry.Sequence <= last) continue;
            var line = entry.Render() + "\n";
            builder.Append(line); _shown.Add((entry.Sequence, line.Length));
        }
        if (builder.Length > 0) document.Insert(document.TextLength, builder.ToString());
        _dirty = false;
        if (_follow) Dispatcher.UIThread.Post(_editor.ScrollToEnd, DispatcherPriority.Background);
    }

    private void UpdateCount(int? count = null) => _count.Text = L.Format(L.ConsoleCount, count ?? _log.Count, ConsoleLog.MaximumEntries);
    public void RefreshLanguage() => UpdateCount();
    internal bool IsStale => _dirty;
}
