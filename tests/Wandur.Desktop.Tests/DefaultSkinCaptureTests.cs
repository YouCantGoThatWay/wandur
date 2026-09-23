using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Wandur.Core.Settings;
using Wandur.Desktop.Terminal;

namespace Wandur.Desktop.Tests;

/// <summary>
/// Renders the default look to a PNG when WANDUR_CAPTURE_DIR is set, so it can be looked at before anyone
/// launches the app. A palette in a file says almost nothing; the window at real size says everything.
/// </summary>
public sealed class DefaultSkinCaptureTests
{
    [AvaloniaTheory]
    [InlineData("Hull")]
    [InlineData("Paper")]
    [InlineData("Ember")]
    public async Task CaptureDefaultLook(string theme)
    {
        if (Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR") is not { Length: > 0 } dir) return;
        var path = Path.Combine(Path.GetTempPath(), "wandur-capture-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        var store = new SettingsStore(Path.Combine(path, "settings.json"));
        store.Save(new ClientSettings { Theme = theme });
        var window = new MainWindow(new TranscriptDisplayFactory(), store, new MemoryPasswordVault(),
            new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore())
        { Width = 1380, Height = 900 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            await window.Controller.StartAsync(); Dispatcher.UIThread.RunJobs();
            window.Controller.Terminal.Append("Welcome to \x1b[36mLegends of the Jedi\x1b[0m.\r\nType 'help' for assistance.\r\n\r\n\x1b[36mDOCKING CONTROL\x1b[0m\r\nExits: \x1b[32mnorth\x1b[0m, \x1b[32meast\x1b[0m, \x1b[32mdown\x1b[0m\r\n");
            window.Controller.FlushOutput(); Dispatcher.UIThread.RunJobs();
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(4);
            Directory.CreateDirectory(dir);
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            frame!.Save(Path.Combine(dir, $"default-{theme.ToLowerInvariant()}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
        finally { await window.Sessions.DisposeAsync(); window.Close(); }
    }
}
