using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Diagnostics;
using Wandur.Core.Settings;
using Wandur.Desktop.ViewModels;
using Wandur.Desktop.Views;

namespace Wandur.Desktop.Tests;

public sealed class DiagnosticsContrastTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmbeddedDiagnosticsRemainReadableAcrossPagesAndThemeSwitches(bool populated)
    {
        var directory = Path.Combine(Path.GetTempPath(), "wandur-diagnostics-contrast-" + Guid.NewGuid());
        await using var controller = new WorkspaceController(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(),
            new SettingsStore(Path.Combine(directory, "settings.json")), new MemoryPasswordVault(),
            new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        var view = new TerminalView(controller);
        var window = new Window { Content = view, Width = 1000, Height = 720 };
        window.Classes.Add("fleet");
        try
        {
            window.Show();
            controller.Pages.SelectedIndex = (int)SessionPage.Diagnostics;
            if (populated)
            {
                controller.Diagnostics.Append(DateTimeOffset.Now, 201,
                    Encoding.UTF8.GetBytes("Room.Info {\"name\":\"Observation Deck\",\"level\":4,\"indoors\":true}"));
                controller.ConsoleLog.Append(ConsoleEntryKind.Received, "Observation Deck\r\n", false);
            }
            Layout(window);
            var diagnostics = view.GetVisualDescendants().OfType<ProtocolDiagnosticsView>().Single();
            var tabs = diagnostics.GetVisualDescendants().OfType<TabControl>().Single();
            var footer = view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "OutputFooter");
            var failures = new List<string>();
            foreach (var theme in new[] { "Hull", "Slate", "Paper", "Hull" })
            {
                ThemeService.Apply(new ClientSettings { Theme = theme });
                for (var page = 0; page < 3; page++)
                {
                    tabs.SelectedIndex = page;
                    Layout(window);
                    Assert.True(diagnostics.IsEffectivelyVisible);
                    failures.AddRange(ContrastProbe.Scan(diagnostics).Concat(ContrastProbe.Scan(footer))
                        .Select(f => $"{theme}, page {page}: {f}"));
                    foreach (var editor in diagnostics.GetVisualDescendants().OfType<DiagnosticsBodyEditor>()
                                 .Where(e => e.IsEffectivelyVisible))
                    {
                        var surface = Assert.IsAssignableFrom<ISolidColorBrush>(editor.Background).Color;
                        var ink = Assert.IsAssignableFrom<ISolidColorBrush>(editor.Foreground).Color;
                        Assert.True(ContrastProbe.Contrast(ink, surface) >= 4.5, $"{theme} editor plain text");
                        if (editor.SyntaxHighlighting is not { } syntax) continue;
                        foreach (var name in new[] { "FieldName", "String", "Number", "Bool", "Null", "Punctuation" })
                            Assert.True(ContrastProbe.Contrast(syntax.GetNamedColor(name).Foreground.GetColor(null)!.Value, surface) >= 4.5,
                                $"{theme} editor {name}");
                    }
                    if (Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR") is { Length: > 0 } captureDirectory)
                    {
                        Directory.CreateDirectory(captureDirectory);
                        using var frame = window.CaptureRenderedFrame();
                        Assert.NotNull(frame);
                        frame.Save(Path.Combine(captureDirectory, $"diagnostics-{theme}-{page}-{populated}.png"), new PngBitmapEncoderOptions());
                    }
                }
            }
            Assert.True(failures.Count == 0, string.Join("\n", failures.Distinct()));
        }
        finally
        {
            window.Close();
            ThemeService.Apply(new ClientSettings());
        }
    }

    private static void Layout(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
    }
}
