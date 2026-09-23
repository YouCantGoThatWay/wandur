using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Wandur.Core.Diagnostics;

namespace Wandur.Core.Discovery;

public sealed partial class WorldCatalog
{
    /// <summary>
    /// Theme artwork, cached under its url and the version the directory reports for it. Keying on the url
    /// alone pins the first copy ever fetched: art lives at a stable path and is replaced in place, so the
    /// directory would serve new art while the client kept painting the old one, indistinguishable from a
    /// theme that never changed. The version moves with the bytes, so replacing an asset simply produces a
    /// key nothing is stored under, and the previous copy stays valid for anyone still pointed at it.
    /// </summary>
    public async Task<byte[]?> GetThemeImageAsync(WorldThemeImage image, CancellationToken token = default)
    {
        if (!image.IsValid) return null;
        var uri = new Uri(BaseUri, image.Url);
        if (!BaseUri.IsBaseOf(uri)) return null;
        var identity = image.Version is { Length: > 0 } version ? uri.AbsoluteUri + "@" + version : uri.AbsoluteUri;
        var key = "theme-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        token.ThrowIfCancellationRequested();
        try { if (_cache.ReadArtwork(key) is { } cached && IsThemePng(cached)) return cached; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxThemeImageBytes)
        {
            ThemeTrace.Write("theme.image", $"{image.Url} HTTP {(int)response.StatusCode}, not cached");
            return null;
        }
        using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var data = new MemoryStream();
        var buffer = new byte[16_384];
        int read;
        while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0)
        {
            if (data.Length + read > MaxThemeImageBytes) return null;
            data.Write(buffer, 0, read);
        }
        var bytes = data.ToArray();
        if (!IsThemePng(bytes)) return null;
        ThemeTrace.Write("theme.image", $"{image.Url} fetched {bytes.Length} bytes at version {image.Version ?? "(none)"}");
        try { _cache.WriteArtwork(key, bytes); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return bytes;
    }

    private const int MaxThemeImageBytes = 4 * 1024 * 1024;
    private static bool IsThemePng(byte[] data)
    {
        if (data.Length is < 24 or > MaxThemeImageBytes || !data.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            !data.AsSpan(12, 4).SequenceEqual("IHDR"u8)) return false;
        var width = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(20, 4));
        return width is > 0 and <= 4096 && height is > 0 and <= 4096 && (long)width * height <= 8_388_608;
    }
}
