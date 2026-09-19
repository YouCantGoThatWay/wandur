using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Wandur.Core.Discovery;

namespace Wandur.Desktop;

internal static class ThemeMaterials
{
    /// <summary>
    /// Textured or metallic surfaces replace whole brushes, so they are applied only when the theme asks
    /// for them. A plain theme keeps the solid brushes <see cref="ThemeService"/> already updated in place.
    /// </summary>
    public static void Apply(ThemeResources resources, WorldTheme? theme, IReadOnlyDictionary<string, Bitmap>? images, string panelColor)
    {
        var chromeImage = images?.GetValueOrDefault("chrome");
        var shellImage = images?.GetValueOrDefault("shell");
        var metallic = theme?.Surface == "metallic";
        IBrush chrome = resources.Read("PanelBrush");
        if (metallic)
        {
            var source = Color.Parse(theme!.Colors.Panel);
            var gray = (byte)((source.R + source.G + source.B) / 3);
            var color = Color.FromRgb((byte)((gray * 3 + source.R) / 4),
                (byte)((gray * 3 + source.G) / 4), (byte)((gray * 3 + source.B) / 4));
            Color Highlight(double amount) => Color.FromRgb(
                (byte)(color.R + (255 - color.R) * amount),
                (byte)(color.G + (255 - color.G) * amount),
                (byte)(color.B + (255 - color.B) * amount));
            chrome = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                [
                    new GradientStop(Highlight(.18), 0),
                    new GradientStop(Highlight(.09), .04),
                    new GradientStop(Highlight(.035), .55),
                    new GradientStop(color, .96),
                    new GradientStop(Highlight(.08), 1)
                ]
            };
        }
        var textured = chromeImage is not null || metallic;
        if (textured) chrome = Overlay(chrome, chromeImage, theme?.Images?.Chrome?.Opacity ?? .12, height: 64);
        // ThemeService has already restored every solid surface, so only textures are written here.
        if (shellImage is not null)
        {
            var shell = Overlay(resources.Read("ShellBrush"), shellImage, theme?.Images?.Shell?.Opacity ?? .12);
            foreach (var key in new[] { "ShellBrush", "DockSurfacePanelBrush", "DockSurfaceSidebarBrush", "DockSurfaceWorkbenchBrush", "DockWindowChromeBackgroundBrush" })
                resources.Brush(key, shell);
        }
        if (textured)
        {
            resources.Brush("ChromeBrush", chrome);
            resources.Brush("DockHeaderBrush", chrome);
            foreach (var key in new[] { "DockSurfaceHeaderBrush", "DockSurfaceHeaderActiveBrush", "DockWindowChromeTitleBarBackgroundBrush", "DockDocumentTabStripBackgroundBrush" })
                resources.Brush(key, chrome);
        }
        else
        {
            resources.Color("ChromeBrush", panelColor);
            resources.Brush("DockHeaderBrush", resources.Read("ButtonFaceBrush"));
        }
    }

    private static IBrush Overlay(IBrush surface, Bitmap? bitmap, double opacity, double height = 256)
    {
        var group = new DrawingGroup();
        // Crop the image to a header-height strip instead of squeezing the entire
        // square texture into a toolbar, which erases its fine horizontal grain.
        var rect = new RectangleGeometry(new Rect(0, 0, 256, height));
        group.Children.Add(new GeometryDrawing { Brush = surface, Geometry = rect });
        if (bitmap is not null)
            group.Children.Add(new GeometryDrawing { Geometry = rect, Brush = new ImageBrush(bitmap)
            { Opacity = opacity, TileMode = TileMode.Tile, DestinationRect = new RelativeRect(0, 0, 256, 256, RelativeUnit.Absolute), Stretch = Stretch.Fill } });
        else
            for (var y = 1; y < height; y += 3)
                group.Children.Add(new GeometryDrawing { Brush = new SolidColorBrush(Color.FromArgb((byte)(y % 2 == 0 ? 5 : 3), 255, 255, 255)),
                    Geometry = new RectangleGeometry(new Rect(0, y, 256, .5)) });
        return new DrawingBrush(group) { Stretch = Stretch.Fill };
    }
}
