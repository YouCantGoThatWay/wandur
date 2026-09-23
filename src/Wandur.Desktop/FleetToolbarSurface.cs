using Avalonia;
using Avalonia.Media;

namespace Wandur.Desktop;

/// <summary>The receiving half of the title joint, drawn in the toolbar's own background layer.</summary>
internal static class FleetToolbarSurface
{
    private static readonly IBrush Channel = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Absolute),
        EndPoint = new RelativePoint(0, 20, RelativeUnit.Absolute),
        GradientStops = { new(Color.Parse("#303C3F"), 0), new(Color.Parse("#78827F"), 1) }
    };
    private static readonly Pen Shadow = new(Brush.Parse("#453F4B4D"), 5);
    private static readonly Pen Edge = new(Brush.Parse("#3C494B"), 1);
    private static readonly Pen Highlight = new(Brush.Parse("#F6F9F5"), 1);

    public static IBrush Create(Size size, Rect title)
    {
        if (size.Width < 2 || size.Height < 2) return FleetSkin.Toolbar;
        var top = Math.Min(4, size.Height / 3);
        var notch = Math.Clamp(title.Bottom + 5, top, Math.Max(top, size.Height - 2));
        var left = Math.Clamp(title.Left - 12, 0, size.Width);
        var right = Math.Clamp(title.Right + 12, left, size.Width);
        Point[] rim = title.Width >= 176 && title.Bottom > 0 && right - left >= 42
            ? [new(0, top), new(left, top), new(left + 21, notch),
               new(right - 21, notch), new(right, top), new(size.Width, top)]
            : [new(0, top), new(size.Width, top)];
        var edge = new StreamGeometry();
        using (var path = edge.Open())
        {
            path.BeginFigure(rim[0], false);
            foreach (var point in rim.Skip(1)) path.LineTo(point);
            path.EndFigure(false);
        }
        var face = new StreamGeometry();
        using (var path = face.Open())
        {
            path.BeginFigure(rim[0], true);
            foreach (var point in rim.Skip(1)) path.LineTo(point);
            path.LineTo(new(size.Width, size.Height));
            path.LineTo(new(0, size.Height));
            path.EndFigure(true);
        }
        var shine = new DrawingGroup { Transform = new TranslateTransform(0, 1.5) };
        shine.Children.Add(new GeometryDrawing { Geometry = edge, Pen = Highlight });
        var drawing = new DrawingGroup { ClipGeometry = new RectangleGeometry(new Rect(size)) };
        drawing.Children.Add(new GeometryDrawing { Geometry = new RectangleGeometry(new Rect(size)), Brush = Channel });
        drawing.Children.Add(new GeometryDrawing { Geometry = face, Brush = FleetSkin.Toolbar });
        drawing.Children.Add(new GeometryDrawing { Geometry = edge, Pen = Shadow });
        drawing.Children.Add(new GeometryDrawing { Geometry = edge, Pen = Edge });
        drawing.Children.Add(shine);
        return new DrawingBrush { Drawing = drawing, Stretch = Stretch.Fill,
            SourceRect = new RelativeRect(new Rect(size), RelativeUnit.Absolute) };
    }
}
