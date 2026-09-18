using Avalonia;

namespace Wandur.Desktop.ViewModels;

/// <summary>North-up projection shared by rendering and hit testing. Floors are filtered separately.</summary>
public readonly record struct MapViewport(double CenterX, double CenterY, double PanX, double PanY,
    double Zoom, double Width, double Height)
{
    public double Scale => 96 * Zoom;
    public Point Project(double x, double y) => new(
        Width / 2 + PanX + (x - CenterX) * Scale,
        Height / 2 + PanY - (y - CenterY) * Scale);
    public Point Unproject(Point point) => new(
        CenterX + (point.X - Width / 2 - PanX) / Scale,
        CenterY - (point.Y - Height / 2 - PanY) / Scale);
}
