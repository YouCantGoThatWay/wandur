using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Discovery;
using Wandur.Core.Settings;
using Wandur.Desktop.Terminal;
using Wandur.Desktop.ViewModels;
using Wandur.Desktop.Views;
using Surface = Iciclecreek.Terminal.TerminalView;

namespace Wandur.Desktop.Tests;

/// <summary>
/// Changing the theme while the client runs has to leave the transcript readable, has to leave a world
/// theme with its own session rather than with a highlighted row, and has to be quick.
/// </summary>
public sealed class ThemeSwitchTests(ITestOutputHelper output)
{
    private static WorldTheme DarkWorldTheme => JsonSerializer.Deserialize<WorldTheme>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "world-theme.json")))!;

    private static Color Of(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;
    private static Color Resource(string key) => Of((IBrush)Application.Current!.Resources[key]!);
    private static double Contrast(Color first, Color second)
    {
        static double Luminance(Color color)
        {
            static double Linear(byte channel)
            {
                var value = channel / 255d;
                return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        }
        double a = Luminance(first), b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    [AvaloniaFact]
    public async Task SwitchingThemeWhileASessionIsOpenKeepsTheTranscriptReadable()
    {
        var theme = DarkWorldTheme;
        var path = Path.Combine(Path.GetTempPath(), "wandur-live-theme-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var world = new WorldListing { Id = "themed", Name = "Themed world", Host = "127.0.0.1",
            Port = ((IPEndPoint)listener.LocalEndpoint).Port, Theme = theme };
        var store = new SettingsStore(Path.Combine(path, "settings.json"));
        store.Save(new ClientSettings { Theme = "Ember", Profiles = [world.ToProfile()] });
        await using var sessions = new SessionWorkspace(new TranscriptDisplayFactory(), store, new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        var window = new Window { Width = 900, Height = 600, Content = new TerminalView(sessions.Active.Controller) };
        try
        {
            window.Show();
            await sessions.OpenAsync(store.Load().Settings.Profiles[0]);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
            sessions.Active.Controller.Terminal.Append("plain \x1b[31mred\x1b[0m \x1b[32mgreen\x1b[0m \x1b[97mbright\x1b[0m\r\n");
            sessions.Active.Controller.FlushOutput();
            Dispatcher.UIThread.RunJobs();
            var surface = Assert.Single(window.GetVisualDescendants().OfType<Surface>());
            Assert.Equal(theme, sessions.Active.Controller.WorldTheme);

            AssertReadable(surface, theme, "Ember");
            // The same call the preferences dialog makes while the theme picker moves.
            foreach (var preset in new[] { "Paper", "Ember", "Daylight", "Moonlight", "Ember" })
            {
                sessions.PreviewAppearanceSettings(sessions.Active.Controller.Settings with { Theme = preset });
                Dispatcher.UIThread.RunJobs();
                AssertReadable(surface, theme, preset);
            }
            sessions.EndAppearanceSettingsPreview(); Dispatcher.UIThread.RunJobs();
            AssertReadable(surface, theme, "Ember");
        }
        finally { window.Close(); ThemeService.Apply(new()); }
    }

    private void AssertReadable(Surface surface, WorldTheme theme, string preset)
    {
        var background = Of(surface.Background);
        var foreground = Of(surface.Foreground);
        Assert.Equal(Color.Parse(theme.Colors.Terminal), background);
        Assert.Equal(Color.Parse(theme.Colors.TerminalText), foreground);
        Assert.True(Contrast(foreground, background) >= 4.5,
            $"{preset}: transcript text {foreground} has {Contrast(foreground, background):F2}:1 on {background}");
        for (var index = 0; index < 16; index++)
        {
            var color = Resource($"AnsiColor{index}Brush");
            // Index 0 is the "black" games paint as a background, so it only has to stay visible.
            var required = index == 0 ? 3 : 4.5;
            Assert.True(Contrast(color, background) >= required,
                $"{preset}: ANSI {index} ({color}) has {Contrast(color, background):F2}:1 on {background}, needs {required}:1");
        }
        // The engine palette has to follow without replaying the transcript.
        var engine = surface.Terminal.Options.Theme!;
        Assert.Equal($"#{background.R:X2}{background.G:X2}{background.B:X2}", engine.Background, ignoreCase: true);
        Assert.Equal($"#{foreground.R:X2}{foreground.G:X2}{foreground.B:X2}", engine.Foreground, ignoreCase: true);
        var red = Resource("AnsiColor1Brush");
        Assert.Equal($"#{red.R:X2}{red.G:X2}{red.B:X2}", engine.Red, ignoreCase: true);
        output.WriteLine($"{preset}: transcript {foreground} on {background}, ANSI red {red}");
    }

    [AvaloniaFact]
    public async Task AWorldThemeFollowsItsSessionRatherThanTheSelectedRow()
    {
        var theme = DarkWorldTheme;
        var path = Path.Combine(Path.GetTempPath(), "wandur-world-theme-scope-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var themed = new WorldListing { Id = "themed", Name = "Themed world", Host = "127.0.0.1",
            Port = ((IPEndPoint)listener.LocalEndpoint).Port, Theme = theme };
        var plain = new WorldListing { Id = "plain", Name = "Plain world", Host = "127.0.0.1", Port = themed.Port };
        File.WriteAllText(Path.Combine(path, "directory.json"), JsonSerializer.Serialize(
            new { schema_version = 2, format = "wandur.directory", fetched_at = DateTimeOffset.UtcNow, worlds = new[] { themed, plain } },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
        using var http = new HttpClient(new Offline());
        using var catalog = new WorldCatalog(Path.Combine(path, "directory.json"), http: http);
        var store = new SettingsStore(Path.Combine(path, "settings.json"));
        store.Save(new ClientSettings { Theme = "Paper", Profiles = [themed.ToProfile()] });
        await using var sessions = new SessionWorkspace(new TranscriptDisplayFactory(), store, new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore(), catalog: catalog);
        var paper = Color.Parse(UserTheme.FromPreset("Paper").Colors["Terminal"]);
        var window = new Window { Content = new TerminalView(sessions.Active.Controller) };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(paper, Resource("TerminalBrush"));

            using var browser = new WorldBrowserViewModel(catalog, sessions);
            browser.Attach(action => action());
            browser.SelectedWorld = catalog.Worlds.Single(w => w.Id == "themed");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(paper, Resource("TerminalBrush"));
            Assert.Equal(ThemeVariant.Light, Application.Current!.RequestedThemeVariant);

            var library = new WorldLibraryViewModel(sessions, () => { }, null, null);
            library.Attach();
            library.SelectedProfile = library.Profiles.Single();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(paper, Resource("TerminalBrush"));

            await sessions.OpenAsync(store.Load().Settings.Profiles[0]);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
            Dispatcher.UIThread.RunJobs();
            var session = sessions.Active;
            Assert.Equal(Color.Parse(theme.Colors.Terminal), Resource("TerminalBrush"));
            Assert.Equal(ThemeVariant.Dark, Application.Current.RequestedThemeVariant);

            // A second, unthemed tab owns the appearance while it is active, and switching back restores it.
            await sessions.OpenAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(paper, Resource("TerminalBrush"));
            sessions.Select(session); Dispatcher.UIThread.RunJobs();
            Assert.Equal(Color.Parse(theme.Colors.Terminal), Resource("TerminalBrush"));

            browser.SelectedWorld = catalog.Worlds.Single(w => w.Id == "plain");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(Color.Parse(theme.Colors.Terminal), Resource("TerminalBrush"));

            await sessions.CloseAsync(session); Dispatcher.UIThread.RunJobs();
            Assert.Equal(paper, Resource("TerminalBrush"));
            Assert.Equal(ThemeVariant.Light, Application.Current.RequestedThemeVariant);
            library.Detach(); browser.Detach();
        }
        finally { window.Close(); ThemeService.Apply(new()); }
    }

    [AvaloniaFact]
    public async Task ThemeSwitchesStayFastWithASessionOnScreen()
    {
        var path = Path.Combine(Path.GetTempPath(), "wandur-theme-speed-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        var store = new SettingsStore(Path.Combine(path, "settings.json"));
        store.Save(new ClientSettings { Theme = "Ember" });
        var window = new MainWindow(new TranscriptDisplayFactory(), store, new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            await window.Controller.StartAsync(); Dispatcher.UIThread.RunJobs();
            for (var line = 0; line < 400; line++) window.Controller.Terminal.Append($"\x1b[3{line % 8}mroom {line} of the transcript\x1b[0m\r\n");
            window.Controller.FlushOutput();
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            Time(window, "Ember", "Moonlight");

            var sameVariant = Time(window, "Ember", "Moonlight");
            var toLight = Time(window, "Moonlight", "Paper");
            var toDark = Time(window, "Paper", "Ember");
            output.WriteLine($"dark to dark {sameVariant:F1} ms, dark to light {toLight:F1} ms, light to dark {toDark:F1} ms");
            // Generous ceilings: the point is that a switch no longer rewrites the resource dictionary
            // key by key, which cost about seventy milliseconds here and far more in a full window.
            Assert.True(sameVariant < 500, $"dark to dark took {sameVariant:F1} ms");
            Assert.True(toLight < 1500, $"dark to light took {toLight:F1} ms");
            Assert.True(toDark < 1500, $"light to dark took {toDark:F1} ms");
        }
        finally { window.Close(); await window.Sessions.DisposeAsync(); ThemeService.Apply(new()); }
    }

    private static double Time(MainWindow window, string from, string to)
    {
        var best = double.MaxValue;
        for (var run = 0; run < 3; run++)
        {
            ThemeService.Apply(window.Controller.Settings with { Theme = from });
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            var watch = Stopwatch.StartNew();
            ThemeService.Apply(window.Controller.Settings with { Theme = to });
            watch.Stop();
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
        }
        return best;
    }

    [AvaloniaFact]
    public async Task ALiveSwitchPaintsTheSameWindowAsStartingInThatTheme()
    {
        // Brushes are now updated in place, so this proves the in-place update still repaints everything.
        var live = await Paint("Ember", "Paper");
        var fresh = await Paint("Paper", null);
        // Avalonia's own transparency fallback layer keeps the variant it was created with. This client
        // never enables window transparency, so that layer sits behind an opaque background and never shows.
        var keys = live.Brushes.Keys.Union(fresh.Brushes.Keys)
            .Where(key => !key.StartsWith("Border:PART_TransparencyFallback", StringComparison.Ordinal))
            .OrderBy(key => key).ToArray();
        var differences = keys.Where(key => live.Brushes.GetValueOrDefault(key) != fresh.Brushes.GetValueOrDefault(key))
            .Select(key => $"{key}: live {live.Brushes.GetValueOrDefault(key)}, fresh {fresh.Brushes.GetValueOrDefault(key)}").ToArray();
        Assert.Empty(differences);
        var changed = 0;
        for (var i = 0; i + 3 < Math.Min(live.Pixels.Length, fresh.Pixels.Length); i += 4)
            if (live.Pixels[i] != fresh.Pixels[i] || live.Pixels[i + 1] != fresh.Pixels[i + 1] || live.Pixels[i + 2] != fresh.Pixels[i + 2]) changed++;
        output.WriteLine($"{live.Brushes.Count} controls compared, {changed} of {live.Pixels.Length / 4} pixels differ");
        // Text antialiasing is not bit exact between renders, so allow a hair of drift.
        Assert.True(changed * 1000 < live.Pixels.Length / 4, $"{changed} pixels differ after a live switch");
    }

    private sealed record Painted(Dictionary<string, string> Brushes, byte[] Pixels);

    private static async Task<Painted> Paint(string start, string? switchTo)
    {
        var path = Path.Combine(Path.GetTempPath(), "wandur-theme-paint-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        var store = new SettingsStore(Path.Combine(path, "settings.json"));
        store.Save(new ClientSettings { Theme = start });
        var window = new MainWindow(new TranscriptDisplayFactory(), store, new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            await window.Controller.StartAsync(); Dispatcher.UIThread.RunJobs();
            window.Controller.Terminal.Append("plain \x1b[31mred\x1b[0m \x1b[34mblue\x1b[0m\r\n");
            window.Controller.FlushOutput(); Dispatcher.UIThread.RunJobs();
            if (switchTo is not null)
            {
                window.Sessions.PreviewAppearanceSettings(window.Controller.Settings with { Theme = switchTo });
                Dispatcher.UIThread.RunJobs();
            }
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(4);
            var brushes = new Dictionary<string, string>();
            var seen = new Dictionary<string, int>();
            foreach (var visual in window.GetVisualDescendants())
            {
                var id = $"{visual.GetType().Name}:{(visual as Control)?.Name ?? "-"}";
                seen[id] = seen.GetValueOrDefault(id) + 1;
                var foreground = visual is TextBlock text ? text.Foreground : (visual as TemplatedControl)?.Foreground;
                var background = visual is Border border ? border.Background : (visual as TemplatedControl)?.Background ?? (visual as Panel)?.Background;
                var edge = visual is Border framed ? framed.BorderBrush : (visual as TemplatedControl)?.BorderBrush;
                brushes[$"{id}#{seen[id]}"] = $"fg={Describe(foreground)} bg={Describe(background)} edge={Describe(edge)}";
            }
            using var frame = window.CaptureRenderedFrame()!;
            var size = frame.PixelSize;
            var buffer = new byte[size.Width * size.Height * 4];
            var pinned = System.Runtime.InteropServices.GCHandle.Alloc(buffer, System.Runtime.InteropServices.GCHandleType.Pinned);
            try { frame.CopyPixels(new PixelRect(0, 0, size.Width, size.Height), pinned.AddrOfPinnedObject(), buffer.Length, size.Width * 4); }
            finally { pinned.Free(); }
            return new(brushes, buffer);
        }
        finally { window.Close(); await window.Sessions.DisposeAsync(); }
    }

    private static string Describe(IBrush? brush) => brush switch
    {
        null => "none",
        ISolidColorBrush solid => solid.Color.ToString(),
        GradientBrush gradient => "gradient(" + string.Join(",", gradient.GradientStops.Select(stop => stop.Color)) + ")",
        _ => brush.GetType().Name
    };

    private sealed class Offline : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
