using System.Buffers.Binary;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Wandur.Core.Discovery;

namespace Wandur.Desktop;

public sealed partial class SessionWorkspace
{
    private WorldTheme? _imageTheme;
    private string? _imageThemeKey;
    private CancellationTokenSource? _imageRequest;
    private Dictionary<string, Bitmap>? _themeImages;

    private const int MaxSkinUrls = 5;
    private const long MaxSkinPngBytes = 16L * 1024 * 1024;
    private const long MaxSkinRgbaBytes = 64L * 1024 * 1024;

    private Dictionary<string, Bitmap>? PrepareThemeImages(WorldTheme? theme)
    {
        var key = ThemeImageKey(theme);
        if (key == _imageThemeKey && Equals(theme, _imageTheme)) return null;
        _imageRequest?.Cancel(); _imageRequest?.Dispose(); _imageRequest = null;
        var previous = _themeImages;
        _themeImages = null; _imageTheme = theme; _imageThemeKey = key;
        var needsImages = theme is not null && (
            theme.Images.Chrome is { IsValid: true } || theme.Images.Shell is { IsValid: true } ||
            theme.Frame is { IsValid: true, Assets.Border.IsValid: true } ||
            theme.Skin is { HasContent: true });
        if (_catalog is not null && needsImages)
        {
            var request = _imageRequest = new CancellationTokenSource();
            var token = request.Token;
            Dispatcher.UIThread.Post(async () => await LoadThemeImagesAsync(theme!, token));
        }
        return previous;
    }

    private static string? ThemeImageKey(WorldTheme? theme)
    {
        if (theme is null) return null;
        var skin = theme.Skin is { HasContent: true } s ? WorldThemeSkinJson.CanonicalKey(s) : "-";
        var frame = theme.Frame is { IsValid: true, Assets.Border: { IsValid: true } b } ? b.Url : "-";
        var chrome = theme.Images.Chrome is { IsValid: true } c ? c.Url : "-";
        var shell = theme.Images.Shell is { IsValid: true } s2 ? s2.Url : "-";
        return theme.Id + "|" + skin + "|" + frame + "|" + chrome + "|" + shell;
    }

    private async Task LoadThemeImagesAsync(WorldTheme theme, CancellationToken token)
    {
        var decoded = new Dictionary<string, Bitmap>(StringComparer.Ordinal);
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
                    var width = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
                    decoded[key] = Bitmap.DecodeToWidth(stream, Math.Min(width, 1024));
                }
                catch (Exception e) when (e is not OutOfMemoryException) { if (token.IsCancellationRequested) return; }
            }

            var windowSkinReady = false;
            if (theme.Skin is { HasContent: true } skin)
                windowSkinReady = await LoadSkinImagesAsync(skin, decoded, token);
            Wandur.Core.Diagnostics.ThemeTrace.Write("skin.ready",
                $"theme={theme.Id} hasSkin={theme.Skin is { HasContent: true }} windowSkinReady={windowSkinReady} " +
                $"declinesFrame={theme.Skin is { HasContent: true, Window: null }} " +
                $"legacyBezelWillPaint={(!windowSkinReady && theme.Skin is not { HasContent: true, Window: null } && theme.Frame is { IsValid: true, Assets.Border.IsValid: true })}");

            // Legacy bezel only when the modular window border was wanted and did not load. A skin that has
            // content but names no window is not a failure to fall back from: it is a theme saying it wants
            // no frame, and painting the old bezel over it puts back the very thing it removed.
            var declinesAFrame = theme.Skin is { HasContent: true, Window: null };
            if (!windowSkinReady && !declinesAFrame &&
                theme.Frame is { IsValid: true, Assets.Border: { IsValid: true } border })
            {
                try
                {
                    var image = new WorldThemeImage { Url = border.Url, Opacity = 0 };
                    var bytes = await _catalog!.GetThemeImageAsync(image, token);
                    if (bytes is not null)
                    {
                        using var stream = new MemoryStream(bytes);
                        var width = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
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
        finally { DisposeBitmaps(decoded); }
    }

    private async Task<bool> LoadSkinImagesAsync(
        WorldThemeSkin skin, Dictionary<string, Bitmap> decoded, CancellationToken token)
    {
        var jobs = EnumerateSkinJobs(skin);
        if (jobs.Count == 0) return false;

        // Deduplicate by resolved relative URL; panels share one bitmap copy.
        var byUrl = new Dictionary<string, List<SkinImageJob>>(StringComparer.Ordinal);
        foreach (var job in jobs)
        {
            if (!byUrl.TryGetValue(job.Url, out var list))
                byUrl[job.Url] = list = [];
            list.Add(job);
        }
        if (byUrl.Count > MaxSkinUrls) return false;

        long pngBytes = 0, rgbaBytes = 0;
        var urlBitmaps = new Dictionary<string, Bitmap?>(StringComparer.Ordinal);
        using var gate = new SemaphoreSlim(2, 2);
        try
        {
            var loadTasks = byUrl.Keys.Select(async url =>
            {
                await gate.WaitAsync(token);
                try
                {
                    if (token.IsCancellationRequested) return;
                    var sample = byUrl[url][0];
                    var bytes = await _catalog!.GetThemeImageAsync(
                        new WorldThemeImage { Url = url, Version = sample.Version, Opacity = 0 }, token);
                    if (bytes is null)
                    {
                        Wandur.Core.Diagnostics.ThemeTrace.Write("skin.image", $"{url} REJECTED: fetch returned nothing");
                        urlBitmaps[url] = null; return;
                    }
                    if (Interlocked.Add(ref pngBytes, bytes.Length) > MaxSkinPngBytes)
                    { urlBitmaps[url] = null; return; }

                    var width = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
                    var height = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
                    if (sample.ExpectedPixels is { } expected &&
                        (width != expected.Width || height != expected.Height))
                    {
                        Wandur.Core.Diagnostics.ThemeTrace.Write("skin.image",
                            $"{url} REJECTED: {width}x{height} but source_size declares {expected.Width}x{expected.Height}");
                        urlBitmaps[url] = null; return;
                    }

                    if (sample.DestSize is { } dest && !AspectMatches(width, height, dest.Width, dest.Height))
                    {
                        Wandur.Core.Diagnostics.ThemeTrace.Write("skin.image",
                            $"{url} REJECTED: {width}x{height} aspect does not match declared size {dest.Width}x{dest.Height}");
                        urlBitmaps[url] = null; return;
                    }
                    Wandur.Core.Diagnostics.ThemeTrace.Write("skin.image", $"{url} ok {width}x{height}");

                    var rgba = (long)width * height * 4;
                    if (Interlocked.Add(ref rgbaBytes, rgba) > MaxSkinRgbaBytes)
                    { urlBitmaps[url] = null; return; }

                    using var stream = new MemoryStream(bytes);
                    // Full-resolution decode so nine-slice source rects match source_size metadata.
                    urlBitmaps[url] = new Bitmap(stream);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    urlBitmaps[url] = null;
                }
                finally { try { gate.Release(); } catch (ObjectDisposedException) { } }
            });
            await Task.WhenAll(loadTasks);
        }
        catch (OperationCanceledException) { return false; }
        if (token.IsCancellationRequested) return false;

        foreach (var (url, list) in byUrl)
        {
            if (!urlBitmaps.TryGetValue(url, out var bitmap) || bitmap is null) continue;
            foreach (var job in list)
                decoded[job.Key] = bitmap;
        }

        return decoded.ContainsKey(ThemeSkinResources.WindowBorderKey);
    }

    private static List<SkinImageJob> EnumerateSkinJobs(WorldThemeSkin skin)
    {
        var jobs = new List<SkinImageJob>();
        if (skin.Window is { } window)
        {
            jobs.Add(new SkinImageJob(
                ThemeSkinResources.WindowBorderKey, window.Border.Url, window.Border.Version, window.Border.SourceSize, null));
            foreach (var overlay in window.Overlays)
            {
                // Overlay PNGs are not required to declare source_size; aspect is checked against dest size.
                jobs.Add(new SkinImageJob(
                    ThemeSkinResources.OverlayKey(overlay.Id), overlay.Url, overlay.Version, null, overlay.Size));
            }
        }
        if (skin.Panels?.Default is { } panel)
        {
            jobs.Add(new SkinImageJob(
                ThemeSkinResources.PanelDefaultKey, panel.Border.Url, panel.Border.Version, panel.Border.SourceSize, null));
        }
        return jobs;
    }

    private static bool AspectMatches(int pixelW, int pixelH, double destW, double destH)
    {
        if (pixelW <= 0 || pixelH <= 0 || destW <= 0 || destH <= 0) return false;
        var src = (double)pixelW / pixelH;
        var dest = destW / destH;
        return Math.Abs(src - dest) / dest <= 0.01;
    }

    private void DisposeThemeImages()
    {
        _imageRequest?.Cancel(); _imageRequest?.Dispose(); _imageRequest = null;
        DisposeBitmaps(_themeImages);
        _themeImages = null;
        _imageTheme = null;
        _imageThemeKey = null;
    }

    private static void DisposeBitmaps(Dictionary<string, Bitmap>? images)
    {
        if (images is null) return;
        foreach (var bitmap in images.Values.Distinct()) bitmap.Dispose();
    }

    private readonly record struct SkinImageJob(
        string Key, string Url, string? Version, SkinPixelSize? ExpectedPixels, SkinSize? DestSize);
}
