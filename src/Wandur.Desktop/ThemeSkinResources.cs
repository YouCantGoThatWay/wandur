using Avalonia.Media.Imaging;
using Wandur.Core.Discovery;

namespace Wandur.Desktop;

/// <summary>
/// Read-only view of ready skin bitmaps. SessionWorkspace owns the bitmaps; this type does not dispose them.
/// </summary>
internal sealed class ThemeSkinResources
{
    public const string WindowBorderKey = "skin-window-border";
    public const string PanelDefaultKey = "skin-panel-default";
    public const string OverlayKeyPrefix = "skin-overlay:";

    public static string OverlayKey(string id) => OverlayKeyPrefix + id;

    public Bitmap? WindowBorder { get; init; }
    public WorldThemeSkinBorder? WindowBorderMeta { get; init; }
    public Bitmap? PanelDefault { get; init; }
    public WorldThemePanelSkin? PanelMeta { get; init; }
    public IReadOnlyDictionary<string, (Bitmap Bitmap, WorldThemeSkinOverlay Overlay)> Overlays { get; init; } =
        new Dictionary<string, (Bitmap, WorldThemeSkinOverlay)>();

    public bool WindowReady => WindowBorder is not null && WindowBorderMeta is not null;
    public bool PanelReady => PanelDefault is not null && PanelMeta is not null;

    public static ThemeSkinResources? FromApplied()
    {
        var theme = ThemeService.AppliedWorldTheme;
        var images = ThemeService.AppliedImages;
        if (theme?.Skin is not { HasContent: true } skin || images is null) return null;
        return From(skin, images);
    }

    public static ThemeSkinResources From(WorldThemeSkin skin, IReadOnlyDictionary<string, Bitmap> images)
    {
        Bitmap? window = null;
        WorldThemeSkinBorder? windowMeta = null;
        if (skin.Window is { } w && images.TryGetValue(WindowBorderKey, out var wb))
        {
            window = wb;
            windowMeta = w.Border;
        }

        Bitmap? panel = null;
        WorldThemePanelSkin? panelMeta = null;
        if (skin.Panels?.Default is { } p && images.TryGetValue(PanelDefaultKey, out var pb))
        {
            panel = pb;
            panelMeta = p;
        }

        var overlays = new Dictionary<string, (Bitmap, WorldThemeSkinOverlay)>(StringComparer.Ordinal);
        if (skin.Window is { } win)
        {
            foreach (var overlay in win.Overlays)
            {
                var key = OverlayKey(overlay.Id);
                if (images.TryGetValue(key, out var bitmap))
                    overlays[overlay.Id] = (bitmap, overlay);
            }
        }

        return new ThemeSkinResources
        {
            WindowBorder = window,
            WindowBorderMeta = windowMeta,
            PanelDefault = panel,
            PanelMeta = panelMeta,
            Overlays = overlays
        };
    }
}
