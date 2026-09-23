using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Wandur.Core.Discovery;

namespace Wandur.Desktop;

/// <summary>
/// Fixed-size window ornaments drawn above the live shell. Never hit-tests or takes focus.
/// </summary>
internal sealed class ThemeOrnamentLayer : Control
{
    public static readonly StyledProperty<bool> IsCompactProperty =
        AvaloniaProperty.Register<ThemeOrnamentLayer, bool>(nameof(IsCompact));

    private IReadOnlyList<Ornament> _ornaments = [];
    private SkinFooterClearance _configuredClearance;
    private SkinSize? _compactBelow;

    public bool IsCompact
    {
        get => GetValue(IsCompactProperty);
        private set => SetValue(IsCompactProperty, value);
    }

    /// <summary>Footer padding contributed by visible bottom ornaments; zero in compact mode.</summary>
    public SkinFooterClearance EffectiveFooterClearance { get; private set; }

    public event Action? ClearanceChanged;

    static ThemeOrnamentLayer()
    {
        AffectsRender<ThemeOrnamentLayer>(IsCompactProperty);
        FocusableProperty.OverrideDefaultValue<ThemeOrnamentLayer>(false);
    }

    public ThemeOrnamentLayer()
    {
        IsHitTestVisible = false;
        Focusable = false;
    }

    public void ApplyFromTheme()
    {
        _ornaments = [];
        _configuredClearance = default;
        _compactBelow = null;
        var theme = ThemeService.AppliedWorldTheme;
        var skin = ThemeSkinResources.FromApplied();
        if (theme?.Skin?.Window is { } window && skin is not null)
        {
            _configuredClearance = window.FooterClearance;
            _compactBelow = window.CompactBelow;
            var list = new List<Ornament>();
            foreach (var (id, entry) in skin.Overlays)
            {
                list.Add(new Ornament(id, entry.Overlay.Anchor, entry.Overlay.Size, entry.Bitmap));
            }
            _ornaments = list;
        }
        UpdateCompactAndClearance();
        InvalidateVisual();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateCompactAndClearance();
    }

    private void UpdateCompactAndClearance()
    {
        var compact = false;
        if (_compactBelow is { } threshold)
        {
            // Use host bounds once, before ornament clearance shrinks content, to avoid oscillation.
            compact = Bounds.Width > 0 && Bounds.Height > 0 &&
                      (Bounds.Width < threshold.Width || Bounds.Height < threshold.Height);
        }
        IsCompact = compact;

        SkinFooterClearance clearance = default;
        if (!compact)
        {
            var left = _ornaments.Any(o => o.Anchor == "bottom-left") ? _configuredClearance.Left : 0;
            var right = _ornaments.Any(o => o.Anchor == "bottom-right") ? _configuredClearance.Right : 0;
            var minHeight = _ornaments.Any(o => o.Anchor is "bottom-left" or "bottom-right")
                ? _configuredClearance.MinHeight : 0;
            clearance = new SkinFooterClearance(left, right, minHeight);
        }

        if (clearance != EffectiveFooterClearance)
        {
            EffectiveFooterClearance = clearance;
            ClearanceChanged?.Invoke();
        }
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (IsCompact || _ornaments.Count == 0) return;
        var host = Bounds.Size;
        if (host.Width <= 0 || host.Height <= 0) return;
        foreach (var ornament in _ornaments)
        {
            var bounds = AnchorBounds(ornament.Anchor, ornament.Size, host);
            if (bounds.Width <= 0 || bounds.Height <= 0) continue;
            var src = new Rect(0, 0, ornament.Bitmap.PixelSize.Width, ornament.Bitmap.PixelSize.Height);
            context.DrawImage(ornament.Bitmap, src, bounds);
        }
    }

    internal static Rect AnchorBounds(string anchor, SkinSize size, Size host)
    {
        var width = size.Width;
        var height = size.Height;
        var x = anchor switch
        {
            "bottom-left" => 0d,
            "bottom-right" => host.Width - width,
            "top-center" => (host.Width - width) / 2,
            _ => throw new InvalidOperationException($"Unknown ornament anchor '{anchor}'.")
        };
        var y = anchor == "top-center" ? 0d : host.Height - height;
        return new Rect(x, y, width, height);
    }

    private readonly record struct Ornament(string Id, string Anchor, SkinSize Size, Bitmap Bitmap);
}
