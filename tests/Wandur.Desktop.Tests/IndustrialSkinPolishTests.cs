using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Discovery;
using Wandur.Core.Settings;

namespace Wandur.Desktop.Tests;

/// <summary>Industrial skin polish: dark terminal on a light frame, terminal-field chrome, multi-size captures.</summary>
public sealed class IndustrialSkinPolishTests
{
    private static WorldTheme Theme => JsonSerializer.Deserialize<WorldTheme>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "world-theme-industrial-skin.json")))!;

    private static string ColorOf(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color.ToString().ToLowerInvariant();

    [AvaloniaFact]
    public async Task IndustrialSkinUsesDarkTerminalAndTerminalFieldChrome()
    {
        var path = Path.Combine(Path.GetTempPath(), "wandur-industrial-polish-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var world = new WorldListing
        {
            Id = "industrial", Name = "Industrial", Host = "127.0.0.1",
            Port = ((IPEndPoint)listener.LocalEndpoint).Port, Theme = Theme,
            Summary = "Polish", Description = "Dark terminal on light metal."
        };
        var store = new SettingsStore(Path.Combine(path, "settings.json"));
        store.Save(new ClientSettings { Theme = "Paper", Profiles = [world.ToProfile() with { Theme = Theme }] });
        File.WriteAllText(Path.Combine(path, "directory.json"), JsonSerializer.Serialize(
            new { schema_version = 2, format = "wandur.directory", fetched_at = DateTimeOffset.UtcNow, worlds = new[] { world } },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
        using var catalog = new WorldCatalog(Path.Combine(path, "directory.json"));
        var window = new MainWindow(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), store,
            new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(),
            new MemoryScriptLibraryStore(), catalog: catalog);
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            await window.Sessions.OpenAsync(window.Controller.Settings.Profiles[0]);
            using var _ = await listener.AcceptTcpClientAsync();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("#ff101820", ColorOf((IBrush)Application.Current!.Resources["TerminalBrush"]!));
            Assert.Equal("#ffdce6ee", ColorOf((IBrush)Application.Current!.Resources["TerminalTextBrush"]!));
            Assert.True(UserTheme.IsLightBackground(Theme.Colors.Shell));
            Assert.False(UserTheme.IsLightBackground(Theme.Colors.Terminal));
            Assert.True(Application.Current.Resources.ContainsKey("TerminalPlaceholderForeground"));

            var input = window.GetVisualDescendants().OfType<TextBox>().Single(b => b.Name == "CommandInput");
            Assert.Contains(input.Classes, c => c == "terminal-field");
            Assert.Equal("#ffdce6ee", ColorOf(input.CaretBrush));

            if (Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR") is { } captures)
            {
                Directory.CreateDirectory(captures);
                foreach (var (name, size) in new[] { ("industrial-1380x900", new Size(1380, 900)), ("industrial-1040x680", new Size(1040, 680)), ("industrial-1920x1080", new Size(1920, 1080)) })
                {
                    window.Width = size.Width; window.Height = size.Height;
                    Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
                    using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
                    frame!.Save(Path.Combine(captures, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
                }
            }
        }
        finally { await window.Sessions.DisposeAsync(); window.Close(); if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
}
