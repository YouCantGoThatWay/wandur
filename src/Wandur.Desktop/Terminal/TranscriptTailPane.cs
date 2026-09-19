using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Wandur.Core.Terminal;

namespace Wandur.Desktop.Terminal;

/// <summary>
/// The split screen a Mudlet reader expects: while the transcript is scrolled back, the newest lines keep
/// running in a small pane at the bottom, so nothing happening now is lost. It renders the same buffer as
/// the transcript, which is why masked or withheld input never reaches it either. Clicking it returns the
/// transcript to the tail, the same action as the "latest output" button.
/// </summary>
internal sealed class TranscriptTailPane : Border
{
    public const int MaximumLines = 30;
    private readonly AnsiTerminal _source;
    private readonly StackPanel _rows = new();
    private readonly List<IDisposable> _bindings = [];
    private readonly Action _followTail;
    private int _lines = 8;
    private double _textSize = 15;
    private bool _stale = true;

    public TranscriptTailPane(AnsiTerminal source, Action followTail)
    {
        _source = source; _followTail = followTail;
        Name = "TranscriptTail";
        IsVisible = false;
        ClipToBounds = true;
        VerticalAlignment = VerticalAlignment.Bottom;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        BorderThickness = new Thickness(0, 1, 0, 0);
        Padding = new Thickness(8, 2, 4, 2);
        Child = _rows;
        Bind(BackgroundProperty, new DynamicResourceExtension("TerminalBrush"));
        Bind(BorderBrushProperty, new DynamicResourceExtension("LineBrush"));
        _bindings.Add(this.Bind(TextElement.ForegroundProperty, new DynamicResourceExtension("TerminalTextBrush")));
        TerminalPalette.Bind(this, _bindings);
    }

    /// <summary>How many lines the pane shows; zero turns the feature off.</summary>
    public int Lines
    {
        get => _lines;
        set { var wanted = Math.Clamp(value, 0, MaximumLines); if (_lines == wanted) return; _lines = wanted; Invalidate(); }
    }

    public double TextSize
    {
        get => _textSize;
        set { if (Math.Abs(_textSize - value) < 0.01) return; _textSize = value; Invalidate(); }
    }

    /// <summary>The text on screen, one entry per rendered line.</summary>
    public IReadOnlyList<string> Rows =>
        [.. _rows.Children.OfType<TextBlock>().Select(block => string.Concat(block.Inlines!.OfType<Run>().Select(run => run.Text)))];

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _source.OutputAppended += Appended;
        _source.Cleared += Invalidate;
        ThemeService.Applied += Invalidate;
        Invalidate();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _source.OutputAppended -= Appended;
        _source.Cleared -= Invalidate;
        ThemeService.Applied -= Invalidate;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // A hidden pane does no work; the first frame after it is shown catches up on everything it missed.
        if (change.Property == IsVisibleProperty && IsVisible && _stale) Rebuild();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        _followTail();
    }

    private void Appended(string text, bool local) => Invalidate();

    private void Invalidate()
    {
        _stale = true;
        if (IsVisible) Rebuild();
    }

    private void Rebuild()
    {
        _stale = false;
        var height = LineHeight();
        while (_rows.Children.Count > _lines) _rows.Children.RemoveAt(_rows.Children.Count - 1);
        while (_rows.Children.Count < _lines) _rows.Children.Add(NewRow());
        var all = _source.Lines;
        var count = all.Count;
        // A finished line leaves an empty line in progress behind it; the tail shows output, not that gap.
        if (count > 0 && all[count - 1].Text.Length == 0) count--;
        var first = count - _lines;
        for (var i = 0; i < _rows.Children.Count; i++)
        {
            var block = (TextBlock)_rows.Children[i];
            block.FontSize = _textSize;
            block.LineHeight = height;
            block.Height = height;
            var inlines = block.Inlines!;
            inlines.Clear();
            var index = first + i;
            if (index < 0 || index >= count) continue;
            foreach (var run in all[index].Runs) inlines.Add(Styled(run));
        }
    }

    private TextBlock NewRow() => new()
    {
        FontFamily = new FontFamily(TerminalPalette.Monospace), TextWrapping = TextWrapping.NoWrap,
        Padding = new Thickness(0), Margin = new Thickness(0), Inlines = []
    };

    private Run Styled(TextRun run)
    {
        var span = new Run(run.Text);
        if (TerminalPalette.Resolve(this, run.Style.ForegroundIndex, run.Style.Foreground) is { } foreground) span.Foreground = foreground;
        if (TerminalPalette.Resolve(this, run.Style.BackgroundIndex, run.Style.Background) is { } background) span.Background = background;
        if (run.Style.Bold) span.FontWeight = FontWeight.Bold;
        if (run.Style.Italic) span.FontStyle = FontStyle.Italic;
        if (run.Style.Underline) span.TextDecorations = TextDecorations.Underline;
        return span;
    }

    /// <summary>The pane is exactly as tall as the lines it shows, so its height follows the reader's font.</summary>
    private double LineHeight()
    {
        var sample = new FormattedText("Mg", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily(TerminalPalette.Monospace)), _textSize, null);
        return Math.Ceiling(sample.Height);
    }
}
