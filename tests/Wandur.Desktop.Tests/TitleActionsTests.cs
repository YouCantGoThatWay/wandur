using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Settings;
using Wandur.Desktop.Terminal;

namespace Wandur.Desktop.Tests;

public sealed class TitleActionsTests
{
    private static MainWindow CreateWindow() => new(new TranscriptDisplayFactory(),
        new SettingsStore(Path.Combine(Path.GetTempPath(), "wandur-title-actions-" + Guid.NewGuid(), "settings.json")),
        new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());

    private static T Named<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
    }

    private static void Click(Window window, string name)
    {
        var button = Named<Button>(window, name);
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
    }

    private static void Capture(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(directory, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    [AvaloniaFact]
    public async Task TitleActionsStayRightOfThePlaqueAndClearOfNativeCaptionButtons()
    {
        var window = CreateWindow();
        try
        {
            window.Show();
            foreach (var width in new[] { 1380, 1040 })
            {
                window.Width = width; Settle(window);
                var theme = Named<Button>(window, "TitleThemeButton");
                var full = Named<Button>(window, "TitleFullScreenButton");
                var plaque = Named<Border>(window, "PlaqueTitleHost");
                var titleRight = plaque.TranslatePoint(new Point(plaque.Bounds.Width, 0), window)!.Value.X;
                var themeOrigin = theme.TranslatePoint(default, window)!.Value;
                var fullOrigin = full.TranslatePoint(default, window)!.Value;
                Assert.True(themeOrigin.X >= titleRight + 8);
                Assert.True(fullOrigin.X >= themeOrigin.X + theme.Bounds.Width);
                Assert.True(fullOrigin.X + full.Bounds.Width <= window.Bounds.Width - (OperatingSystem.IsWindows() ? 144 : 8));
                Assert.InRange(fullOrigin.Y, 0, 50 - full.Bounds.Height);
                Capture(window, $"title-actions-{width}");
                Click(window, "TitleThemeButton"); Settle(window);
                var menu = Assert.IsType<MenuFlyout>(theme.Flyout);
                Assert.True(menu.IsOpen, "The titlebar button must receive clicks instead of dragging the window.");
                Settle(window);
                var presenter = Assert.IsType<MenuFlyoutPresenter>(menu.Popup.Child);
                var popup = TopLevel.GetTopLevel(presenter);
                Assert.NotNull(popup);
                popup.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
                Assert.NotEmpty(presenter.GetVisualDescendants().OfType<MenuItem>());
                if (Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR") is { Length: > 0 } directory)
                {
                    using var frame = popup.CaptureRenderedFrame();
                    Assert.NotNull(frame);
                    frame.Save(Path.Combine(directory, $"theme-menu-{width}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
                }
                menu.Hide();
            }
        }
        finally { await window.Sessions.DisposeAsync(); window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullScreenReclaimsTitleSpaceAndAlwaysOffersAnExit(bool toolbarVisible)
    {
        var window = CreateWindow();
        try
        {
            window.Show(); window.ToolbarVisible = toolbarVisible; Settle(window);
            var dock = Named<Control>(window, "WorkspaceDock");
            var before = dock.TranslatePoint(default, window)!.Value.Y;
            Click(window, "TitleFullScreenButton");
            Settle(window);
            Assert.Equal(WindowState.FullScreen, window.WindowState);
            Assert.False(Named<Border>(window, "PlaqueTitleHost").IsEffectivelyVisible);
            Assert.False(Named<Border>(window, "MetalHeaderDrag").IsEffectivelyVisible);
            Assert.False(Named<TextBlock>(window, "AppTitle").IsEffectivelyVisible);
            Assert.True(dock.TranslatePoint(default, window)!.Value.Y <= before - 45);
            Assert.Equal(toolbarVisible, window.ToolbarVisible);
            Assert.True(Named<Button>(window, "ExitFullScreenButton").IsEffectivelyVisible);
            Capture(window, $"fullscreen-toolbar-{toolbarVisible}");
            window.Sessions.PreviewAppearanceSettings(new ClientSettings { Theme = "Slate" });
            Settle(window);
            Assert.False(Named<Border>(window, "PlaqueTitleHost").IsEffectivelyVisible);
            Click(window, "ExitFullScreenButton");
            Settle(window);
            Assert.Equal(WindowState.Normal, window.WindowState);
            Assert.True(Named<Border>(window, "PlaqueTitleHost").IsEffectivelyVisible);
            Assert.Equal(toolbarVisible, window.ToolbarVisible);
            Assert.InRange(Math.Abs(dock.TranslatePoint(default, window)!.Value.Y - before), 0, 1);
        }
        finally { await window.Sessions.DisposeAsync(); window.Close(); ThemeService.Apply(new ClientSettings()); }
    }

    [AvaloniaFact]
    public async Task NativeFullScreenAndExitRestoreAMaximizedWindow()
    {
        var window = CreateWindow();
        try
        {
            window.Show(); window.WindowState = WindowState.Maximized; Settle(window);
            // Native green-button and OS/menu actions can enter fullscreen without clicking our button.
            window.WindowState = WindowState.FullScreen; Settle(window);
            Assert.False(Named<Border>(window, "PlaqueTitleHost").IsEffectivelyVisible);
            Click(window, "ExitFullScreenButton");
            Settle(window);
            Assert.Equal(WindowState.Maximized, window.WindowState);
            Assert.True(Named<Border>(window, "PlaqueTitleHost").IsEffectivelyVisible);
        }
        finally { await window.Sessions.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task FullScreenShortcutRemainsAvailableWithTheToolbarHidden()
    {
        var window = CreateWindow();
        try
        {
            window.Show(); window.ToolbarVisible = false; Settle(window);
            var gesture = OperatingSystem.IsMacOS()
                ? new KeyGesture(Key.F, KeyModifiers.Meta | KeyModifiers.Control)
                : new KeyGesture(Key.F11);
            var shortcut = window.KeyBindings.Single(k => k.Gesture == gesture);
            for (var i = 0; i < 3; i++)
            {
                shortcut.Command!.Execute(null); Settle(window);
                Assert.Equal(WindowState.FullScreen, window.WindowState);
                Assert.True(Named<Button>(window, "ExitFullScreenButton").IsEffectivelyVisible);
                window.ToolbarVisible = true; Settle(window);
                var exit = Named<Button>(window, "ExitFullScreenButton");
                Assert.Contains(exit.GetVisualAncestors(), a => a == Named<Border>(window, "MainToolbar"));
                window.ToolbarVisible = false; Settle(window);
                shortcut.Command.Execute(null); Settle(window);
                Assert.Equal(WindowState.Normal, window.WindowState);
                Assert.True(Named<Button>(window, "TitleFullScreenButton").IsEffectivelyVisible);
                Assert.False(window.ToolbarVisible);
            }
        }
        finally { await window.Sessions.DisposeAsync(); window.Close(); }
    }
}
