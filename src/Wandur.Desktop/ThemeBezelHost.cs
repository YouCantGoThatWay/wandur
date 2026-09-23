using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Wandur.Core.Discovery;

namespace Wandur.Desktop;

/// <summary>
/// Nine-slice bezel around the window shell. With no border bitmap it is a transparent passthrough
/// so palette-only themes keep today's layout.
/// </summary>
internal sealed class ThemeBezelHost : Decorator
{
    public static readonly StyledProperty<Bitmap?> BorderBitmapProperty =
        AvaloniaProperty.Register<ThemeBezelHost, Bitmap?>(nameof(BorderBitmap));
    public static readonly StyledProperty<WorldThemeFrameInsets> SliceProperty =
        AvaloniaProperty.Register<ThemeBezelHost, WorldThemeFrameInsets>(nameof(Slice), new WorldThemeFrameInsets());
    public static readonly StyledProperty<Thickness> InsetProperty =
        AvaloniaProperty.Register<ThemeBezelHost, Thickness>(nameof(Inset));

    public Bitmap? BorderBitmap
    {
        get => GetValue(BorderBitmapProperty);
        set => SetValue(BorderBitmapProperty, value);
    }
    public WorldThemeFrameInsets Slice
    {
        get => GetValue(SliceProperty);
        set => SetValue(SliceProperty, value);
    }
    public Thickness Inset
    {
        get => GetValue(InsetProperty);
        set => SetValue(InsetProperty, value);
    }

    static ThemeBezelHost()
    {
        AffectsRender<ThemeBezelHost>(BorderBitmapProperty, SliceProperty, InsetProperty);
        AffectsMeasure<ThemeBezelHost>(BorderBitmapProperty, InsetProperty);
        AffectsArrange<ThemeBezelHost>(BorderBitmapProperty, InsetProperty);
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
        if (BorderBitmap is not { } bitmap) return;
        var size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) return;
        var patches = ThemeNineSlice.BuildLegacy(bitmap.PixelSize, Slice, size);
        ThemeNineSlice.Draw(context, bitmap, patches);
    }

    /// <summary>Applies or clears the bezel from the theme currently painted by <see cref="ThemeService"/>.</summary>
    public void ApplyFromTheme()
    {
        var theme = ThemeService.AppliedWorldTheme;
        // Prefer modular window skin when ready; legacy bezel must not stack with it. A skin that names no
        // window at all has decided against a frame, so there is nothing for the bezel to stand in for.
        if (FleetSkin.IsActive || ThemeSkinResources.FromApplied() is { WindowReady: true } ||
            theme?.Skin is { HasContent: true, Window: null })
        {
            BorderBitmap = null;
            Inset = default;
            if (Child is Border clip) clip.CornerRadius = default;
            InvalidateMeasure();
            InvalidateVisual();
            return;
        }
        var frame = theme?.Frame is { IsValid: true } f ? f : null;
        var bitmap = frame is not null ? ThemeService.AppliedImages?.GetValueOrDefault("frame-border") : null;
        if (frame is null || bitmap is null)
        {
            BorderBitmap = null;
            Inset = default;
            if (Child is Border clip) clip.CornerRadius = default;
            InvalidateMeasure();
            InvalidateVisual();
            return;
        }
        BorderBitmap = bitmap;
        var inset = frame.EffectiveInset;
        Inset = new Thickness(inset.Left, inset.Top, inset.Right, inset.Bottom);
        Slice = frame.Assets!.Border!.Slice;
        var radius = frame.EffectiveContentRadius(theme!.CornerRadius);
        if (Child is Border clipChild) clipChild.CornerRadius = new CornerRadius(radius);
        InvalidateMeasure();
        InvalidateVisual();
    }
}
