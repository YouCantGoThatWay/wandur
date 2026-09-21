using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Discovery;
using Wandur.Core.Settings;

namespace Wandur.Desktop.Tests;

/// <summary>
/// Opening a session on a world that carries its own appearance repaints the whole window. Anything that
/// resolved a Fluent brush instead of an application brush keeps the colour it had, which is invisible
/// rather than merely wrong when a light preset gives way to a dark world: the caret disappeared outright.
/// </summary>
public sealed class ThemeSwitchContrastTests
{
    [AvaloniaFact]
    public async Task OpeningADarkWorldFromALightPresetRepaintsEveryPieceOfChrome()
    {
        var path = Path.Combine(Path.GetTempPath(), "wandur-switch-" + Guid.NewGuid()); Directory.CreateDirectory(path);
        var theme = JsonSerializer.Deserialize<WorldTheme>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "world-theme-metallic.json")))!;
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var world = new WorldListing
        {
            Id = "lotj", Name = "Legends of the Jedi", Host = "127.0.0.1",
            Port = ((IPEndPoint)listener.LocalEndpoint).Port, Theme = theme,
            Summary = "A galaxy shaped by its players.", Description = "Explore distant worlds."
        };
        File.WriteAllText(Path.Combine(path, "directory.json"), JsonSerializer.Serialize(
            new { schema_version = 2, format = "wandur.directory", fetched_at = DateTimeOffset.UtcNow, worlds = new[] { world } },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
        using var catalog = new WorldCatalog(Path.Combine(path, "directory.json"));

        var store = new SettingsStore(Path.Combine(path, "settings.json"));
        store.Save(new ClientSettings { Theme = "Daylight" });
        ThemeService.Apply(store.Load().Settings);
        var window = new MainWindow(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), store,
            new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore(), catalog: catalog);
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.True(ContrastProbe.Luminance(ContrastProbe.Resource("ShellBrush")) > 0.5, "the preset under test has to start light");

            await window.Sessions.OpenAsync(world.ToProfile());
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();

            var shell = ContrastProbe.Resource("ShellBrush");
            Assert.True(ContrastProbe.Luminance(shell) < 0.1, $"the world's dark shell should be in use, got {shell}");

            var failures = ContrastProbe.Scan(window);
            Assert.True(failures.Count == 0,
                $"{failures.Count} pieces of chrome kept the previous theme:\n" + string.Join("\n", failures.Distinct().Take(12)));

            // The caret is the one that fails silently: it is never measured by a foreground scan.
            var carets = window.GetVisualDescendants().OfType<TextBox>().ToArray();
            Assert.NotEmpty(carets);
            foreach (var box in carets)
            {
                var caret = Assert.IsAssignableFrom<ISolidColorBrush>(box.CaretBrush);
                var surface = ContrastProbe.Surface(box, shell);
                Assert.True(ContrastProbe.Contrast(caret.Color, surface) >= ContrastProbe.EnabledFloor,
                    $"caret in {box.Name ?? "(unnamed)"} is {caret.Color} on {surface}");
            }
        }
        finally
        {
            window.Close(); await window.Sessions.DisposeAsync();
            ThemeService.Apply(new ClientSettings());
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }
}
