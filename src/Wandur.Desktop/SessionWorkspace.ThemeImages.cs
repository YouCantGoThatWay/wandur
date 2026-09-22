using System.Buffers.Binary;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Wandur.Core.Discovery;

namespace Wandur.Desktop;

public sealed partial class SessionWorkspace
{
    private WorldTheme? _imageTheme;
    private CancellationTokenSource? _imageRequest;
    private Dictionary<string, Bitmap>? _themeImages;

    private Dictionary<string, Bitmap>? PrepareThemeImages(WorldTheme? theme)
    {
        if (Equals(theme, _imageTheme)) return null;
        _imageRequest?.Cancel(); _imageRequest?.Dispose(); _imageRequest = null;
        var previous = _themeImages;
        _themeImages = null; _imageTheme = theme;
        var needsImages = theme is not null && (
            theme.Images.Chrome is { IsValid: true } || theme.Images.Shell is { IsValid: true } ||
            theme.Frame is { IsValid: true, Assets.Border.IsValid: true });
        if (_catalog is not null && needsImages)
        {
            var request = _imageRequest = new CancellationTokenSource();
            var token = request.Token;
            Dispatcher.UIThread.Post(async () => await LoadThemeImagesAsync(theme!, token));
        }
        return previous;
    }

    private async Task LoadThemeImagesAsync(WorldTheme theme, CancellationToken token)
    {
        var decoded = new Dictionary<string, Bitmap>();
        try
        {
            foreach (var (key, image) in new[] { ("chrome", theme.Images.Chrome), ("shell", theme.Images.Shell) })
            {
                if (image is not { IsValid: true }) continue;
                try
                {
                    var bytes = await _catalog!.GetThemeImageAsync(image, token);
                    if (bytes is null) continue;
                    using var stream = new MemoryStream(bytes);
                    // The catalog validates the PNG header and pixel limits. Never upscale:
                    // a narrow, tall texture could otherwise allocate an enormous bitmap.
                    var width = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
                    decoded[key] = Bitmap.DecodeToWidth(stream, Math.Min(width, 1024));
                }
                catch (Exception e) when (e is not OutOfMemoryException) { if (token.IsCancellationRequested) return; }
            }
            if (theme.Frame is { IsValid: true, Assets.Border: { IsValid: true } border })
            {
                try
                {
                    // Frame borders reuse the same URL and PNG rules as tiled chrome images.
                    var image = new WorldThemeImage { Url = border.Url, Opacity = 0 };
                    var bytes = await _catalog!.GetThemeImageAsync(image, token);
                    if (bytes is not null)
                    {
                        using var stream = new MemoryStream(bytes);
                        var width = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
                        // Bezels need full resolution for nine-slice corners; cap like chrome textures.
                        decoded["frame-border"] = Bitmap.DecodeToWidth(stream, Math.Min(width, 2048));
                    }
                }
                catch (Exception e) when (e is not OutOfMemoryException) { if (token.IsCancellationRequested) return; }
            }
            if (token.IsCancellationRequested || _disposed || !Equals(theme, _imageTheme)) return;
            _themeImages = decoded;
            ApplyAppearance();
            decoded = [];
        }
        finally { foreach (var bitmap in decoded.Values) bitmap.Dispose(); }
    }

    private void DisposeThemeImages()
    {
        _imageRequest?.Cancel(); _imageRequest?.Dispose(); _imageRequest = null;
        if (_themeImages is not null) foreach (var bitmap in _themeImages.Values) bitmap.Dispose();
        _themeImages = null;
    }
}
