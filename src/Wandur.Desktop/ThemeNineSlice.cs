using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Wandur.Core.Discovery;

namespace Wandur.Desktop;

internal readonly record struct SkinPatch(Rect SourcePixels, Rect Destination);

/// <summary>
/// Eight-patch (no center) geometry. Source coordinates are raw bitmap pixels; destination
/// thicknesses are layout DIPs and may differ from the source slice widths.
/// </summary>
internal static class ThemeNineSlice
{
    /// <summary>Return empty when the destination cannot fit the border at its fixed thickness.</summary>
    internal static IReadOnlyList<SkinPatch> Build(
        PixelSize source, SkinPixelBox slice, SkinBox thickness, Size destination)
    {
        if (source.Width <= 0 || source.Height <= 0) return [];
        if (destination.Width <= 0 || destination.Height <= 0) return [];
        if (thickness.Left + thickness.Right > destination.Width ||
            thickness.Top + thickness.Bottom > destination.Height)
            return [];

        var sw = source.Width;
        var sh = source.Height;
        var left = slice.Left;
        var top = slice.Top;
        var right = slice.Right;
        var bottom = slice.Bottom;
        if (left < 0 || top < 0 || right < 0 || bottom < 0) return [];
        if (left + right >= sw || top + bottom >= sh) return [];

        // Source grid: [0, left, width-right, width] × [0, top, height-bottom, height]
        double[] sx = [0, left, sw - right, sw];
        double[] sy = [0, top, sh - bottom, sh];
        // Destination grid from declared DIP thickness.
        double[] dx = [0, thickness.Left, destination.Width - thickness.Right, destination.Width];
        double[] dy = [0, thickness.Top, destination.Height - thickness.Bottom, destination.Height];

        var patches = new List<SkinPatch>(8);
        for (var row = 0; row < 3; row++)
        for (var col = 0; col < 3; col++)
        {
            if (row == 1 && col == 1) continue; // never draw the center
            var sourceRect = new Rect(sx[col], sy[row], sx[col + 1] - sx[col], sy[row + 1] - sy[row]);
            var destRect = new Rect(dx[col], dy[row], dx[col + 1] - dx[col], dy[row + 1] - dy[row]);
            if (sourceRect.Width <= 0 || sourceRect.Height <= 0 || destRect.Width <= 0 || destRect.Height <= 0)
                continue;
            patches.Add(new SkinPatch(sourceRect, destRect));
        }
        return patches;
    }

    /// <summary>
    /// Legacy bezel path: destination edge widths match the (clamped) source slice, preserving
    /// historical sizing when a high-resolution asset is not paired with explicit DIP thickness.
    /// </summary>
    internal static IReadOnlyList<SkinPatch> BuildLegacy(
        PixelSize source, WorldThemeFrameInsets slice, Size destination)
    {
        if (source.Width <= 0 || source.Height <= 0 || destination.Width <= 0 || destination.Height <= 0)
            return [];
        var sw = (double)source.Width;
        var sh = (double)source.Height;
        var left = Math.Min(slice.Left, sw);
        var top = Math.Min(slice.Top, sh);
        var right = Math.Min(slice.Right, Math.Max(0, sw - left));
        var bottom = Math.Min(slice.Bottom, Math.Max(0, sh - top));
        if (left + right >= sw) { left = Math.Min(left, sw / 3); right = Math.Min(right, sw - left); }
        if (top + bottom >= sh) { top = Math.Min(top, sh / 3); bottom = Math.Min(bottom, sh - top); }
        return BuildLegacyExact(sw, sh, left, top, right, bottom, destination);
    }

    private static IReadOnlyList<SkinPatch> BuildLegacyExact(
        double sw, double sh, double left, double top, double right, double bottom, Size destination)
    {
        var destL = Math.Min(left, destination.Width / 2);
        var destT = Math.Min(top, destination.Height / 2);
        var destR = Math.Min(right, Math.Max(0, destination.Width - destL));
        var destB = Math.Min(bottom, Math.Max(0, destination.Height - destT));
        var midW = Math.Max(0, sw - left - right);
        var midH = Math.Max(0, sh - top - bottom);
        var destMidW = Math.Max(0, destination.Width - destL - destR);
        var destMidH = Math.Max(0, destination.Height - destT - destB);
        SkinPatch[] all =
        [
            new(new Rect(0, 0, left, top), new Rect(0, 0, destL, destT)),
            new(new Rect(sw - right, 0, right, top), new Rect(destination.Width - destR, 0, destR, destT)),
            new(new Rect(0, sh - bottom, left, bottom), new Rect(0, destination.Height - destB, destL, destB)),
            new(new Rect(sw - right, sh - bottom, right, bottom), new Rect(destination.Width - destR, destination.Height - destB, destR, destB)),
            new(new Rect(left, 0, midW, top), new Rect(destL, 0, destMidW, destT)),
            new(new Rect(left, sh - bottom, midW, bottom), new Rect(destL, destination.Height - destB, destMidW, destB)),
            new(new Rect(0, top, left, midH), new Rect(0, destT, destL, destMidH)),
            new(new Rect(sw - right, top, right, midH), new Rect(destination.Width - destR, destT, destR, destMidH)),
        ];
        return all.Where(p => p.SourcePixels.Width > 0 && p.SourcePixels.Height > 0 &&
                              p.Destination.Width > 0 && p.Destination.Height > 0).ToArray();
    }

    internal static void Draw(DrawingContext context, Bitmap bitmap, IReadOnlyList<SkinPatch> patches)
    {
        foreach (var patch in patches)
            context.DrawImage(bitmap, patch.SourcePixels, patch.Destination);
    }
}
