using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Wandur.Core.Settings;
using Wandur.Core.Terminal;
using XTerm.Options;

namespace Wandur.Desktop.Terminal;

/// <summary>One incremental terminal buffer per session, independent of tab visibility.</summary>
internal sealed class TranscriptDisplay : ITranscriptDisplay
{
    private readonly AnsiTerminal _source;
    private readonly MudTerminalSurface _surface;
    private readonly ScrollBar _scrollbar;
    private readonly TerminalStreamFramer _framer = new();
    private bool _disposed, _updatingScroll;
    private readonly List<IDisposable> _bindings = [];
    private static readonly StyledProperty<IBrush?>[] PaletteProperties = new StyledProperty<IBrush?>[16];
    static TranscriptDisplay()
    { for (var i = 0; i < 16; i++) PaletteProperties[i] = AvaloniaProperty.Register<MudTerminalSurface, IBrush?>("AnsiPalette" + i); }

    public TranscriptDisplay(AnsiTerminal source)
    {
        _source = source;
        _surface = new MudTerminalSurface
        {
            Name = "Transcript", Process = "", FontFamily = new FontFamily("Menlo, Consolas, DejaVu Sans Mono"),
            BufferSize = 2000, ConvertEol = true, AllowWindowOps = false, ShowCaretOnClick = false,
            Options = new TerminalOptions
            {
                Cols = 100, Rows = 30, ConvertEol = true, Scrollback = 2000,
                ClipboardReadEnabled = false, ClipboardWriteEnabled = false,
                SixelEnabled = false, KittyGraphicsEnabled = false, ITerm2ImagesEnabled = false,
                KittyNotificationsEnabled = false, KittyKeyboardEnabled = false
            }
        };
        _surface.BeginInit(); _surface.EndInit(); _surface.InitializeSession();
        _bindings.Add(_surface.Bind(Iciclecreek.Terminal.TerminalView.BackgroundProperty, new DynamicResourceExtension("TerminalBrush")));
        _bindings.Add(_surface.Bind(Iciclecreek.Terminal.TerminalView.ForegroundProperty, new DynamicResourceExtension("TerminalTextBrush")));
        _bindings.Add(_surface.Bind(Iciclecreek.Terminal.TerminalView.SelectionBrushProperty, new DynamicResourceExtension("WorldSelectionBrush")));
        for (var i = 0; i < 16; i++) _bindings.Add(_surface.Bind(PaletteProperties[i], new DynamicResourceExtension($"AnsiColor{i}Brush")));
        _surface.PropertyChanged += SurfaceChanged;
        _scrollbar = new ScrollBar { Orientation = Orientation.Vertical, SmallChange = 3, MinWidth = 12 };
        _scrollbar.PropertyChanged += (_, e) =>
        {
            if (!_updatingScroll && e.Property == RangeBase.ValueProperty) { _surface.ViewportY = (int)_scrollbar.Value; UpdateScroll(); }
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { _surface, _scrollbar } };
        Grid.SetColumn(_scrollbar, 1);
        View = grid;
        _source.OutputAppended += Append;
        _source.Cleared += Clear;
        // The palette brushes keep their identity across theme changes, so only their colors move and
        // the bound properties raise nothing. One notification per applied theme rebuilds the engine
        // palette and repaints the existing buffer once, without reparsing it.
        ThemeService.Applied += UpdatePalette;
        UpdatePalette();
    }
    public Control View { get; }
    public string PlainText
    {
        get
        {
            var result = new StringBuilder();
            var buffer = _surface.Terminal.Buffer;
            var last = buffer.Lines.Length - 1;
            while (last >= 0 && string.IsNullOrWhiteSpace(buffer.GetLine(last)!.TranslateToString(true))) last--;
            for (var i = 0; i <= last; i++)
            {
                var line = buffer.GetLine(i)!;
                if (i > 0 && !line.IsWrapped) result.Append('\n');
                result.Append(line.TranslateToString(true));
            }
            return result.ToString();
        }
    }
    public bool IsFollowingTail => _surface.Terminal.Buffer.IsAtBottom;
    public event Action? ViewportChanged;
    public void FollowTail() { _surface.ViewportY = _surface.Terminal.Buffer.YBase; UpdateScroll(); }
    public void ApplySettings(ClientSettings settings)
    { _surface.FontSize = settings.FontSize; _surface.AllowBlink = settings.AllowBlinkingText; }

    private void SurfaceChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (PaletteProperties.Contains(e.Property) || e.Property == Iciclecreek.Terminal.TerminalView.ForegroundProperty || e.Property == Iciclecreek.Terminal.TerminalView.BackgroundProperty) UpdatePalette();
        if (e.Property == Iciclecreek.Terminal.TerminalView.ViewportYProperty || e.Property == Iciclecreek.Terminal.TerminalView.MaxScrollbackProperty || e.Property == Iciclecreek.Terminal.TerminalView.ViewportLinesProperty) UpdateScroll();
    }
    private void UpdatePalette()
    {
        if (_disposed) return;
        var theme = new ThemeOptions
        {
            Foreground = Hex(_surface.Foreground, "#C8C8C8"), Background = Hex(_surface.Background, "#101214")
        };
        string Color(int i) => Hex(_surface.GetValue(PaletteProperties[i]), AnsiPalette.Defaults[i]);
        theme.Black = Color(0); theme.Red = Color(1); theme.Green = Color(2); theme.Yellow = Color(3);
        theme.Blue = Color(4); theme.Magenta = Color(5); theme.Cyan = Color(6); theme.White = Color(7);
        theme.BrightBlack = Color(8); theme.BrightRed = Color(9); theme.BrightGreen = Color(10); theme.BrightYellow = Color(11);
        theme.BrightBlue = Color(12); theme.BrightMagenta = Color(13); theme.BrightCyan = Color(14); theme.BrightWhite = Color(15);
        _surface.Terminal.Options.Theme = theme;
        _surface.InvalidateVisual();
    }
    private static string Hex(IBrush? brush, string fallback) => brush is ISolidColorBrush b ? $"#{b.Color.R:X2}{b.Color.G:X2}{b.Color.B:X2}" : fallback;

    private void Append(string text, bool local)
    {
        if (_disposed) return;
        var follow = IsFollowingTail;
        if (local) WriteLocal(text);
        else
        {
            var complete = _framer.Feed(text);
            if (complete.Length > 0) _surface.Terminal.Write(complete);
        }
        if (follow) _surface.ViewportY = _surface.Terminal.Buffer.YBase;
        UpdateScroll();
        _surface.InvalidateVisual();
    }
    private void WriteLocal(string text)
    {
        var engine = _surface.Terminal;
        // Preserve the server's saved cursor and SGR state. Local echo is literal and cannot
        // finish a partial server CSI/OSC sequence (held separately by the framer).
        var saved = engine.Buffer.SavedCursorState;
        engine.Buffer.SavedCursorState = new XTerm.Buffer.TerminalBuffer.SavedCursor();
        engine.Write("\x1b" + "7\x1b(B\u000f\x1b[0;90m");
        var literal = new StringBuilder(text.Length);
        foreach (var ch in text) { if (ch == '\n') literal.Append("\r\n"); else if (!char.IsControl(ch)) literal.Append(ch); }
        engine.Write(literal.ToString());
        var x = engine.Buffer.X; var y = engine.Buffer.Y;
        engine.Write("\x1b" + "8");
        engine.Buffer.SetCursor(x, y);
        engine.Buffer.SavedCursorState = saved;
    }
    private void Clear()
    {
        _framer.Reset();
        _surface.Terminal.Reset();
        UpdatePalette();
        UpdateScroll();
        _surface.InvalidateVisual();
    }
    private void UpdateScroll()
    {
        if (_disposed || _scrollbar is null) return;
        _updatingScroll = true;
        try { _scrollbar.Maximum = _surface.Terminal.Buffer.YBase; _scrollbar.ViewportSize = _surface.ViewportLines; _scrollbar.Value = _surface.ViewportY; }
        finally { _updatingScroll = false; }
        ViewportChanged?.Invoke();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ThemeService.Applied -= UpdatePalette;
        _source.OutputAppended -= Append; _source.Cleared -= Clear;
        _surface.PropertyChanged -= SurfaceChanged;
        _surface.StopBlinking();
        foreach (var binding in _bindings) binding.Dispose();
        _surface.Dispose();
    }
}
