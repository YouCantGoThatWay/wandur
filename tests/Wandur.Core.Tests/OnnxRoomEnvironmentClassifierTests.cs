using System.Text.Json;
using Wandur.Core.Classification;

namespace Wandur.Core.Tests;

/// <summary>Runs only when WANDUR_ROOM_MODEL_DIR points at an extracted model package.</summary>
public sealed class OnnxRoomEnvironmentClassifierTests
{
    private static string? ModelDirectory => Environment.GetEnvironmentVariable("WANDUR_ROOM_MODEL_DIR") is { Length: > 0 } dir && Directory.Exists(dir) ? dir : null;

    [Fact]
    public void ReproducesEveryParityFixture()
    {
        if (ModelDirectory is not { } dir) return; // gated
        using var classifier = new OnnxRoomEnvironmentClassifier(ModelPackage.Load(dir));
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "parity_fixtures.json")));
        var atol = document.RootElement.GetProperty("atol_probs").GetDouble();
        foreach (var fixture in document.RootElement.GetProperty("fixtures").EnumerateArray())
        {
            var text = fixture.GetProperty("text").GetString()!;
            var expectedIds = fixture.GetProperty("token_ids").EnumerateArray().Select(v => v.GetInt32()).ToArray();
            Assert.Equal(expectedIds, classifier.Tokenize(text));
            var expectedProbs = fixture.GetProperty("probs").EnumerateArray().Select(v => v.GetDouble()).ToArray();
            var probs = classifier.Probabilities(text);
            for (var i = 0; i < expectedProbs.Length; i++) Assert.InRange(probs[i], expectedProbs[i] - atol, expectedProbs[i] + atol);
            var prediction = classifier.Classify(fixture.GetProperty("name").GetString()!, fixture.GetProperty("description").GetString()!, threshold: 0);
            Assert.Equal(fixture.GetProperty("predicted").GetString(), prediction!.Environment);
        }
    }

    [Fact]
    public void AbstainsBelowThreshold()
    {
        if (ModelDirectory is not { } dir) return;
        using var classifier = new OnnxRoomEnvironmentClassifier(ModelPackage.Load(dir));
        Assert.Null(classifier.Classify("Void", "Nothing.", threshold: 1.0));
        Assert.NotNull(classifier.Classify("Void", "Nothing.", threshold: 0.0));
        Assert.Equal("0.1.1", classifier.ModelVersion);
    }

    [Fact]
    public void ClassifyAfterDisposeThrowsObjectDisposed()
    {
        if (ModelDirectory is not { } dir) return;
        var classifier = new OnnxRoomEnvironmentClassifier(ModelPackage.Load(dir));
        classifier.Dispose(); classifier.Dispose(); // idempotent
        Assert.Throws<ObjectDisposedException>(() => classifier.Classify("Void", "Nothing.", 0));
    }
}
