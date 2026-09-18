using Avalonia;
using Avalonia.Media;
using Avalonia.Input;
using Avalonia.Threading;
using XTerm.Buffer;

namespace Wandur.Desktop.Terminal;

/// <summary>Adapts text blinking to a transcript with a separate command input.</summary>
internal sealed class MudTerminalSurface : Iciclecreek.Terminal.TerminalView
{
    private readonly DispatcherTimer _blinkTimer;
    private bool _allowBlink, _hiddenPhase, _attached, _sessionInitialized;

    public MudTerminalSurface()
    {
        CursorBlink = false;
        SuppressCursor = true;
        _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
        _blinkTimer.Tick += (_, _) => { _hiddenPhase = !_hiddenPhase; InvalidateVisual(); };
    }

    public void InitializeSession() => OnInitialized();
    protected override void OnInitialized()
    {
        if (_sessionInitialized) return;
        _sessionInitialized = true;
        base.OnInitialized();
    }

    public bool AllowBlink
    {
        get => _allowBlink;
        set { if (_allowBlink == value) return; _allowBlink = value; _hiddenPhase = false; UpdateTimer(); InvalidateVisual(); }
    }

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        var primary = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        if (e.KeyModifiers == primary && e.Key == Key.A)
        {
            e.Handled = true; Terminal.Selection.SelectAll(); InvalidateVisual(); return;
        }
        if ((e.KeyModifiers == primary || e.KeyModifiers == (primary | KeyModifiers.Shift)) && e.Key == Key.C)
        {
            e.Handled = true; await CopyAsync(); return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // A MUD's cursor-style command must not enable the library's focus-dependent blink clock.
        if (change.Property == CursorBlinkProperty && CursorBlink) SetCurrentValue(CursorBlinkProperty, false);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // A shorter viewport (including the output tabs) can add scrollback during reflow.
        // Keep following if we were at the tail; preserve a reader's older scroll position.
        var following = Terminal.Buffer.IsAtBottom;
        var result = base.ArrangeOverride(finalSize);
        if (following) ViewportY = Terminal.Buffer.YBase;
        return result;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    { base.OnAttachedToVisualTree(e); _attached = true; UpdateTimer(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    { _attached = false; UpdateTimer(); base.OnDetachedFromVisualTree(e); }
    private void UpdateTimer()
    { if (_attached && _allowBlink) _blinkTimer.Start(); else { _blinkTimer.Stop(); _hiddenPhase = false; } }

    public override void Render(DrawingContext context)
    {
        if (!_allowBlink || !_hiddenPhase) { base.Render(context); return; }
        // No PTY is attached: writes and drawing both run on the UI thread. Temporarily conceal
        // visible blinking cells, then restore them so export, selection, and future writes retain
        // the server's original attributes. The public cell setter invalidates cached glyph runs.
        var changed = new List<(BufferLine Line, int Column, BufferCell Cell)>();
        try
        {
            for (int y = Terminal.Buffer.YDisp; y < Math.Min(Terminal.Buffer.Lines.Length, Terminal.Buffer.YDisp + Terminal.Rows); y++)
            {
                var line = Terminal.Buffer.GetLine(y);
                if (line is null) continue;
                for (var x = 0; x < line.Length; x++)
                {
                    var cell = line[x];
                    if (!cell.Attributes.IsBlink() || cell.Attributes.IsInvisible()) continue;
                    changed.Add((line, x, cell));
                    cell.Attributes.SetInvisible(true);
                    line[x] = cell;
                }
            }
            base.Render(context);
        }
        finally { foreach (var entry in changed) entry.Line[entry.Column] = entry.Cell; }
    }

    public void StopBlinking() => _blinkTimer.Stop();
}
