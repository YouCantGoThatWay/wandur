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
    public static readonly StyledProperty<Bitmap?> BorderBitmapProperty =
        AvaloniaProperty.Register<ThemeWindowSkinHost, Bitmap?>(nameof(BorderBitmap));
    public static readonly StyledProperty<WorldThemeSkinBorder?> BorderMetaProperty =
        AvaloniaProperty.Register<ThemeWindowSkinHost, WorldThemeSkinBorder?>(nameof(BorderMeta));
    public static readonly StyledProperty<Thickness> InsetProperty =
        AvaloniaProperty.Register<ThemeWindowSkinHost, Thickness>(nameof(Inset));

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
        AffectsRender<ThemeWindowSkinHost>(BorderBitmapProperty, BorderMetaProperty, InsetProperty);
        AffectsMeasure<ThemeWindowSkinHost>(BorderBitmapProperty, InsetProperty);
        AffectsArrange<ThemeWindowSkinHost>(BorderBitmapProperty, InsetProperty);
    }

    private Thickness ActiveInset => BorderBitmap is null ? default : Inset;

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
        if (BorderBitmap is not { } bitmap || BorderMeta is not { } meta) return;
        var size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) return;
        var patches = ThemeNineSlice.Build(
            bitmap.PixelSize, meta.Slice, meta.Thickness, size);
        ThemeNineSlice.Draw(context, bitmap, patches);
    }

    public void ApplyFromTheme()
    {
        var skin = ThemeSkinResources.FromApplied();
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
