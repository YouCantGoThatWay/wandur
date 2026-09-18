using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Wandur.Core.Settings;
using Wandur.Core.Terminal;
using Wandur.Desktop;
using Wandur.Desktop.Terminal;

namespace Wandur.Desktop.Tests;

public sealed class TerminalReplayTests
{
    // A repeatable local observation, not a timing threshold or a general FPS benchmark.
    [AvaloniaFact]
    public void BusyTranscriptReplayRendersAndRetainsTheNewestOutput()
    {
        ThemeService.Apply(new ClientSettings());
        var source = new AnsiTerminal();
        using var display = new TranscriptDisplayFactory().Create(source);
        display.ApplySettings(new());
        var window = new Window { Width = 900, Height = 500, Content = display.View };
        window.Show();
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/terminal"));
        Directory.CreateDirectory(directory);
        try
        {
            var initial = string.Concat(Enumerable.Range(0, 1800).Select(Line));
            var chunks = Enumerable.Range(0, 12).Select(i => string.Concat(Enumerable.Range(1800 + i * 10, 10).Select(Line))).ToArray();
            source.Append(initial); Paint(window);
            var watch = Stopwatch.StartNew();
            foreach (var chunk in chunks) { source.Append(chunk); Paint(window); }
            watch.Stop(); var incremental = watch.Elapsed.TotalMilliseconds;
            Assert.Contains("Room 1919", source.PlainText);
            using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(directory, "dedicated-terminal.png"), new PngBitmapEncoderOptions());

            // Reproduce the previous renderer's full retained-inline rebuild on each refresh.
            var baseline = new AnsiTerminal(); baseline.Append(initial);
            var text = new SelectableTextBlock { FontSize = 15, LineHeight = 24.75, FontFamily = new FontFamily("Menlo, Consolas, DejaVu Sans Mono"), TextWrapping = TextWrapping.Wrap };
            var scroll = new ScrollViewer { Content = text };
            window.Content = scroll;
            Rebuild(); Paint(window);
            watch.Restart();
            foreach (var chunk in chunks) { baseline.Append(chunk); Rebuild(); Paint(window); }
            watch.Stop();
            File.WriteAllText(Path.Combine(directory, "replay-timing.json"), JsonSerializer.Serialize(new
            {
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                platform = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                historyLines = 1800, batches = 12, linesPerBatch = 10,
                incrementalMilliseconds = incremental, fullInlineRebuildMilliseconds = watch.Elapsed.TotalMilliseconds,
                note = "Headless Skia, 900x500, feed + layout + forced render; illustrative single run, reconstructed previous renderer, no network."
            }, new JsonSerializerOptions { WriteIndented = true }));

            void Rebuild()
            {
                var runs = new InlineCollection();
                foreach (var line in baseline.Lines)
                {
                    foreach (var run in line.Runs) runs.Add(new Run(run.Text) { Foreground = run.Style.Foreground is { } color ? Brush.Parse(color) : Brushes.LightGray, FontWeight = run.Style.Bold ? FontWeight.Bold : FontWeight.Normal });
                    runs.Add(new Run("\n"));
                }
                text.Inlines = runs; window.UpdateLayout(); scroll.ScrollToEnd();
            }
        }
        finally { window.Close(); }
    }
    private static string Line(int i) => $"\x1b[36mRoom {i:0000}\x1b[0m  A stone corridor stretches north. \x1b[32mExits: north south east\x1b[0m\r\n";
    private static void Paint(Window window)
    { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(1); }
}
