using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Wandur.Core.Settings;

namespace Wandur.Desktop.Tests;

/// <summary>
/// Every preset has to stay legible, and a pale grey on a light surface reads as absent in a way the
/// same ratio on a dark surface does not. The window is built for real and each visible glyph is
/// measured against the surface it actually lands on, because the offenders here were Fluent defaults
/// leaking past styles that matched nothing, not colors anyone chose.
/// </summary>
public sealed class ThemeContrastTests
{
    public static TheoryData<string> Presets()
    {
        var data = new TheoryData<string>();
        foreach (var name in ThemeService.Names) data.Add(name);
        return data;
    }

    [AvaloniaTheory]
    [MemberData(nameof(Presets))]
    public async Task EveryPresetKeepsItsChromeLegible(string theme)
    {
        var directory = Path.Combine(Path.GetTempPath(), "wandur-contrast-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var store = new SettingsStore(Path.Combine(directory, "settings.json"));
        store.Save(new ClientSettings { Theme = theme });
        ThemeService.Apply(store.Load().Settings);
        var window = new MainWindow(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), store,
            new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var failures = ContrastProbe.Scan(window);
            Assert.True(failures.Count == 0, $"{theme} has {failures.Count} unreadable foregrounds:\n" + string.Join("\n", failures.Distinct().Take(12)));
        }
        finally
        {
            await window.Sessions.DisposeAsync(); window.Close();
            ThemeService.Apply(new ClientSettings());
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
