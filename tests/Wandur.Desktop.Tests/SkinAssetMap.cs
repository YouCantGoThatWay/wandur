using Wandur.Core.Discovery;

namespace Wandur.Desktop.Tests;

/// <summary>
/// Serves every asset a theme's skin declares, at the size the skin says it is. Tests used to pin the
/// file names and pixel sizes by hand, which meant swapping the artwork broke a dozen tests that were
/// not about the artwork. Reading them from the theme keeps the assets config.
/// </summary>
internal static class SkinAssetMap
{
    internal static IReadOnlyList<(string Fragment, int Width, int Height)> Declared(WorldTheme theme)
    {
        var list = new List<(string, int, int)>();
        void Add(string url, SkinPixelSize size)
        {
            var fragment = url.Split('/')[^1];
            if (fragment.Length > 0) list.Add((fragment, size.Width, size.Height));
        }
        if (theme.Skin?.Window is { } window)
        {
            Add(window.Border.Url, window.Border.SourceSize);
            foreach (var overlay in window.Overlays)
                Add(overlay.Url, new SkinPixelSize((int)overlay.Size.Width, (int)overlay.Size.Height));
        }
        if (theme.Skin?.Panels?.Default is { } panel) Add(panel.Border.Url, panel.Border.SourceSize);
        return list;
    }

    internal static int OverlayCount(WorldTheme theme) => theme.Skin?.Window?.Overlays.Count ?? 0;
}
