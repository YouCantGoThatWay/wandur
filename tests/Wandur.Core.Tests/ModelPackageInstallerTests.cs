using System.IO.Compression;
using System.Net;
using Wandur.Core.Classification;

namespace Wandur.Core.Tests;

public sealed class ModelPackageInstallerTests
{
    private static MemoryStream Zip(string packageDirectory, Action<ZipArchive>? extra = null)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.GetFiles(packageDirectory)) zip.CreateEntryFromFile(file, Path.GetFileName(file));
            extra?.Invoke(zip);
        }
        stream.Position = 0; return stream;
    }

    /// <summary>Probes for a free loopback port and starts a listener on it.</summary>
    internal static (HttpListener Listener, int Port) StartListener(int startPort = 40000, int endPort = 40200)
    {
        for (var attempt = startPort; attempt < endPort; attempt++)
        {
            var listener = new HttpListener();
            try
            {
                listener.Prefixes.Add($"http://127.0.0.1:{attempt}/");
                listener.Start();
                return (listener, attempt);
            }
            catch (HttpListenerException) { listener.Close(); }
        }
        throw new InvalidOperationException("No available loopback port found for the test listener.");
    }

    [Fact]
    public async Task InstallsVerifiedZipIntoVersionDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "wandur-models-" + Guid.NewGuid());
        var installer = new ModelPackageInstaller(root, new HttpClient());
        Assert.Null(installer.InstalledDirectory);
        var package = await installer.InstallAsync(Zip(ModelPackageTests.CreateFakePackage()), CancellationToken.None);
        Assert.Equal("9.9.9", package.Version);
        Assert.Equal(Path.Combine(root, "9.9.9"), installer.InstalledDirectory);
        Assert.Empty(Directory.GetDirectories(root, ".staging-*"));
    }

    [Fact]
    public async Task RejectsZipSlipAndTamperedContent()
    {
        var root = Path.Combine(Path.GetTempPath(), "wandur-models-" + Guid.NewGuid());
        var installer = new ModelPackageInstaller(root, new HttpClient());
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(Zip(ModelPackageTests.CreateFakePackage(), zip =>
        { using var writer = new StreamWriter(zip.CreateEntry("../escape.txt").Open()); writer.Write("x"); }), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(Zip(ModelPackageTests.CreateFakePackage(tamper: "head.json")), CancellationToken.None));
        Assert.Null(installer.InstalledDirectory);
        Assert.Empty(Directory.Exists(root) ? Directory.GetDirectories(root, ".staging-*") : []);
    }

    [Fact]
    public async Task RejectsPathTraversalViaManifestVersion()
    {
        var tempParent = Path.Combine(Path.GetTempPath(), "wandur-escape-" + Guid.NewGuid());
        Directory.CreateDirectory(tempParent);
        var root = Path.Combine(tempParent, "models");
        var escapeTarget = Path.Combine(tempParent, "escape-target");
        Directory.CreateDirectory(escapeTarget);
        var keep = Path.Combine(escapeTarget, "keep.txt");
        File.WriteAllText(keep, "keep-me");

        var installer = new ModelPackageInstaller(root, new HttpClient());
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            installer.InstallAsync(Zip(ModelPackageTests.CreateFakePackage(version: "../escape-target")), CancellationToken.None));

        Assert.True(File.Exists(keep));
        Assert.Equal("keep-me", File.ReadAllText(keep));
        Assert.Empty(Directory.Exists(root) ? Directory.GetDirectories(root, ".previous-*") : []);
        Assert.Empty(Directory.Exists(root) ? Directory.GetDirectories(root, ".staging-*") : []);
    }

    [Fact]
    public async Task DownloadsThroughHttpWithProgress()
    {
        var root = Path.Combine(Path.GetTempPath(), "wandur-models-" + Guid.NewGuid());
        var bytes = Zip(ModelPackageTests.CreateFakePackage()).ToArray();
        var (listener, port) = StartListener();
        using var _listener = listener;
        _ = Task.Run(async () => { var context = await listener.GetContextAsync(); context.Response.ContentLength64 = bytes.Length; await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close(); });
        var progress = new List<double>();
        var installer = new ModelPackageInstaller(root, new HttpClient());
        var package = await installer.DownloadAsync(new Uri($"http://127.0.0.1:{port}/model.zip"), new Progress<double>(progress.Add), CancellationToken.None);
        Assert.Equal("9.9.9", package.Version);
        await Task.Delay(50);
        Assert.Contains(progress, p => p >= 0.99);
        listener.Stop();
    }

    [Fact]
    public async Task FollowsRedirectsUpToTheCap()
    {
        var bytes = Zip(ModelPackageTests.CreateFakePackage()).ToArray();

        // A single redirect hop must succeed: listener A points at listener B, which serves the package.
        var (listenerB, portB) = StartListener();
        using var _listenerB = listenerB;
        _ = Task.Run(async () =>
        {
            var context = await listenerB.GetContextAsync();
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        });

        var (listenerA, portA) = StartListener();
        using var _listenerA = listenerA;
        _ = Task.Run(async () =>
        {
            var context = await listenerA.GetContextAsync();
            context.Response.StatusCode = (int)HttpStatusCode.Found;
            context.Response.Headers.Add("Location", $"http://127.0.0.1:{portB}/model.zip");
            context.Response.Close();
        });

        var root = Path.Combine(Path.GetTempPath(), "wandur-models-" + Guid.NewGuid());
        var installer = new ModelPackageInstaller(root, new HttpClient());
        var package = await installer.DownloadAsync(new Uri($"http://127.0.0.1:{portA}/model.zip"), null, CancellationToken.None);
        Assert.Equal("9.9.9", package.Version);
        listenerA.Stop();
        listenerB.Stop();

        // A listener that always redirects to itself must eventually be rejected once the depth cap is exceeded.
        var (loopListener, loopPort) = StartListener();
        using var _loopListener = loopListener;
        _ = Task.Run(async () =>
        {
            while (loopListener.IsListening)
            {
                HttpListenerContext context;
                try { context = await loopListener.GetContextAsync(); }
                catch (Exception) { break; }
                context.Response.StatusCode = (int)HttpStatusCode.Found;
                context.Response.Headers.Add("Location", $"http://127.0.0.1:{loopPort}/model.zip");
                context.Response.Close();
            }
        });
        var loopRoot = Path.Combine(Path.GetTempPath(), "wandur-models-" + Guid.NewGuid());
        var loopInstaller = new ModelPackageInstaller(loopRoot, new HttpClient());
        await Assert.ThrowsAsync<HttpRequestException>(() => loopInstaller.DownloadAsync(new Uri($"http://127.0.0.1:{loopPort}/model.zip"), null, CancellationToken.None));
        loopListener.Stop();
    }

    [Fact]
    public async Task RejectsExtractionExceedingSizeCapEvenWhenCompressedSmall()
    {
        var root = Path.Combine(Path.GetTempPath(), "wandur-models-" + Guid.NewGuid());
        var installer = new ModelPackageInstaller(root, new HttpClient());

        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("encoder.onnx", CompressionLevel.SmallestSize);
            await using var target = entry.Open();
            var buffer = new byte[1024 * 1024]; // zeros compress to almost nothing
            const long totalBytes = 210L * 1024 * 1024; // exceeds ModelPackageInstaller.MaxPackageBytes (200 MiB)
            for (long written = 0; written < totalBytes; written += buffer.Length)
            {
                var chunk = (int)Math.Min(buffer.Length, totalBytes - written);
                await target.WriteAsync(buffer.AsMemory(0, chunk));
            }
        }
        stream.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(stream, CancellationToken.None));
        Assert.Empty(Directory.Exists(root) ? Directory.GetDirectories(root, ".staging-*") : []);
    }
}
