using Avalonia;

namespace Wandur.Desktop;

internal readonly record struct FleetTitlePlacement(Rect Bounds, bool PlainTitle);

internal static class FleetTitleLayout
{
    // The plaque projects below this seam, into the toolbar's reserved upper ledge.
    public const double BandHeight = 50;
    public const double PlaqueHeight = 60;
    // The dark inset starts 44 DIP inside the outer shoulders. Leave 40 DIP
    // of breathing room inside that inset on each side of the icon and title.
    public const double TextInset = 44 + 40;

    public static FleetTitlePlacement Calculate(double windowWidth, double leftExclusion,
        double rightExclusion, double measuredTextWidth)
    {
        static double Safe(double value) => double.IsFinite(value) ? Math.Max(0, value) : 0;
        var w = Safe(windowWidth);
        var exclusion = Math.Max(Safe(leftExclusion), Safe(rightExclusion)) + 12;
        var available = Math.Max(0, w - exclusion * 2);
        var width = Math.Min(Safe(measuredTextWidth) + TextInset * 2, available);
        return new(new Rect((w - width) / 2, 2, width, PlaqueHeight), width < TextInset * 2 + 48);
    }
}
