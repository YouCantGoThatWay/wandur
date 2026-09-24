using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Wandur.Core.Discovery;

namespace Wandur.Desktop;

/// <summary>
/// Modular window border behind the live shell. Passthrough when no skin window bitmap is ready
/// so <see cref="ThemeBezelHost"/> can still own the legacy bezel.
/// </summary>
internal sealed class ThemeWindowSkinHost : Decorator
{
    private IBrush? _edgeAccent;
    public static readonly StyledProperty<Bitmap?> BorderBitmapProperty =
        AvaloniaProperty.Register<ThemeWindowSkinHost, Bitmap?>(nameof(BorderBitmap));
    public static readonly StyledProperty<WorldThemeSkinBorder?> BorderMetaProperty =
        AvaloniaProperty.Register<ThemeWindowSkinHost, WorldThemeSkinBorder?>(nameof(BorderMeta));
    public static readonly StyledProperty<Thickness> InsetProperty =
        AvaloniaProperty.Register<ThemeWindowSkinHost, Thickness>(nameof(Inset));
    public static readonly StyledProperty<Rect> TitleModuleBoundsProperty =
        AvaloniaProperty.Register<ThemeWindowSkinHost, Rect>(nameof(TitleModuleBounds));
    public Rect TitleModuleBounds { get => GetValue(TitleModuleBoundsProperty); set => SetValue(TitleModuleBoundsProperty, value); }

    public Bitmap? BorderBitmap
    {
        get => GetValue(BorderBitmapProperty);
        set => SetValue(BorderBitmapProperty, value);
    }
    public WorldThemeSkinBorder? BorderMeta
    {
        get => GetValue(BorderMetaProperty);
        set => SetValue(BorderMetaProperty, value);
    }
    public Thickness Inset
    {
        get => GetValue(InsetProperty);
        set => SetValue(InsetProperty, value);
    }

    static ThemeWindowSkinHost()
    {
        AffectsRender<ThemeWindowSkinHost>(EdgeBrushProperty, EdgeThicknessProperty, EdgeOutlineProperty, GroundBrushProperty, BandBrushProperty,
            BandHeightProperty, BorderBitmapProperty, BorderMetaProperty, InsetProperty, TitleModuleBoundsProperty);
        AffectsMeasure<ThemeWindowSkinHost>(BorderBitmapProperty, BandHeightProperty, InsetProperty,
            EdgeOutlineProperty, EdgeThicknessProperty);
        AffectsArrange<ThemeWindowSkinHost>(BorderBitmapProperty, BandHeightProperty, InsetProperty,
            EdgeOutlineProperty, EdgeThicknessProperty);
    }

    /// <summary>
    /// The device edge: a band of metal down both sides and along the bottom, held between a dark line on
    /// the outside and a dark line on the inside, with a bright line just inside the outer one where the
    /// light catches the lip. Each side is filled as a rectangle rather than stroked, so the corners meet
    /// cleanly instead of overlapping into a darker square.
    /// </summary>
    private void DrawBevel(DrawingContext context, Size size, Color metal, Color outline)
    {
        var t = EdgeThickness;
        var fill = new SolidColorBrush(metal);
        var dark = new SolidColorBrush(outline);
        var lip = FleetSkin.IsActive ? FleetSkin.RimHighlight : new SolidColorBrush(Lighten(metal, 0.55));
        var (w, h) = (size.Width, size.Height);
        var top = Math.Max(0, BorderBitmap is null ? BandHeight : 0);
        if (w < t * 2 || h <= top + t) return;

        // Metal bands.
        context.FillRectangle(fill, new Rect(0, top, t, h - top));
        context.FillRectangle(fill, new Rect(w - t, top, t, h - top));
        context.FillRectangle(fill, new Rect(0, h - t, w, t));

        // Outer outline, then the lip just inside it.
        context.FillRectangle(dark, new Rect(0, top, 1, h - top));
        context.FillRectangle(dark, new Rect(w - 1, top, 1, h - top));
        context.FillRectangle(dark, new Rect(0, h - 1, w, 1));
        context.FillRectangle(lip, new Rect(1, top, 1, h - top - 1));
        context.FillRectangle(lip, new Rect(w - 2, top, 1, h - top - 1));
        context.FillRectangle(lip, new Rect(1, h - 2, w - 2, 1));

        // Inner outline, where the frame meets the content.
        context.FillRectangle(dark, new Rect(t - 1, top, 1, h - top - t));
        context.FillRectangle(dark, new Rect(w - t, top, 1, h - top - t));
        context.FillRectangle(dark, new Rect(t - 1, h - t, w - t * 2 + 2, 1));
        if (_edgeAccent is not null && BorderBitmap is null && t >= 4)
        {
            context.FillRectangle(_edgeAccent, new Rect(2, top, 1.5, h - top - t));
            context.FillRectangle(_edgeAccent, new Rect(w - 3.5, top, 1.5, h - top - t));
        }
    }

    private static Color Lighten(Color colour, double amount) => Color.FromArgb(colour.A,
        (byte)Math.Round(colour.R + (255 - colour.R) * amount),
        (byte)Math.Round(colour.G + (255 - colour.G) * amount),
        (byte)Math.Round(colour.B + (255 - colour.B) * amount));

    public static readonly StyledProperty<IBrush?> GroundBrushProperty =
        AvaloniaProperty.Register<ThemeWindowSkinHost, IBrush?>(nameof(GroundBrush));
    /// <summary>
    /// The chassis behind the content. The dock containers are transparent all the way down to the window
    /// background, so the gutters between panels show whatever is painted here, and this host sits behind
    /// the content while knowing where the band and frame end.
    /// </summary>
    public IBrush? GroundBrush { get => GetValue(GroundBrushProperty); set => SetValue(GroundBrushProperty, value); }

    public static readonly StyledProperty<IBrush?> BandBrushProperty =
        AvaloniaProperty.Register<ThemeWindowSkinHost, IBrush?>(nameof(BandBrush));
    public static readonly StyledProperty<double> BandHeightProperty =
        AvaloniaProperty.Register<ThemeWindowSkinHost, double>(nameof(BandHeight));

    /// <summary>What fills the title band when the theme has no window art to supply one.</summary>
    public IBrush? BandBrush { get => GetValue(BandBrushProperty); set => SetValue(BandBrushProperty, value); }
    /// <summary>How deep that band is, or zero for none.</summary>
    public double BandHeight { get => GetValue(BandHeightProperty); set => SetValue(BandHeightProperty, value); }

    /// <summary>
    /// The room the chrome takes out of the window. Window art states it through its inset; a theme with
    /// no art states it as a band height instead. Without the second case a theme that moved its toolbar
    /// into the title bar reserved nothing, so the bar had no surface, the content began at the top of the
    /// window, and the title floated over the transcript.
    /// </summary>
    private Thickness ActiveInset => BorderBitmap is not null ? Inset
        : new Thickness(FrameWidth, Math.Max(0, BandHeight), FrameWidth, FrameWidth);

    /// <summary>A bevelled edge is a frame with depth, so content starts inside it; a hairline takes no room.</summary>
    private double FrameWidth => EdgeOutline is not null ? EdgeThickness : 0;

    protected override Size MeasureOverride(Size availableSize)
    {
        var inset = ActiveInset;
        var width = double.IsInfinity(availableSize.Width) ? availableSize.Width : Math.Max(0, availableSize.Width - inset.Left - inset.Right);
        var height = double.IsInfinity(availableSize.Height) ? availableSize.Height : Math.Max(0, availableSize.Height - inset.Top - inset.Bottom);
        Child?.Measure(new Size(width, height));
        var desired = Child?.DesiredSize ?? default;
        return new Size(desired.Width + inset.Left + inset.Right, desired.Height + inset.Top + inset.Bottom);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var inset = ActiveInset;
        Child?.Arrange(new Rect(inset.Left, inset.Top,
            Math.Max(0, finalSize.Width - inset.Left - inset.Right),
            Math.Max(0, finalSize.Height - inset.Top - inset.Bottom)));
        return finalSize;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (GroundBrush is { } chassis && Bounds.Width > 0 && Bounds.Height > 0)
        {
            var inset = ActiveInset;
            var inner = new Rect(inset.Left, inset.Top, Math.Max(0, Bounds.Width - inset.Left - inset.Right),
                Math.Max(0, Bounds.Height - inset.Top - inset.Bottom));
            if (inner.Width > 0 && inner.Height > 0) context.FillRectangle(chassis, inner);
        }
        // The band is painted before anything else so the title, the toolbar and any art sit on it.
        if (BorderBitmap is null && BandBrush is { } band && BandHeight > 0 && Bounds.Width > 0)
        {
            context.FillRectangle(band, new Rect(0, 0, Bounds.Width, BandHeight));
            if (FleetSkin.IsActive && Bounds.Width > 12)
            {
                var dark = new Pen(FleetSkin.RimEdge, 1);
                var lip = new Pen(FleetSkin.RimHighlight, 1);
                if (OperatingSystem.IsMacOS())
                {
                    // Native traffic lights keep their system position. Give them an
                    // uninterrupted metal surround instead of a close-fitting bevel.
                    context.FillRectangle(FleetSkin.Metal, new Rect(0, 0, Bounds.Width, BandHeight));
                }
                else
                {
                    var cap = new Rect(5.5, 2.5, Bounds.Width - 11, Math.Max(0, BandHeight - 3));
                    context.DrawRectangle(FleetSkin.Metal, dark, cap, 4, 4);
                    context.DrawRectangle(null, lip, cap.Deflate(1), 3, 3);
                }
                // The side rails meet the plaque shoulders instead of boxing a separate badge.
                var railY = BandHeight - .5;
                var leftEnd = Math.Clamp(TitleModuleBounds.Left + 6, 6, Bounds.Width - 6);
                var rightStart = Math.Clamp(TitleModuleBounds.Right - 6, leftEnd, Bounds.Width - 6);
                context.DrawLine(dark, new(6, railY), new(leftEnd, railY));
                context.DrawLine(dark, new(rightStart, railY), new(Bounds.Width - 6, railY));
                context.DrawLine(lip, new(6, railY + 1), new(leftEnd, railY + 1));
                context.DrawLine(lip, new(rightStart, railY + 1), new(Bounds.Width - 6, railY + 1));
            }
        }
        // The hairline is drawn whether or not there is window art, because a theme may declare one alone.
        DrawEdge(context);
        if (BorderBitmap is not { } bitmap || BorderMeta is not { } meta) return;
        var size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) return;
        var patches = ThemeNineSlice.Build(
            bitmap.PixelSize, meta.Slice, meta.Thickness, size, meta.Tiles);
        ThemeNineSlice.Draw(context, bitmap, patches);
    }

    /// <summary>
    /// The hairline framing the window, down both sides and along the bottom. The title bar caps the top,
    /// so drawing there would double the line the bar already ends with. Drawn on a half pixel so the
    /// stroke lands on one device pixel instead of straddling two and going soft.
    /// </summary>
    private void DrawEdge(DrawingContext context)
    {
        if (EdgeBrush is not { } brush || EdgeThickness <= 0) return;
        var size = Bounds.Size;
        if (size.Width <= 1 || size.Height <= 1) return;
        if (EdgeOutline is { } outline && brush is ISolidColorBrush metal)
        {
            DrawBevel(context, size, metal.Color, outline);
            return;
        }
        var pen = new Pen(brush, EdgeThickness);
        var half = EdgeThickness / 2;
        context.DrawLine(pen, new Point(half, 0), new Point(half, size.Height - half));
        context.DrawLine(pen, new Point(size.Width - half, 0), new Point(size.Width - half, size.Height - half));
        context.DrawLine(pen, new Point(0, size.Height - half), new Point(size.Width, size.Height - half));
    }

    public static readonly StyledProperty<IBrush?> EdgeBrushProperty =
        AvaloniaProperty.Register<ThemeWindowSkinHost, IBrush?>(nameof(EdgeBrush));
    public static readonly StyledProperty<double> EdgeThicknessProperty =
        AvaloniaProperty.Register<ThemeWindowSkinHost, double>(nameof(EdgeThickness));

    public IBrush? EdgeBrush { get => GetValue(EdgeBrushProperty); set => SetValue(EdgeBrushProperty, value); }
    public static readonly StyledProperty<Color?> EdgeOutlineProperty =
        AvaloniaProperty.Register<ThemeWindowSkinHost, Color?>(nameof(EdgeOutline));
    /// <summary>When set, the edge is drawn as a bevelled frame rather than a single line.</summary>
    public Color? EdgeOutline { get => GetValue(EdgeOutlineProperty); set => SetValue(EdgeOutlineProperty, value); }
    public double EdgeThickness { get => GetValue(EdgeThicknessProperty); set => SetValue(EdgeThicknessProperty, value); }

    public void ApplyFromTheme()
    {
        var skin = ThemeSkinResources.FromApplied();
        // The framing hairline is independent of the window art: a theme may have one, the other, both or
        // neither, and a theme made only of colours is the case this exists for.
        var layout = ThemeService.AppliedSkin?.Layout?.TitleBar;
        GroundBrush = ThemeSkinSurfaces.Build(ThemeService.AppliedSkin?.Surfaces?.Ground);
        BandHeight = layout?.Height ?? 0;
        BandBrush = BandHeight > 0 ? Application.Current?.Resources["ChromeBrush"] as IBrush : null;

        var themeEdge = ThemeService.AppliedSkin?.Edge;
        _edgeAccent = themeEdge?.Accent is { } accent && Color.TryParse(accent, out var accentColor)
            ? new SolidColorBrush(accentColor) : null;
        EdgeBrush = themeEdge is not null && Color.TryParse(themeEdge.Color, out var edgeColour)
            ? new SolidColorBrush(edgeColour) : null;
        EdgeThickness = themeEdge?.Thickness ?? 0;
        EdgeOutline = themeEdge?.Outline is { } outlineHex && Color.TryParse(outlineHex, out var outlineColour)
            ? outlineColour : null;
        if (skin is not { WindowReady: true } ||
            ThemeService.AppliedWorldTheme?.Skin?.Window is not { } window)
        {
            BorderBitmap = null;
            BorderMeta = null;
            Inset = default;
            InvalidateMeasure();
            InvalidateVisual();
            return;
        }
        BorderBitmap = skin.WindowBorder;
        BorderMeta = skin.WindowBorderMeta;
        Inset = new Thickness(window.Inset.Left, window.Inset.Top, window.Inset.Right, window.Inset.Bottom);
        InvalidateMeasure();
        InvalidateVisual();
    }
}
