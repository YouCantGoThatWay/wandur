namespace Wandur.Core.Classification;

/// <summary>A presentation hint only: never identity, exits or routing.</summary>
public sealed record RoomEnvironmentPrediction(string Environment, double Confidence, string ModelVersion);

public interface IRoomEnvironmentClassifier
{
    string ModelVersion { get; }
    double DefaultThreshold { get; }
    /// <summary>Returns null when the best class scores below <paramref name="threshold"/>. CPU-bound; call off the UI thread.</summary>
    RoomEnvironmentPrediction? Classify(string name, string description, double threshold);
}
