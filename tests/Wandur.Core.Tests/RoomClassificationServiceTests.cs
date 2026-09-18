using System.IO.Compression;
using System.Net;
using Wandur.Core.Classification;
using Wandur.Core.Settings;

namespace Wandur.Core.Tests;

public sealed class RoomClassificationServiceTests
{
    private sealed class FakeClassifier(string version) : IRoomEnvironmentClassifier
    {
        public string ModelVersion => version; public double DefaultThreshold => 0.8;
        public RoomEnvironmentPrediction? Classify(string name, string description, double threshold) => new("forest", 0.9, version);
    }

    [Fact]
    public async Task InstallFromFileMovesServiceToReadyAndLoadsClassifierLazily()
    {
        var data = Path.Combine(Path.GetTempPath(), "wandur-data-" + Guid.NewGuid());
        var zipPath = Path.Combine(Path.GetTempPath(), "wandur-pkg-" + Guid.NewGuid() + ".zip");
        ZipFile.CreateFromDirectory(ModelPackageTests.CreateFakePackage(), zipPath);
        var loaded = 0;
        using var service = new RoomClassificationService(data, new HttpClient(), classifierFactory: p => { loaded++; return new FakeClassifier(p.Version); });
        Assert.Equal(RoomClassificationState.NotInstalled, service.Status.State);
        Assert.Null(service.TryGetClassifier());
        var changes = 0; service.Changed += () => changes++;
        await service.InstallFromFileAsync(zipPath, CancellationToken.None);
        Assert.Equal(RoomClassificationState.Ready, service.Status.State); Assert.Equal("9.9.9", service.Status.Version);
        Assert.Equal(0, loaded);
        Assert.Equal("9.9.9", service.TryGetClassifier()!.ModelVersion); Assert.Same(service.TryGetClassifier(), service.TryGetClassifier());
        Assert.Equal(1, loaded); Assert.True(changes >= 1);
        Assert.Equal(Path.Combine(data, "models", "room-classifier", "9.9.9"), new ModelPackageInstaller(Path.Combine(data, "models", "room-classifier"), new HttpClient()).InstalledDirectory);
    }

    [Fact]
    public async Task FailedInstallReportsFailureAndKeepsNotInstalled()
    {
        var data = Path.Combine(Path.GetTempPath(), "wandur-data-" + Guid.NewGuid());
        using var service = new RoomClassificationService(data, new HttpClient(), classifierFactory: p => new FakeClassifier(p.Version));
        var bad = Path.Combine(Path.GetTempPath(), "wandur-bad-" + Guid.NewGuid() + ".zip");
        File.WriteAllText(bad, "not a zip");
        await service.InstallFromFileAsync(bad, CancellationToken.None);
        Assert.Equal(RoomClassificationState.Failed, service.Status.State); Assert.NotNull(service.Status.Message);
        Assert.Null(service.TryGetClassifier());
    }

    [Fact]
    public async Task ClassifierFactoryFailureIsCaughtAndReportedAsFailed()
    {
        var data = Path.Combine(Path.GetTempPath(), "wandur-data-" + Guid.NewGuid());
        var zipPath = Path.Combine(Path.GetTempPath(), "wandur-pkg-" + Guid.NewGuid() + ".zip");
        ZipFile.CreateFromDirectory(ModelPackageTests.CreateFakePackage(), zipPath);
        using var service = new RoomClassificationService(data, new HttpClient(), classifierFactory: _ => throw new DllNotFoundException("boom"));
        await service.InstallFromFileAsync(zipPath, CancellationToken.None);
        Assert.Equal(RoomClassificationState.Ready, service.Status.State);
        Assert.Null(service.TryGetClassifier());
        Assert.Equal(RoomClassificationState.Failed, service.Status.State);
        Assert.False(string.IsNullOrEmpty(service.Status.Message));
    }

    [Fact]
    public async Task MalformedRedirectFailsCleanlyWithoutWedgingBusy()
    {
        var (listener, port) = ModelPackageInstallerTests.StartListener();
        using var _listener = listener;
        _ = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            context.Response.StatusCode = (int)HttpStatusCode.Found;
            context.Response.Headers.Add("Location", "ht!tp://bad");
            context.Response.Close();
        });

        var data = Path.Combine(Path.GetTempPath(), "wandur-data-" + Guid.NewGuid());
        using var service = new RoomClassificationService(data, new HttpClient(),
            packageUrl: new Uri($"http://127.0.0.1:{port}/model.zip"), classifierFactory: p => new FakeClassifier(p.Version));
        await service.DownloadAsync(CancellationToken.None);
        Assert.Equal(RoomClassificationState.Failed, service.Status.State);
        listener.Stop();

        // The failed download must not leave _busy stuck true: a later install proceeds instead of being silently ignored.
        var zipPath = Path.Combine(Path.GetTempPath(), "wandur-pkg-" + Guid.NewGuid() + ".zip");
        ZipFile.CreateFromDirectory(ModelPackageTests.CreateFakePackage(), zipPath);
        await service.InstallFromFileAsync(zipPath, CancellationToken.None);
        Assert.Equal(RoomClassificationState.Ready, service.Status.State);
    }

    [Fact]
    public void ForTestingIsReadyImmediately()
    {
        using var service = RoomClassificationService.ForTesting(new FakeClassifier("t"));
        Assert.Equal(RoomClassificationState.Ready, service.Status.State);
        Assert.Equal("t", service.TryGetClassifier()!.ModelVersion);
    }

    [Fact]
    public void SettingsValidateThreshold()
    {
        new ClientSettings().Validate();
        Assert.True(new ClientSettings().ClassifyRoomsLocally); Assert.Equal(0.8, new ClientSettings().RoomClassificationThreshold);
        Assert.Throws<ArgumentException>(() => (new ClientSettings() with { RoomClassificationThreshold = 0.2 }).Validate());
        Assert.Throws<ArgumentException>(() => (new ClientSettings() with { RoomClassificationThreshold = double.NaN }).Validate());
        (new ClientSettings() with { RoomClassificationThreshold = 0.99 }).Validate();
    }
}
