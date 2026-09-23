using Avalonia;

namespace Wandur.Desktop;

internal readonly record struct FleetTitlePlacement(Rect Bounds, bool PlainTitle);

internal static class FleetTitleLayout
{
    // The plaque projects below this seam, into the toolbar's reserved upper ledge.
    public const double BandHeight = 50;
    public const double PlaqueHeight = 60;
    public const double TextInset = 64;

    public static FleetTitlePlacement Calculate(double windowWidth, double leftExclusion,
        double rightExclusion, double measuredTextWidth)
    {
        static double Safe(double value) => double.IsFinite(value) ? Math.Max(0, value) : 0;
        var w = Safe(windowWidth);
        var exclusion = Math.Max(Safe(leftExclusion), Safe(rightExclusion)) + 12;
        var available = Math.Max(0, w - exclusion * 2);
        var width = Math.Min(Math.Clamp(Safe(measuredTextWidth) + TextInset * 2, 608, 760), available);
        return new(new Rect((w - width) / 2, 2, width, PlaqueHeight), width < TextInset * 2 + 48);
    }
}
