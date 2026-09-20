using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Wandur.Core.Settings;
using Wandur.Desktop.Services;

namespace Wandur.Desktop.Views;

/// <summary>
/// The small picture beside a saved world: the directory's artwork letterboxed into a 40 by 30 tile in the
/// panel colour, or the world's initials in the same tile when there is none. The tile has its size from
/// the start, so a picture arriving later changes nothing around it.
/// </summary>
public sealed class WorldThumbnail : Border
{
    private readonly WorldThumbnails? _thumbnails;
    private readonly ConnectionProfile _profile;
    private readonly Image _image = new() { Name = "WorldThumbnailImage", Stretch = Stretch.Uniform, IsVisible = false, IsHitTestVisible = false };
    private readonly TextBlock _initials;
    private CancellationTokenSource? _load;

    public WorldThumbnail(ConnectionProfile profile, WorldThumbnails? thumbnails)
    {
        _profile = profile; _thumbnails = thumbnails;
        Name = "WorldThumbnail";
        Width = WorldThumbnails.Width; Height = WorldThumbnails.Height;
        MinWidth = Width; MinHeight = Height;
        CornerRadius = new CornerRadius(4);
        ClipToBounds = true;
        VerticalAlignment = VerticalAlignment.Center;
        Bind(BackgroundProperty, new DynamicResourceExtension("PanelBrush"));
        _initials = new TextBlock
        {
            Name = "WorldThumbnailInitials", Text = WorldThumbnails.Initials(profile.Name), FontSize = 12, FontWeight = FontWeight.Bold,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false
        };
        _initials.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AccentBrush"));
        Child = new Panel { Children = { _initials, _image } };
    }

    /// <summary>True once the directory's picture is on the tile rather than the initials.</summary>
    public bool ShowsArtwork => _image.IsVisible;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_thumbnails is null || _image.IsVisible) return;
        var load = _load = new CancellationTokenSource();
        Dispatcher.UIThread.Post(async () =>
        {
            var bitmap = await _thumbnails.GetAsync(_profile);
            if (bitmap is null || load.IsCancellationRequested) return;
            _image.Source = bitmap;
            _image.IsVisible = true;
            _initials.IsVisible = false;
        });
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _load?.Cancel(); _load?.Dispose(); _load = null;
        base.OnDetachedFromVisualTree(e);
    }
}
