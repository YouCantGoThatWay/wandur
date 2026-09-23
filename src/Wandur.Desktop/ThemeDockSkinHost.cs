using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Wandur.Core.Discovery;

namespace Wandur.Desktop;

/// <summary>
/// Nine-slice panel chrome around one ToolDock's live header and body.
/// Passthrough when no panel skin bitmap is ready.
/// </summary>
public sealed class ThemeDockSkinHost : Decorator
{
    public static readonly StyledProperty<bool> IsSkinActiveProperty =
        AvaloniaProperty.Register<ThemeDockSkinHost, bool>(nameof(IsSkinActive));
    public static readonly StyledProperty<Bitmap?> BorderBitmapProperty =
        AvaloniaProperty.Register<ThemeDockSkinHost, Bitmap?>(nameof(BorderBitmap));
    public static readonly StyledProperty<WorldThemeSkinBorder?> BorderMetaProperty =
        AvaloniaProperty.Register<ThemeDockSkinHost, WorldThemeSkinBorder?>(nameof(BorderMeta));
    public static readonly StyledProperty<Thickness> InsetProperty =
        AvaloniaProperty.Register<ThemeDockSkinHost, Thickness>(nameof(Inset));
    public static readonly StyledProperty<double> HeaderHeightProperty =
        AvaloniaProperty.Register<ThemeDockSkinHost, double>(nameof(HeaderHeight), 32);

    public bool IsSkinActive
    {
        get => GetValue(IsSkinActiveProperty);
        private set => SetValue(IsSkinActiveProperty, value);
    }
    public Bitmap? BorderBitmap
    {
        get => GetValue(BorderBitmapProperty);
        private set => SetValue(BorderBitmapProperty, value);
    }
    public WorldThemeSkinBorder? BorderMeta
    {
        get => GetValue(BorderMetaProperty);
        private set => SetValue(BorderMetaProperty, value);
    }
    public Thickness Inset
    {
        get => GetValue(InsetProperty);
        private set => SetValue(InsetProperty, value);
    }
    public double HeaderHeight
    {
        get => GetValue(HeaderHeightProperty);
        private set => SetValue(HeaderHeightProperty, value);
    }

    static ThemeDockSkinHost()
    {
        AffectsRender<ThemeDockSkinHost>(BorderBitmapProperty, BorderMetaProperty, InsetProperty, IsSkinActiveProperty);
        AffectsMeasure<ThemeDockSkinHost>(BorderBitmapProperty, InsetProperty, IsSkinActiveProperty);
        AffectsArrange<ThemeDockSkinHost>(BorderBitmapProperty, InsetProperty, IsSkinActiveProperty);
    }

    public ThemeDockSkinHost()
    {
        DetachedFromVisualTree += (_, _) => ThemeService.Applied -= OnThemeApplied;
        AttachedToVisualTree += (_, _) => { ThemeService.Applied += OnThemeApplied; OnThemeApplied(); };
    }

    private bool _fleet;
    private Thickness ActiveInset => IsSkinActive ? Inset : _fleet ? new Thickness(3) : default;

    /// <summary>
    /// The shape each panel border was templated with. The corners come through a converter on a binding
    /// over the dock's alignment, which squares the edge that meets the window side; a binding does not
    /// re-run because a theme moved a radius, so the shape is re-derived here from the pattern the
    /// template produced. Keeping the original means a theme can square the corners and a later one can
    /// round them again, instead of squaring being one-way.
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Border, object> Templated = new();

    private void ApplyPanelRadius()
    {
        var radius = ThemeService.AppliedSkin?.Radii?.Panel;
        if (radius is null) return;
        foreach (var border in this.GetVisualDescendants().OfType<Border>().Where(b => b.Name == "PART_Border"))
        {
            var original = (CornerRadius)Templated.GetValue(border, b => b.CornerRadius);
            border.CornerRadius = new CornerRadius(
                original.TopLeft > 0 ? radius.Value : 0,
                original.TopRight > 0 ? radius.Value : 0,
                original.BottomRight > 0 ? radius.Value : 0,
                original.BottomLeft > 0 ? radius.Value : 0);
        }
    }

    private void OnThemeApplied()
    {
        _fleet = FleetSkin.IsActive;
        Classes.Set("fleet", _fleet);
        ApplyPanelRadius();
        var skin = ThemeSkinResources.FromApplied();
        if (skin is not { PanelReady: true } || skin.PanelMeta is not { } meta)
        {
            ClearSkin();
            return;
        }
        BorderBitmap = skin.PanelDefault;
        BorderMeta = meta.Border;
        Inset = new Thickness(meta.Inset.Left, meta.Inset.Top, meta.Inset.Right, meta.Inset.Bottom);
        HeaderHeight = meta.HeaderHeight;
        IsSkinActive = true;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void ClearSkin()
    {
        BorderBitmap = null;
        BorderMeta = null;
        Inset = default;
        IsSkinActive = false;
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var inset = ActiveInset;
        // Fall back to plain chrome when the dock cannot fit its border + header.
        if (IsSkinActive && !CanFit(availableSize, inset))
        {
            Child?.Measure(availableSize);
            return Child?.DesiredSize ?? default;
        }
        var width = double.IsInfinity(availableSize.Width) ? availableSize.Width : Math.Max(0, availableSize.Width - inset.Left - inset.Right);
        var height = double.IsInfinity(availableSize.Height) ? availableSize.Height : Math.Max(0, availableSize.Height - inset.Top - inset.Bottom);
        Child?.Measure(new Size(width, height));
        var desired = Child?.DesiredSize ?? default;
        return new Size(desired.Width + inset.Left + inset.Right, desired.Height + inset.Top + inset.Bottom);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var inset = ActiveInset;
        if (IsSkinActive && !CanFit(finalSize, inset))
        {
            Child?.Arrange(new Rect(finalSize));
            return finalSize;
        }
        Child?.Arrange(new Rect(inset.Left, inset.Top,
            Math.Max(0, finalSize.Width - inset.Left - inset.Right),
            Math.Max(0, finalSize.Height - inset.Top - inset.Bottom)));
        return finalSize;
    }

    private bool CanFit(Size size, Thickness inset)
    {
        if (BorderMeta is null) return false;
        var t = BorderMeta.Thickness;
        var minW = t.Left + t.Right;
        var minH = t.Top + t.Bottom;
        if (!double.IsInfinity(size.Width) && size.Width < minW) return false;
        if (!double.IsInfinity(size.Height) && size.Height < minH + HeaderHeight) return false;
        if (!double.IsInfinity(size.Width) && size.Width < inset.Left + inset.Right) return false;
        if (!double.IsInfinity(size.Height) && size.Height < inset.Top + inset.Bottom + HeaderHeight) return false;
        return true;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_fleet && !IsSkinActive && Bounds.Width > 6 && Bounds.Height > 6)
        {
            context.DrawRectangle(FleetSkin.Metal, new Pen(Brush.Parse("#424743"), 1), new Rect(Bounds.Size).Deflate(.5), 3, 3);
            context.DrawRectangle(null, new Pen(Brush.Parse("#F3F3EC"), 1), new Rect(Bounds.Size).Deflate(1.5), 2, 2);
            context.DrawRectangle(null, new Pen(Brush.Parse("#687572"), 1), new Rect(Bounds.Size).Deflate(2.5), 1, 1);
        }
        if (!IsSkinActive || BorderBitmap is not { } bitmap || BorderMeta is not { } meta) return;
        var size = Bounds.Size;
        if (!CanFit(size, Inset)) return;
        var patches = ThemeNineSlice.Build(bitmap.PixelSize, meta.Slice, meta.Thickness, size, meta.Tiles);
        ThemeNineSlice.Draw(context, bitmap, patches);
    }
}
