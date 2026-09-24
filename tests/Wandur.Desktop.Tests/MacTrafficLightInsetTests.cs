using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Wandur.Desktop.Services;

namespace Wandur.Desktop.Tests;

public sealed class MacTrafficLightInsetTests
{
    [AvaloniaFact]
    public void HeadlessWindowAndQueuedWorkAfterCloseAreSafe()
    {
        var window = new Window();
        using var inset = new MacTrafficLightInset(window);
        window.Show();
        window.Width = 1200;
        window.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.IsVisible);
    }

    [Fact]
    public void InsetIsAppliedOnceAndReappliedAfterNativeLayoutResetsIt()
    {
        var position = new MacTrafficLightPosition();
        var bounds = new Rect(0, 0, 200, 22);
        Assert.Equal(new Point(12, 0), position.Resolve(new Rect(8, 4, 14, 14), bounds, false, true));
        for (var i = 0; i < 20; i++)
            Assert.Equal(new Point(12, 0), position.Resolve(new Rect(12, 0, 14, 14), bounds, false, true));
        Assert.Equal(new Point(12, 0), position.Resolve(new Rect(8, 4, 14, 14), bounds, false, true));
    }

    [Theory]
    [InlineData(false, 3, 0)]
    [InlineData(true, 5, 8)]
    public void UsesThreeDownWhenFourWouldClip(bool flipped, double y, double expectedY)
    {
        var position = new MacTrafficLightPosition();
        Assert.Equal(new Point(12, expectedY), position.Resolve(new Rect(8, y, 14, 14), new Rect(0, 0, 200, 22), flipped, true));
    }

    [Fact]
    public void RefusesAnInsetThatCannotFitEvenThreeDownOrFourRight()
    {
        Assert.Equal(new Point(8, 2), new MacTrafficLightPosition().Resolve(new Rect(8, 2, 14, 14), new Rect(0, 0, 200, 22), false, true));
        Assert.Equal(new Point(8, 4), new MacTrafficLightPosition().Resolve(new Rect(8, 4, 14, 14), new Rect(0, 0, 24, 22), false, true));
    }

    [Fact]
    public void SuspensionRestoresOnlyOurOffsetAndResumeDoesNotAccumulate()
    {
        var position = new MacTrafficLightPosition();
        var bounds = new Rect(0, 0, 200, 22);
        position.Resolve(new Rect(8, 4, 14, 14), bounds, false, true);
        Assert.Equal(new Point(8, 4), position.Resolve(new Rect(12, 0, 14, 14), bounds, false, false));
        Assert.Equal(new Point(8, 4), position.Resolve(new Rect(8, 4, 14, 14), bounds, false, false));
        Assert.Equal(new Point(12, 0), position.Resolve(new Rect(8, 4, 14, 14), bounds, false, true));
    }

    [Fact]
    public void ParentHeightChangesAndPartialNativeResetsDoNotAddAnotherOffset()
    {
        var position = new MacTrafficLightPosition();
        position.Resolve(new Rect(8, 4, 14, 14), new Rect(0, 0, 200, 22), false, true);
        Assert.Equal(new Point(12, 10), position.Resolve(new Rect(12, 10, 14, 14), new Rect(0, 0, 200, 32), false, true));
        Assert.Equal(new Point(12, 10), position.Resolve(new Rect(8, 10, 14, 14), new Rect(0, 0, 200, 32), false, true));
        Assert.Equal(new Point(12, 10), position.Resolve(new Rect(12, 14, 14, 14), new Rect(0, 0, 200, 32), false, true));
    }
}
