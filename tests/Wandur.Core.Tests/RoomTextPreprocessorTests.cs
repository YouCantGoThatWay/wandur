using System.Text.Json;
using Wandur.Core.Classification;

namespace Wandur.Core.Tests;

public sealed class RoomTextPreprocessorTests
{
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "room-classifier-parity.json");

    [Fact]
    public void CleanStripsColorCodesTildesAndCollapsesWhitespace()
    {
        Assert.Equal("The Forest", RoomTextPreprocessor.Clean("&RThe &GForest&x"));
        Assert.Equal("Dark cave", RoomTextPreprocessor.Clean("@rDark@n cave"));
        Assert.Equal("Misty path", RoomTextPreprocessor.Clean("{cMisty{x path"));
        Assert.Equal("Red room", RoomTextPreprocessor.Clean("[31mRed[0m room"));
        Assert.Equal("A hall. Dust hangs in the air.", RoomTextPreprocessor.Clean("A hall.~\r\n   Dust    hangs\n\nin the air.  "));
        Assert.Equal("", RoomTextPreprocessor.Clean(null));
    }

    [Fact]
    public void BuildTextJoinsCleanedNameAndDescriptionWithNewline() =>
        Assert.Equal("Temple\nA hall.", RoomTextPreprocessor.BuildText("Temple", "A hall."));

    [Fact]
    public void EveryParityFixtureReproducesItsText()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath));
        var fixtures = document.RootElement.GetProperty("fixtures").EnumerateArray().ToArray();
        Assert.Equal(23, fixtures.Length);
        foreach (var fixture in fixtures)
            Assert.Equal(fixture.GetProperty("text").GetString(),
                RoomTextPreprocessor.BuildText(fixture.GetProperty("name").GetString()!, fixture.GetProperty("description").GetString()!));
    }

    [Fact]
    public void InferenceKeyIsStableAndModelScoped()
    {
        var a = RoomTextPreprocessor.InferenceKey("Temple", "A hall.", "0.1.1");
        Assert.Equal(a, RoomTextPreprocessor.InferenceKey("&RTemple", "A  hall.~", "0.1.1"));
        Assert.NotEqual(a, RoomTextPreprocessor.InferenceKey("Temple", "A hall.", "0.2.0"));
        Assert.Equal(64, a.Length);
    }
}
