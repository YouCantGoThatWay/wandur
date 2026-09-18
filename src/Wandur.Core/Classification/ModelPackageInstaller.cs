using System.IO.Compression;

namespace Wandur.Core.Classification;

/// <summary>Downloads/extracts a model package into &lt;modelsRoot&gt;/&lt;version&gt;/ after manifest verification.</summary>
public sealed class ModelPackageInstaller(string modelsRoot, HttpClient http)
{
    public const long MaxPackageBytes = 200L * 1024 * 1024;
    private const int MaxEntries = 64;

    public string ModelsRoot { get; } = modelsRoot;

    /// <summary>The highest version directory that loads and verifies, or null.</summary>
    public string? InstalledDirectory => LoadInstalled()?.Directory;

    /// <summary>The highest version package that loads and verifies, or null. Loads (and hashes) at most once per candidate.</summary>
    public ModelPackage? LoadInstalled()
    {
        if (!Directory.Exists(ModelsRoot)) return null;
        foreach (var dir in Directory.GetDirectories(ModelsRoot).Where(d => !Path.GetFileName(d).StartsWith('.'))
                     .OrderByDescending(d => Version.TryParse(Path.GetFileName(d), out var v) ? v : new Version(0, 0)))
        {
            try { return ModelPackage.Load(dir); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { }
        }
        return null;
    }

    public Task<ModelPackage> DownloadAsync(Uri url, IProgress<double>? progress, CancellationToken cancellation)
        => DownloadAsync(url, progress, cancellation, depth: 0);

    private async Task<ModelPackage> DownloadAsync(Uri url, IProgress<double>? progress, CancellationToken cancellation, int depth)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation);
        if (response.StatusCode is System.Net.HttpStatusCode.Redirect or System.Net.HttpStatusCode.Found or System.Net.HttpStatusCode.MovedPermanently or System.Net.HttpStatusCode.TemporaryRedirect or System.Net.HttpStatusCode.PermanentRedirect
            && response.Headers.Location is { } location)
        {
            if (depth >= 5) throw new HttpRequestException("Too many redirects.");
            var resolved = location.IsAbsoluteUri ? location : new Uri(url, location);
            if (string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) && string.Equals(resolved.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal))
                throw new HttpRequestException("Insecure redirect.");
            return await DownloadAsync(resolved, progress, cancellation, depth + 1); // the shared HttpClient disables auto-redirect
        }
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        if (total > MaxPackageBytes) throw new InvalidDataException("Package exceeds the size limit.");
        Directory.CreateDirectory(ModelsRoot);
        var temp = Path.Combine(ModelsRoot, ".download-" + Guid.NewGuid() + ".zip");
        try
        {
            await using (var file = File.Create(temp))
            await using (var body = await response.Content.ReadAsStreamAsync(cancellation))
            {
                var buffer = new byte[81920]; long read = 0; int count;
                while ((count = await body.ReadAsync(buffer, cancellation)) > 0)
                {
                    read += count;
                    if (read > MaxPackageBytes) throw new InvalidDataException("Package exceeds the size limit.");
                    await file.WriteAsync(buffer.AsMemory(0, count), cancellation);
                    if (total is > 0) progress?.Report(Math.Min(1, (double)read / total.Value));
                }
                progress?.Report(1);
            }
            await using var zip = File.OpenRead(temp);
            return await InstallAsync(zip, cancellation);
        }
        finally { try { File.Delete(temp); } catch (IOException) { } }
    }

    public async Task<ModelPackage> InstallAsync(Stream zip, CancellationToken cancellation)
    {
        Directory.CreateDirectory(ModelsRoot);
        var staging = Path.Combine(ModelsRoot, ".staging-" + Guid.NewGuid());
        Directory.CreateDirectory(staging);
        try
        {
            using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count > MaxEntries) throw new InvalidDataException("Too many package entries.");
            long extracted = 0;
            foreach (var entry in archive.Entries)
            {
                cancellation.ThrowIfCancellationRequested();
                var name = Path.GetFileName(entry.FullName);
                if (string.IsNullOrEmpty(name) || entry.FullName.Contains("..") || entry.FullName.Contains('/') || entry.FullName.Contains('\\') || name.StartsWith('.'))
                    throw new InvalidDataException($"Unsafe package entry {entry.FullName}.");
                await using var source = entry.Open();
                await using var target = File.Create(Path.Combine(staging, name));
                var buffer = new byte[81920]; int count;
                while ((count = await source.ReadAsync(buffer, cancellation)) > 0)
                {
                    extracted += count;
                    if (extracted > MaxPackageBytes) throw new InvalidDataException("Package exceeds the size limit.");
                    await target.WriteAsync(buffer.AsMemory(0, count), cancellation);
                }
            }
            var package = ModelPackage.Load(staging); // verifies hashes and structure
            var destination = Path.Combine(ModelsRoot, package.Version);
            var root = Path.GetFullPath(ModelsRoot).TrimEnd(Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(destination);
            if (Path.GetDirectoryName(full) != root || Path.GetFileName(full) != package.Version)
                throw new InvalidDataException("Unsafe package destination.");
            var previous = Directory.Exists(destination) ? Path.Combine(ModelsRoot, ".previous-" + Guid.NewGuid()) : null;
            if (previous is not null) Directory.Move(destination, previous);
            try { Directory.Move(staging, destination); }
            catch { if (previous is not null) Directory.Move(previous, destination); throw; }
            if (previous is not null) { try { Directory.Delete(previous, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            return ModelPackage.Load(destination);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException or KeyNotFoundException)
        { throw new InvalidDataException("Invalid package contents.", ex); }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
    }
}
