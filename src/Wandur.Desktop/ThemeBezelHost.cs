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
        var pixel = bitmap.PixelSize;
        var sw = (double)pixel.Width;
        var sh = (double)pixel.Height;
        if (sw <= 0 || sh <= 0) return;
        var slice = Slice;
        var left = Math.Min(slice.Left, sw);
        var top = Math.Min(slice.Top, sh);
        var right = Math.Min(slice.Right, Math.Max(0, sw - left));
        var bottom = Math.Min(slice.Bottom, Math.Max(0, sh - top));
        // Keep a stretchable centre when the declared slice would cover the whole bitmap.
        if (left + right >= sw) { left = Math.Min(left, sw / 3); right = Math.Min(right, sw - left); }
        if (top + bottom >= sh) { top = Math.Min(top, sh / 3); bottom = Math.Min(bottom, sh - top); }
        var midW = Math.Max(0, sw - left - right);
        var midH = Math.Max(0, sh - top - bottom);
        var destL = Math.Min(left, size.Width / 2);
        var destT = Math.Min(top, size.Height / 2);
        var destR = Math.Min(right, Math.Max(0, size.Width - destL));
        var destB = Math.Min(bottom, Math.Max(0, size.Height - destT));
        var destMidW = Math.Max(0, size.Width - destL - destR);
        var destMidH = Math.Max(0, size.Height - destT - destB);

        void Draw(Rect source, Rect dest)
        {
            if (source.Width <= 0 || source.Height <= 0 || dest.Width <= 0 || dest.Height <= 0) return;
            context.DrawImage(bitmap, source, dest);
        }

        Draw(new Rect(0, 0, left, top), new Rect(0, 0, destL, destT));
        Draw(new Rect(sw - right, 0, right, top), new Rect(size.Width - destR, 0, destR, destT));
        Draw(new Rect(0, sh - bottom, left, bottom), new Rect(0, size.Height - destB, destL, destB));
        Draw(new Rect(sw - right, sh - bottom, right, bottom), new Rect(size.Width - destR, size.Height - destB, destR, destB));
        Draw(new Rect(left, 0, midW, top), new Rect(destL, 0, destMidW, destT));
        Draw(new Rect(left, sh - bottom, midW, bottom), new Rect(destL, size.Height - destB, destMidW, destB));
        Draw(new Rect(0, top, left, midH), new Rect(0, destT, destL, destMidH));
        Draw(new Rect(sw - right, top, right, midH), new Rect(size.Width - destR, destT, destR, destMidH));
    }

    /// <summary>Applies or clears the bezel from the theme currently painted by <see cref="ThemeService"/>.</summary>
    public void ApplyFromTheme()
    {
        var theme = ThemeService.AppliedWorldTheme;
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
