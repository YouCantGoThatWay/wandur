using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Settings;
using Wandur.Desktop.Terminal;

namespace Wandur.Desktop.Tests;

public sealed class FleetSkinTests
{
    [AvaloniaFact]
    public async Task SwitchingToHullUsesOneConsistentNativeTitleBarHeight()
    {
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), "wandur-fleet-switch-" + Guid.NewGuid(), "settings.json"));
        store.Save(new ClientSettings { Theme = "Paper", UseWorldThemes = false });
        var window = new MainWindow(new TranscriptDisplayFactory(), store, new MemoryPasswordVault(),
            new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var requestedHeights = new List<double>();
            window.PropertyChanged += (_, e) =>
            {
                if (e.Property == Window.ExtendClientAreaTitleBarHeightHintProperty)
                    requestedHeights.Add(window.ExtendClientAreaTitleBarHeightHint);
            };
            using var preferences = new Wandur.Desktop.ViewModels.PreferencesViewModel(
                window.Controller, window.Sessions.PreviewAppearanceSettings);
            preferences.Theme = "Hull";
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            window.Width += 80;
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var band = window.GetVisualDescendants().OfType<ThemeWindowSkinHost>().Single().BandHeight;
            Assert.Equal(50, band);
            Assert.Equal(band, window.ExtendClientAreaTitleBarHeightHint);
            Assert.Empty(requestedHeights); // A palette switch must not change native frame geometry.
            preferences.SaveCommand.Execute(null);
        }
        finally { await window.Sessions.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task BoxedMapButtonsStillShowHoverFeedback()
    {
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), "wandur-fleet-hover-" + Guid.NewGuid(), "settings.json"));
        store.Save(new ClientSettings { Theme = "Hull", UseWorldThemes = false });
        var window = new MainWindow(new TranscriptDisplayFactory(), store, new MemoryPasswordVault(),
            new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "FitMapFloor");
            window.MouseMove(new Point(1, window.Height - 1));
            var before = button.Background?.ToString();
            window.MouseMove(button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value);
            Dispatcher.UIThread.RunJobs();
            Assert.True(button.IsPointerOver);
            Assert.NotEqual(before, button.Background?.ToString());
        }
        finally { await window.Sessions.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task RaisedTitleOverlapsToolbarWithoutCoveringItsActionsAndLabelsCollapseOnResize()
    {
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), "wandur-fleet-depth-" + Guid.NewGuid(), "settings.json"));
        store.Save(new ClientSettings { Theme = "Hull", UseWorldThemes = false });
        var window = new MainWindow(new TranscriptDisplayFactory(), store, new MemoryPasswordVault(),
            new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore()) { Width = 1536 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var plaque = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PlaqueTitleHost");
            var toolbar = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "MainToolbar");
            var bottom = plaque.TranslatePoint(new Point(0, plaque.Bounds.Height), window)!.Value.Y;
            var top = toolbar.TranslatePoint(default, window)!.Value.Y;
            Assert.InRange(bottom - top, 8, 16);
            var settings = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "FleetSettings");
            Assert.True(settings.IsEffectivelyVisible);
            Assert.True(settings.TranslatePoint(default, window)!.Value.Y >= bottom);
            var labels = toolbar.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("fleet-action-label")).ToArray();
            Assert.True(labels.Length >= 2);
            Assert.All(labels, t => Assert.True(t.IsEffectivelyVisible));
            Assert.All(labels, t => Assert.DoesNotContain("▶", t.Text));
            window.Width = 800;
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.All(labels, t => Assert.False(t.IsEffectivelyVisible));
            Assert.True(settings.IsEffectivelyVisible);
            window.Sessions.PreviewAppearanceSettings(new ClientSettings { Theme = "Paper" });
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.True(settings.IsEffectivelyVisible);
        }
        finally { await window.Sessions.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task HidingToolbarStillReservesTheProjectingTitleLip()
    {
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), "wandur-fleet-hidden-toolbar-" + Guid.NewGuid(), "settings.json"));
        store.Save(new ClientSettings { Theme = "Hull", UseWorldThemes = false });
        var window = new MainWindow(new TranscriptDisplayFactory(), store, new MemoryPasswordVault(),
            new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            window.ToolbarVisible = false;
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var plaque = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PlaqueTitleHost");
            var frame = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "FleetDocumentFrame");
            Assert.True(frame.TranslatePoint(default, window)!.Value.Y >=
                plaque.TranslatePoint(new Point(0, plaque.Bounds.Height), window)!.Value.Y);
        }
        finally { await window.Sessions.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task CachedTerminalKeepsFleetSpacingAcrossPaletteChangesAndReattachment()
    {
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), "wandur-fleet-reattach-" + Guid.NewGuid(), "settings.json"));
        await using var sessions = new SessionWorkspace(new TranscriptDisplayFactory(), store, new MemoryPasswordVault(),
            new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        var view = new Wandur.Desktop.Views.TerminalView(sessions.Active.Controller);
        var host = new Window { Content = view };
        try
        {
            host.Show();
            ThemeService.Apply(new ClientSettings { Theme = "Paper" });
            Assert.Equal(new Thickness(12, 12, 8, 8), sessions.Active.Controller.Display.View.Margin);
            host.Content = null;
            ThemeService.Apply(new ClientSettings { Theme = "Hull" });
            host.Content = view;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new Thickness(12, 12, 8, 8), sessions.Active.Controller.Display.View.Margin);
        }
        finally { host.Close(); }
    }

    [AvaloniaFact]
    public void HullUsesADrawnRectangularInsetAndReservesItsTitleBandWithoutArt()
    {
        ThemeService.Apply(new ClientSettings { Theme = "Hull", UseWorldThemes = false });
        var skin = ThemeService.AppliedSkin!;
        Assert.Null(skin.Window);
        Assert.Equal("fleet", skin.Layout!.TitleBar!.Plaque!.Shape);
        Assert.Equal(50, skin.Layout.TitleBar.Height);
        Assert.False(skin.Layout.TitleBar.HostsToolbar);
        var host = new ThemeWindowSkinHost { Child = new Border() };
        host.ApplyFromTheme();
        host.Measure(new Size(1380, 900));
        host.Arrange(new Rect(0, 0, 1380, 900));
        Assert.Equal(50, host.Child.Bounds.Top);
        Assert.Equal(6, host.Child.Bounds.Left);
    }

    [Theory]
    [InlineData(1536, 88, 0, 300, 608)]
    [InlineData(1536, 0, 144, 590, 718)]
    [InlineData(1040, 0, 144, 1000, 728)]
    [InlineData(400, 88, 0, 1000, 200)]
    public void TitleMeasuresContentButReservesBothCaptionSafeAreas(double width, double left,
        double right, double textWidth, double expectedWidth)
    {
        var place = FleetTitleLayout.Calculate(width, left, right, textWidth);
        Assert.Equal(expectedWidth, place.Bounds.Width);
        Assert.Equal(width / 2, place.Bounds.Center.X);
        Assert.True(place.Bounds.Left >= Math.Max(left, right));
        Assert.Equal(60, place.Bounds.Height);
    }

    [Fact]
    public void VeryNarrowTitleFallsBackWithoutNegativeGeometry()
    {
        var place = FleetTitleLayout.Calculate(200, 88, 0, 800);
        Assert.True(place.PlainTitle);
        Assert.True(place.Bounds.Width >= 0);
    }

    [AvaloniaFact]
    public async Task ResizingTheWindowDoesNotStretchAShortTitleAndKeepsItCentered()
    {
        var path = Path.Combine(Path.GetTempPath(), "wandur-fleet-" + Guid.NewGuid());
        var store = new SettingsStore(Path.Combine(path, "settings.json"));
        store.Save(new ClientSettings { Theme = "Hull", UseWorldThemes = false });
        var window = new MainWindow(new TranscriptDisplayFactory(), store, new MemoryPasswordVault(),
            new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var host = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PlaqueTitleHost");
            var width = host.Bounds.Width;
            window.Width = 1920;
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Equal(width, host.Bounds.Width, 1);
            Assert.Equal(960, host.TranslatePoint(new Point(host.Bounds.Width / 2, 0), window)!.Value.X, 1);
            var skinHost = window.GetVisualDescendants().OfType<ThemeWindowSkinHost>().Single();
            Assert.Equal(960, skinHost.TitleModuleBounds.Center.X, 1);
            Assert.Equal(host.Bounds.Width, skinHost.TitleModuleBounds.Width, 1);
            var toolbar = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "MainToolbar");
            Assert.True(toolbar.TranslatePoint(default, window)!.Value.Y >= 50);
            var picker = window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "ToolbarWorlds");
            Assert.Equal(13, picker.FontSize);
        }
        finally { await window.Sessions.DisposeAsync(); window.Close(); }
    }
}
