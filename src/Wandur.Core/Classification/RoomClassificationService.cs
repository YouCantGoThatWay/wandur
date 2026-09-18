namespace Wandur.Core.Classification;

public enum RoomClassificationState { NotInstalled, Downloading, Ready, Failed }
public sealed record RoomClassificationStatus(RoomClassificationState State, double Progress = 0, string? Version = null, string? Message = null);

/// <summary>Owns the installed room-classifier package and its lazily created classifier.</summary>
public sealed class RoomClassificationService : IDisposable
{
    public static readonly Uri DefaultPackageUrl = new("https://github.com/YouCantGoThatWay/room-classifier/releases/download/v0.1.1/wundur-room-classifier-0.1.1.zip");
    private readonly ModelPackageInstaller? _installer;
    private readonly Uri _packageUrl;
    private readonly Func<ModelPackage, IRoomEnvironmentClassifier> _factory;
    private readonly object _gate = new();
    private IRoomEnvironmentClassifier? _classifier;
    private ModelPackage? _package;
    private bool _busy;

    public RoomClassificationService(string dataDirectory, HttpClient http, Uri? packageUrl = null, Func<ModelPackage, IRoomEnvironmentClassifier>? classifierFactory = null)
    {
        _installer = new ModelPackageInstaller(Path.Combine(dataDirectory, "models", "room-classifier"), http);
        _packageUrl = packageUrl ?? DefaultPackageUrl;
        _factory = classifierFactory ?? (package => new OnnxRoomEnvironmentClassifier(package));
        Status = InstalledStatus();
    }

    private RoomClassificationService(IRoomEnvironmentClassifier classifier)
    {
        _packageUrl = DefaultPackageUrl; _factory = _ => classifier; _classifier = classifier;
        Status = new(RoomClassificationState.Ready, 1, classifier.ModelVersion);
    }

    public static RoomClassificationService ForTesting(IRoomEnvironmentClassifier classifier) => new(classifier);

    public RoomClassificationStatus Status { get; private set; }
    /// <summary>The installed package version, without creating (and so loading) the classifier.</summary>
    public string? ModelVersion { get { var status = Status; return status.State == RoomClassificationState.Ready ? status.Version : null; } }
    public event Action? Changed;

    private RoomClassificationStatus InstalledStatus()
    {
        var package = _installer?.LoadInstalled();
        if (package is null) return new(RoomClassificationState.NotInstalled);
        _package = package;
        return new(RoomClassificationState.Ready, 1, package.Version);
    }

    private void Set(RoomClassificationStatus status) { Status = status; Changed?.Invoke(); }

    /// <summary>Creates the classifier on first use; returns null unless the package is installed and loads.</summary>
    public IRoomEnvironmentClassifier? TryGetClassifier()
    {
        RoomClassificationStatus? failure = null;
        IRoomEnvironmentClassifier? result;
        lock (_gate)
        {
            if (_classifier is not null) return _classifier;
            if (Status.State != RoomClassificationState.Ready) return null;
            var package = _package ?? _installer?.LoadInstalled();
            if (package is null) return null;
            try { result = _classifier = _factory(package); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or InvalidOperationException or Microsoft.ML.OnnxRuntime.OnnxRuntimeException)
            { _package = null; result = null; failure = new(RoomClassificationState.Failed, 0, null, ex.Message); }
        }
        if (failure is not null) Set(failure);
        return result;
    }

    public Task DownloadAsync(CancellationToken cancellation) =>
        RunInstall(progress => _installer!.DownloadAsync(_packageUrl, progress, cancellation));

    public Task InstallFromFileAsync(string zipPath, CancellationToken cancellation) =>
        RunInstall(async _ => { await using var stream = File.OpenRead(zipPath); return await _installer!.InstallAsync(stream, cancellation); });

    private async Task RunInstall(Func<IProgress<double>, Task<ModelPackage>> install)
    {
        if (_installer is null) return;
        lock (_gate) { if (_busy) return; _busy = true; }
        Set(new(RoomClassificationState.Downloading));
        try
        {
            var package = await install(new Progress<double>(p => Set(new(RoomClassificationState.Downloading, p))));
            lock (_gate) { (_classifier as IDisposable)?.Dispose(); _classifier = null; _package = package; }
            Set(new(RoomClassificationState.Ready, 1, package.Version));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or HttpRequestException or OperationCanceledException or UnauthorizedAccessException)
        { lock (_gate) _package = null; Set(new(RoomClassificationState.Failed, 0, null, ex.Message)); }
        finally { lock (_gate) _busy = false; }
    }

    public void Dispose() { lock (_gate) { (_classifier as IDisposable)?.Dispose(); _classifier = null; } }
}
