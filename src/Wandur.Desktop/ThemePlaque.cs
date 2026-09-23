using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Wandur.Desktop;

/// <summary>
/// The nameplate the world's title sits on, drawn rather than supplied as artwork.
///
/// A plaque is a bar with shaped ends and an accent stripe inside each one, which is eight points and two
/// rectangles. Drawing it beats shipping a picture of it on every count that matters here: it stretches to
/// any title length with no slicing, it is exact at any display scale, it recolours from the palette so a
/// world configures it by naming colours rather than commissioning a PNG, and there is no asset to host,
/// version, cache or invalidate. Every one of those was a real failure while this was a bitmap.
/// </summary>
public sealed class ThemePlaque : Decorator
{
    /// <summary>How the ends are cut. The shape is the client's; which colours fill it are the theme's.</summary>
    public static readonly StyledProperty<string> ShapeProperty =
        AvaloniaProperty.Register<ThemePlaque, string>(nameof(Shape), "chamfer");
    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<ThemePlaque, IBrush?>(nameof(Fill));
    public static readonly StyledProperty<IBrush?> EdgeProperty =
        AvaloniaProperty.Register<ThemePlaque, IBrush?>(nameof(Edge));
    public static readonly StyledProperty<IBrush?> AccentProperty =
        AvaloniaProperty.Register<ThemePlaque, IBrush?>(nameof(Accent));
    /// <summary>Width of the accent stripe at each end, in DIP. Zero draws none.</summary>
    public static readonly StyledProperty<double> CapProperty =
        AvaloniaProperty.Register<ThemePlaque, double>(nameof(Cap), 6);
    /// <summary>How far the bracket behind the plate reaches past it on each side. Zero draws none.</summary>
    public static readonly StyledProperty<double> WingExtendProperty =
        AvaloniaProperty.Register<ThemePlaque, double>(nameof(WingExtend));
    public static readonly StyledProperty<IBrush?> WingFillProperty =
        AvaloniaProperty.Register<ThemePlaque, IBrush?>(nameof(WingFill));
    public static readonly StyledProperty<Color?> ShadowColorProperty =
        AvaloniaProperty.Register<ThemePlaque, Color?>(nameof(ShadowColor));
    public static readonly StyledProperty<double> ShadowOpacityProperty =
        AvaloniaProperty.Register<ThemePlaque, double>(nameof(ShadowOpacity), 0.35);
    public static readonly StyledProperty<double> ShadowBlurProperty =
        AvaloniaProperty.Register<ThemePlaque, double>(nameof(ShadowBlur), 8);
    public static readonly StyledProperty<double> ShadowOffsetProperty =
        AvaloniaProperty.Register<ThemePlaque, double>(nameof(ShadowOffset), 2);

    public double WingExtend { get => GetValue(WingExtendProperty); set => SetValue(WingExtendProperty, value); }
    public IBrush? WingFill { get => GetValue(WingFillProperty); set => SetValue(WingFillProperty, value); }
    public static readonly StyledProperty<IBrush?> WingEdgeProperty =
        AvaloniaProperty.Register<ThemePlaque, IBrush?>(nameof(WingEdge));
    public IBrush? WingEdge { get => GetValue(WingEdgeProperty); set => SetValue(WingEdgeProperty, value); }
    public Color? ShadowColor { get => GetValue(ShadowColorProperty); set => SetValue(ShadowColorProperty, value); }
    public double ShadowOpacity { get => GetValue(ShadowOpacityProperty); set => SetValue(ShadowOpacityProperty, value); }
    public double ShadowBlur { get => GetValue(ShadowBlurProperty); set => SetValue(ShadowBlurProperty, value); }
    public double ShadowOffset { get => GetValue(ShadowOffsetProperty); set => SetValue(ShadowOffsetProperty, value); }

    public string Shape { get => GetValue(ShapeProperty); set => SetValue(ShapeProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public IBrush? Edge { get => GetValue(EdgeProperty); set => SetValue(EdgeProperty, value); }
    public IBrush? Accent { get => GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public double Cap { get => GetValue(CapProperty); set => SetValue(CapProperty, value); }

    static ThemePlaque() =>
        AffectsRender<ThemePlaque>(ShapeProperty, FillProperty, EdgeProperty, AccentProperty, CapProperty,
            WingExtendProperty, WingFillProperty, WingEdgeProperty, ShadowColorProperty, ShadowOpacityProperty,
            ShadowBlurProperty, ShadowOffsetProperty);

    /// <summary>The shapes a theme may ask for. Anything else falls back to a plain rectangle.</summary>
    public static bool IsShape(string? shape) =>
        shape is "chamfer" or "notch" or "round" or "square" or "fleet";

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var size = Bounds.Size;
        if (size.Width <= 1 || size.Height <= 1 || Fill is null) return;
        if (Shape == "fleet")
        {
            DrawFleet(context, size);
            return;
        }

        // With wings the plaque is two parts: a light bracket filling the whole bounds, and the dark plate
        // set into it, narrower by the wing reach on each side and inset from the top and bottom so the
        // bracket shows all the way round. Without wings the plate is the whole plaque.
        var wing = Math.Clamp(WingExtend, 0, size.Width / 3);
        var bracketed = wing > 0 && WingFill is not null;
        var lip = bracketed ? size.Height * 0.16 : 0;
        var plate = new Rect(wing, lip, Math.Max(1, size.Width - wing * 2), Math.Max(1, size.Height - lip * 2));

        DrawShadow(context, bracketed ? new Rect(size) : plate);

        if (bracketed)
            context.DrawGeometry(Shaded(WingFill!, 0.35, -0.06), WingEdge is null ? null : new Pen(WingEdge, 1),
                Placed(Shape, new Rect(size)));

        context.DrawGeometry(Shaded(Fill, 0.08, -0.10), Edge is null ? null : new Pen(Edge, 1), Placed(Shape, plate));

        if (Accent is null || Cap <= 0) return;
        // The stripes sit inside the shaped ends rather than on them, so a chamfer still reads as a cut corner
        // instead of being squared off by its own accent.
        var inset = Shape switch
        {
            "chamfer" or "notch" => plate.Height * 0.5,
            "round" => plate.Height * 0.35,
            _ => 4d,
        };
        var cap = Math.Min(Cap, Math.Max(0, (plate.Width - inset * 2) / 2));
        if (cap <= 0) return;
        var top = plate.Y + plate.Height * 0.18;
        var height = plate.Height * 0.64;
        context.FillRectangle(Accent, new Rect(plate.X + inset, top, cap, height));
        context.FillRectangle(Accent, new Rect(plate.Right - inset - cap, top, cap, height));
    }

    private Size _fleetSize;
    private StreamGeometry? _fleetOutline;
    private static IBrush FleetLip => FleetSkin.RimHighlight;
    private static IBrush FleetShadow => FleetSkin.RimShadow;

    internal static Rect FleetPlate(Size size)
    {
        var inset = Math.Min(7, Math.Max(0, size.Height * .12));
        return new(44, inset, Math.Max(0, size.Width - 88), Math.Max(0, size.Height - inset * 2));
    }

    private void DrawFleet(DrawingContext context, Size size)
    {
        if (size.Width < 168 || size.Height < 24) return;
        if (_fleetOutline is null || _fleetSize != size)
        {
            _fleetSize = size;
            var w = size.Width; var h = size.Height;
            _fleetOutline = new StreamGeometry();
            using var draw = _fleetOutline.Open();
            draw.BeginFigure(new Point(28, 2), true);
            foreach (var p in new Point[] { new(w-28,2), new(w-.5,h*.60),
                new(w-12,h-2), new(12,h-2), new(.5,h*.60) })
                draw.LineTo(p);
            draw.EndFigure(true);
        }
        // A short extrusion under the actual silhouette, not a rectangular drop shadow.
        using (context.PushTransform(Matrix.CreateTranslation(0, 2)))
            context.DrawGeometry(FleetShadow, new Pen(Brush.Parse("#303B3D"), 1), _fleetOutline);
        context.DrawGeometry(WingFill is ISolidColorBrush ? Shaded(WingFill, .2, -.12) : WingFill ?? FleetSkin.Metal,
            new Pen(WingEdge ?? FleetShadow, 1), _fleetOutline);
        context.DrawLine(new Pen(FleetLip, 1), new(28, 3), new(size.Width-28, 3));
        context.DrawLine(new Pen(FleetLip, 1), new(28, 3), new(1.5, size.Height*.60));
        context.DrawLine(new Pen(FleetLip, 1), new(size.Width-28, 3), new(size.Width-1.5, size.Height*.60));
        context.DrawLine(new Pen(FleetShadow, 1), new(12, size.Height-3), new(size.Width-12, size.Height-3));
        var plate = FleetPlate(size);
        context.DrawRectangle(Fill is ISolidColorBrush ? Shaded(Fill, .12, -.06) : Fill,
            new Pen(FleetLip, 1), plate, 2, 2);
        context.DrawRectangle(null, new Pen(Edge ?? FleetShadow, 1), plate.Deflate(1), 2, 2);
        // Recessed plate: dark inner top edge, reflected light at the bottom.
        context.DrawLine(new Pen(Brush.Parse("#101B20"), 1), new(plate.Left+2, plate.Top+2), new(plate.Right-2, plate.Top+2));
        context.DrawLine(new Pen(Brush.Parse("#8A9698"), 1), new(plate.Left+2, plate.Bottom-1), new(plate.Right-2, plate.Bottom-1));
        if (Accent is null || Cap <= 0) return;
        var lampInset = Math.Min(6, plate.Height * .18);
        foreach (var x in new[] { plate.X + 10, plate.Right - 13 })
        {
            var lamp = new Rect(x, plate.Y + lampInset, 3, plate.Height - lampInset * 2);
            using (context.PushOpacity(.16)) context.DrawRectangle(Accent, null, lamp.Inflate(2), 3, 3);
            context.DrawRectangle(Accent, null, lamp, 1.5, 1.5);
        }
    }

    /// <summary>
    /// A flat colour given a top-lit face: brighter along the top, a touch darker at the foot. Any other
    /// brush is used as it is, so a theme that already supplies a gradient is not shaded twice.
    /// </summary>
    private static IBrush Shaded(IBrush brush, double lift, double drop)
    {
        if (brush is not ISolidColorBrush solid) return brush;
        static Color Shift(Color c, double a)
        {
            var target = a >= 0 ? 255 : 0; var w = Math.Abs(a);
            return Color.FromArgb(c.A, (byte)Math.Round(c.R + (target - c.R) * w),
                (byte)Math.Round(c.G + (target - c.G) * w), (byte)Math.Round(c.B + (target - c.B) * w));
        }
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Shift(solid.Color, lift), 0),
                new GradientStop(solid.Color, 0.18),
                new GradientStop(Shift(solid.Color, drop), 1),
            },
        };
    }

    /// <summary>
    /// The outline in the plaque's own space. The cut is proportional to height, so a plaque keeps its
    /// character whether it is drawn on a compact bar or a deep one, and never depends on the title length.
    /// </summary>
    /// <summary>
    /// A soft drop under the silhouette, approximated by stacking it a few times, each a little larger and
    /// fainter. There is no blur primitive to reach for here, and a plate this size wants a hint of depth,
    /// not a true gaussian.
    /// </summary>
    private void DrawShadow(DrawingContext context, Rect shape)
    {
        if (ShadowColor is not { } colour || ShadowOpacity <= 0 || shape.Width <= 1 || shape.Height <= 1) return;
        var steps = ShadowBlur <= 0 ? 1 : Math.Min(5, (int)Math.Ceiling(ShadowBlur / 3));
        var each = ShadowOpacity / steps;
        for (var i = steps; i >= 1; i--)
        {
            var spread = ShadowBlur <= 0 ? 0 : ShadowBlur * i / steps;
            var rect = shape.Inflate(spread).Translate(new Vector(0, ShadowOffset));
            if (rect.Width <= 1 || rect.Height <= 1) continue;
            context.DrawGeometry(new SolidColorBrush(colour, each), null, Placed(Shape, rect));
        }
    }

    /// <summary>The outline for a shape, positioned at a rectangle's origin.</summary>
    private static Geometry Placed(string shape, Rect rect)
    {
        var geometry = Outline(shape, rect.Size);
        geometry.Transform = new TranslateTransform(rect.X, rect.Y);
        return geometry;
    }

    internal static Geometry Outline(string shape, Size size)
    {
        var (w, h) = (size.Width, size.Height);
        if (shape == "round")
            return new RectangleGeometry(new Rect(0, 0, w, h), h / 2, h / 2);
        if (shape == "square" || !IsShape(shape))
            return new RectangleGeometry(new Rect(0, 0, w, h));

        var cut = Math.Min(h / 2, w / 2);
        var geometry = new StreamGeometry();
        using var draw = geometry.Open();
        if (shape == "chamfer")
        {
            draw.BeginFigure(new Point(cut, 0), true);
            draw.LineTo(new Point(w - cut, 0));
            draw.LineTo(new Point(w, h / 2));
            draw.LineTo(new Point(w - cut, h));
            draw.LineTo(new Point(cut, h));
            draw.LineTo(new Point(0, h / 2));
        }
        else // notch: the ends step in rather than running to a point
        {
            var step = cut * 0.55;
            draw.BeginFigure(new Point(step, 0), true);
            draw.LineTo(new Point(w - step, 0));
            draw.LineTo(new Point(w - step, h * 0.25));
            draw.LineTo(new Point(w, h * 0.25));
            draw.LineTo(new Point(w, h * 0.75));
            draw.LineTo(new Point(w - step, h * 0.75));
            draw.LineTo(new Point(w - step, h));
            draw.LineTo(new Point(step, h));
            draw.LineTo(new Point(step, h * 0.75));
            draw.LineTo(new Point(0, h * 0.75));
            draw.LineTo(new Point(0, h * 0.25));
            draw.LineTo(new Point(step, h * 0.25));
        }
        draw.EndFigure(true);
        return geometry;
    }
}
