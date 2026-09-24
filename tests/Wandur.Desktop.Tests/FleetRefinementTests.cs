using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Wandur.Core.Settings;
using Wandur.Desktop.Terminal;

namespace Wandur.Desktop.Tests;

public sealed class FleetRefinementTests
{
    private static MainWindow CreateWindow()
    {
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), "wandur-refinement-" + Guid.NewGuid(), "settings.json"));
        store.Save(new ClientSettings { Theme = "Hull", UseWorldThemes = false });
        var window = new MainWindow(new TranscriptDisplayFactory(), store, new MemoryPasswordVault(),
            new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore()) { Width = 1380, Height = 900 };
        window.Show();
        Settle(window);
        return window;
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
    }

    [AvaloniaFact]
    public void FooterHintAndToolbarTypographyAreCompactAndAligned()
    {
        var window = CreateWindow();
        try
        {
            var footer = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "WindowFooter");
            var hint = footer.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text?.Contains("Ctrl+Tab") == true);
            Assert.Equal(Avalonia.Layout.VerticalAlignment.Center, hint.VerticalAlignment);
            Assert.InRange(Math.Abs(hint.TranslatePoint(new Point(0, hint.Bounds.Height / 2), footer)!.Value.Y - footer.Bounds.Height / 2), 0, 1);
            Assert.InRange(window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "ToolbarWorlds").FontSize, 12, 13);
            Assert.All(window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("fleet-action-label")), t => Assert.InRange(t.FontSize, 12, 13));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void SideDocksJoinTheChassisButFloatingDocksKeepTheirRims()
    {
        var window = CreateWindow();
        try
        {
            var docks = window.GetVisualDescendants().OfType<ThemeDockSkinHost>().ToArray();
            var left = docks.Single(d => d.DataContext is IToolDock { Id: "left" });
            var right = docks.Single(d => d.DataContext is IToolDock { Id: "map-dock" });
            Assert.Equal(0, left.Child!.Bounds.Left);
            Assert.Equal(0, left.Margin.Left);
            Assert.Equal(right.Bounds.Width, right.Child!.Bounds.Right);
            Assert.Equal(0, right.Margin.Right);
            Assert.Equal(2, right.Child.Bounds.Left);
            window.Workspace.FloatDockable(window.Workspace.WorldsTool!);
            Settle(window);
            // Headless Dock hosts are tracked by the factory even without a desktop lifetime.
            var hosts = window.Workspace.HostWindows;
            Assert.NotEmpty(hosts);
            var windows = hosts.OfType<Window>().ToArray();
            Assert.NotEmpty(windows);
            foreach (var host in windows)
            {
                Settle(host);
                var floatingDocks = host.GetVisualDescendants().OfType<ThemeDockSkinHost>().ToArray();
                Assert.NotEmpty(floatingDocks);
                Assert.All(floatingDocks, d =>
                {
                    Assert.Equal(2, d.Child!.Bounds.Left);
                    Assert.Equal(d.Bounds.Width - 2, d.Child.Bounds.Right);
                });
            }
            window.ResetLayout();
            window.Width = 1040;
            Settle(window);
            left = window.GetVisualDescendants().OfType<ThemeDockSkinHost>().Single(d => d.DataContext is IToolDock { Id: "left" });
            Assert.Equal(0, left.Child!.Bounds.Left);
            Assert.Equal(0, left.Margin.Left);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void MacNativeButtonsHaveAnUnbeveledSurround()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var window = CreateWindow();
        try
        {
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            using var stream = new MemoryStream();
            frame.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            using var pixels = SkiaSharp.SKBitmap.Decode(stream.ToArray());
            Assert.True(pixels.GetPixel(20, 2).Red > 180, "No dark inset cap border should crowd the native controls.");
        }
        finally { window.Close(); }
    }
}
