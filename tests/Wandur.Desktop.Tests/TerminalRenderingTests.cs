using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Wandur.Desktop.Terminal;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Settings;
using Wandur.Desktop;
using Wandur.Desktop.Views;
using Surface = Iciclecreek.Terminal.TerminalView;

namespace Wandur.Desktop.Tests;

public sealed class TerminalRenderingTests
{
    [AvaloniaFact]
    public async Task ResizingFollowsActualBufferBottomAndPreservesReadingScrollback()
    {
        await using var controller = new WorkspaceController(new TranscriptDisplayFactory(), new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid()+".json")), new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        var window = new Window { Width = 900, Height = 750, Content = new TerminalView(controller) }; window.Show();
        try
        {
            var surface = Assert.Single(window.GetVisualDescendants().OfType<Surface>());
            controller.Terminal.Append("Short output.\r\n");
            window.Height = 500; Dispatcher.UIThread.RunJobs();
            Assert.True(controller.Display.IsFollowingTail);
            for (var i = 0; i < 100; i++) controller.Terminal.Append($"Line {i}\r\n");
            Assert.Equal(surface.Terminal.Buffer.YBase, surface.ViewportY);
            surface.ViewportY = 2;
            window.Height = 450; Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, surface.ViewportY);
            Assert.False(controller.Display.IsFollowingTail);
            controller.Display.FollowTail();
            Assert.Equal(surface.Terminal.Buffer.YBase, surface.ViewportY);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ServerCursorMovementReplacesCellsAndSurvivesReattachment()
    {
        await using var controller = new WorkspaceController(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid()+".json")), new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        var view = new TerminalView(controller);
        var window = new Window { Width = 900, Height = 550, Content = view };
        window.Show();
        try
        {
            var surface = Assert.Single(window.GetVisualDescendants().OfType<Surface>());
            controller.Terminal.Append("first\r\nsecond\x1b[1A\rFIRST");
            Dispatcher.UIThread.RunJobs();
            Assert.StartsWith("FIRST", surface.Terminal.Buffer.GetLine(0)!.TranslateToString(true));
            window.Content = null;
            controller.Terminal.Append("\r\n\x1b[2KSECOND");
            window.Content = view;
            Dispatcher.UIThread.RunJobs();
            Assert.StartsWith("SECOND", surface.Terminal.Buffer.GetLine(1)!.TranslateToString(true));
            Assert.Equal("FIRST\nSECOND", controller.Display.PlainText);
            controller.ClearTranscript();
            Assert.True(string.IsNullOrWhiteSpace(surface.Terminal.Buffer.GetLine(0)!.TranslateToString(true)));
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task LocalEchoPreservesServerSavedCursorAndPartialEscape()
    {
        await using var controller = new WorkspaceController(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid()+".json")), new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        var window = new Window { Width = 900, Height = 550, Content = new TerminalView(controller) };
        window.Show();
        try
        {
            var surface = Assert.Single(window.GetVisualDescendants().OfType<Surface>());
            Assert.Equal("", surface.Process);
            controller.Terminal.Append("abc\x1b" + "7\r\nserver\x1b[3");
            controller.Terminal.AppendLocalText("local\n");
            controller.Terminal.Append("1mRED\x1b" + "8!");
            Assert.StartsWith("abc!", surface.Terminal.Buffer.GetLine(0)!.TranslateToString(true));
            Assert.StartsWith("serverlocal", surface.Terminal.Buffer.GetLine(1)!.TranslateToString(true));
            Assert.StartsWith("RED", surface.Terminal.Buffer.GetLine(2)!.TranslateToString(true));
            Assert.Equal(1, surface.Terminal.Buffer.GetLine(2)![0].Attributes.GetFgColor());
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task OutputBeforeFirstAttachIsRetainedAndScrollbackIsBounded()
    {
        await using var controller = new WorkspaceController(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid()+".json")), new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        for (var i = 0; i < 2500; i++) controller.Terminal.Append($"room {i}\r\n");
        var window = new Window { Width = 900, Height = 550, Content = new TerminalView(controller) };
        window.Show();
        try
        {
            var surface = Assert.Single(window.GetVisualDescendants().OfType<Surface>());
            Assert.True(surface.Terminal.Buffer.Lines.Length <= 2000 + surface.Terminal.Rows);
            var lines = Enumerable.Range(0, surface.Terminal.Buffer.Lines.Length).Select(i => surface.Terminal.Buffer.GetLine(i)!.TranslateToString(true)).ToArray();
            Assert.Contains("room 2499", lines);
            Assert.DoesNotContain("room 0", lines);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task TextBlinkIsOptInWorksWithoutOutputFocusAndPreservesCellAttributes()
    {
        await using var controller = new WorkspaceController(new TranscriptDisplayFactory(), new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid()+".json")), new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        var window = new Window { Width = 900, Height = 550, Content = new TerminalView(controller) };
        window.Show();
        try
        {
            var surface = Assert.Single(window.GetVisualDescendants().OfType<MudTerminalSurface>());
            controller.Terminal.Append("\x1b[5;31mBLINK\x1b[0m stable");
            Assert.False(surface.AllowBlink);
            Assert.False(surface.IsFocused);
            Assert.False(surface.CursorBlink);
            var steady = Capture(surface);
            await Task.Delay(600);
            Assert.Equal(steady, Capture(surface));
            controller.ApplySettings(controller.Settings with { AllowBlinkingText = true });
            Assert.True(surface.AllowBlink);
            var visible = Capture(surface);
            byte[] hidden = visible;
            for (int i = 0; i < 15 && hidden.SequenceEqual(visible); i++) { await Task.Delay(100); hidden = Capture(surface); }
            Assert.False(visible.SequenceEqual(hidden), "Blinking text must animate while the output has no focus.");
            var cell = surface.Terminal.Buffer.GetLine(0)![0];
            Assert.True(cell.Attributes.IsBlink());
            Assert.False(cell.Attributes.IsInvisible());
            controller.ApplySettings(controller.Settings with { AllowBlinkingText = false });
            Assert.Equal(steady, Capture(surface));
        }
        finally { window.Close(); }
    }


    [AvaloniaFact]
    public async Task FragmentedEraseDoesNotResetTheStreamOrServerStyle()
    {
        await using var controller = new WorkspaceController(new TranscriptDisplayFactory(), new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid()+".json")), new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        var window = new Window { Width = 900, Height = 550, Content = new TerminalView(controller) }; window.Show();
        try
        {
            var surface = Assert.Single(window.GetVisualDescendants().OfType<Surface>());
            controller.Terminal.Append("\x1b[31mbefore\r\x1b[");
            controller.Terminal.Append("2Jafter");
            Assert.Equal("after", surface.Terminal.Buffer.GetLine(0)!.TranslateToString(true));
            Assert.Equal(1, surface.Terminal.Buffer.GetLine(0)![0].Attributes.GetFgColor());
        }
        finally { window.Close(); }
    }


    [Fact]
    public void CharacterSetSelectionDoesNotHoldANumericPrompt()
    {
        var framer = new TerminalStreamFramer();
        Assert.Equal("\x1b(0", framer.Feed("\x1b(0"));
        Assert.Equal("123 > ", framer.Feed("123 > "));
    }

    [AvaloniaFact]
    public async Task OutputSupportsSelectAllAndCopyWithoutAPty()
    {
        await using var controller = new WorkspaceController(new TranscriptDisplayFactory(), new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid()+".json")), new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        var window = new Window { Width = 900, Height = 550, Content = new TerminalView(controller) }; window.Show();
        try
        {
            var surface = Assert.Single(window.GetVisualDescendants().OfType<Surface>());
            controller.Terminal.Append("A MUD transcript"); surface.Focus();
            var modifier = OperatingSystem.IsMacOS() ? Avalonia.Input.RawInputModifiers.Meta : Avalonia.Input.RawInputModifiers.Control;
            window.KeyPressQwerty(PhysicalKey.A, modifier); window.KeyReleaseQwerty(PhysicalKey.A, modifier);
            Assert.True(surface.Terminal.Selection.HasSelection);
            window.KeyPressQwerty(PhysicalKey.C, modifier); window.KeyReleaseQwerty(PhysicalKey.C, modifier);
            Dispatcher.UIThread.RunJobs();
            Assert.StartsWith("A MUD transcript", await window.Clipboard!.TryGetTextAsync());
        }
        finally { window.Close(); }
    }


    [AvaloniaFact]
    public async Task SelectionIsATranslucentHighlightAndTheSelectedTextStaysVisible()
    {
        await using var controller = new WorkspaceController(new TranscriptDisplayFactory(), new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid()+".json")), new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        var window = new Window { Width = 900, Height = 550, Content = new TerminalView(controller) }; window.Show();
        try
        {
            var surface = Assert.Single(window.GetVisualDescendants().OfType<Surface>());
            for (var i = 0; i < 8; i++) controller.Terminal.Append($"Line {i}: the quick brown fox jumps over the lazy dog\r\n");
            controller.ApplySettings(controller.Settings); Dispatcher.UIThread.RunJobs();
            // A mouse drag from the second line to the fifth, whole rows in between, like the owner's screenshot.
            surface.Terminal.Selection.StartSelection(0, 1);
            surface.Terminal.Selection.UpdateSelection(surface.Terminal.Cols - 1, 4);
            surface.Terminal.Selection.EndSelection();
            Assert.True(surface.Terminal.Selection.HasSelection);
            surface.InvalidateVisual();
            Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
            if (Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR") is { } folder)
            { Directory.CreateDirectory(folder); frame.Save(Path.Combine(folder, "transcript-selection.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions()); }
            using var stream = new MemoryStream(); frame.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            using var pixels = SkiaSharp.SKBitmap.Decode(stream.ToArray());
            var scaleX = pixels.Width / window.Bounds.Width; var scaleY = pixels.Height / window.Bounds.Height;
            var origin = surface.TranslatePoint(new Point(0, 0), window)!.Value;
            var background = ((ISolidColorBrush)window.FindResource("TerminalBrush")!).Color;
            var text = ((ISolidColorBrush)window.FindResource("TerminalTextBrush")!).Color;
            // The third line sits wholly inside the selection: its glyphs must still read as text, and the
            // empty cells beyond the text must carry the highlight, so neither the text nor the tint is lost.
            var top = (int)Math.Round((origin.Y + 2 * surface.CharHeight) * scaleY); var bottom = (int)Math.Round((origin.Y + 3 * surface.CharHeight) * scaleY);
            var left = (int)Math.Round(origin.X * scaleX); var textRight = (int)Math.Round((origin.X + 50 * surface.CharWidth) * scaleX);
            var right = (int)Math.Round((origin.X + surface.Bounds.Width - 20) * scaleX);
            static int Distance(SkiaSharp.SKColor pixel, Color color) => Math.Abs(pixel.Red - color.R) + Math.Abs(pixel.Green - color.G) + Math.Abs(pixel.Blue - color.B);
            var glyphs = 0; var tinted = 0;
            for (var y = top; y < bottom; y++)
            {
                for (var x = left; x < textRight; x++) if (Distance(pixels.GetPixel(x, y), text) < Distance(pixels.GetPixel(x, y), background)) glyphs++;
                for (var x = textRight; x < right; x++) if (Distance(pixels.GetPixel(x, y), background) > 24) tinted++;
            }
            Assert.True(glyphs > 20, $"selected text must stay legible on top of the highlight; {glyphs} text-colored pixels in the selected row");
            Assert.True(tinted > 0, "the selection highlight must be visible where the row has no text");
            var brush = Assert.IsAssignableFrom<ISolidColorBrush>(surface.SelectionBrush);
            Assert.Equal(ThemeService.TranscriptSelectionAlpha, brush.Color.A);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task LocalEchoUsesLiteralCharactersEvenWhenServerSelectsLineDrawing()
    {
        await using var controller = new WorkspaceController(new TranscriptDisplayFactory(), new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid()+".json")), new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        var window = new Window { Width = 900, Height = 550, Content = new TerminalView(controller) }; window.Show();
        try
        {
            controller.Terminal.Append("\x1b(0");
            controller.Terminal.AppendLocalText("look\n");
            controller.Terminal.Append("qq");
            Assert.StartsWith("look\n", controller.Display.PlainText);
            Assert.EndsWith("──", controller.Display.PlainText);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task SettingsPreviewAppliesBlinkAndFontThenCancelRestoresThem()
    {
        await using var sessions = new SessionWorkspace(new TranscriptDisplayFactory(), new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid()+".json")), new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        var window = new Window { Width = 900, Height = 550, Content = new TerminalView(sessions.Active.Controller) }; window.Show();
        try
        {
            var surface = Assert.Single(window.GetVisualDescendants().OfType<MudTerminalSurface>());
            sessions.PreviewAppearanceSettings(sessions.Active.Controller.Settings with { AllowBlinkingText = true, FontSize = 20 });
            Assert.True(surface.AllowBlink); Assert.Equal(20, surface.FontSize);
            sessions.EndAppearanceSettingsPreview();
            Assert.False(surface.AllowBlink); Assert.Equal(15, surface.FontSize);
        }
        finally { window.Close(); }
    }

    private static byte[] Capture(Surface surface)
    {
        Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        using var bitmap = new RenderTargetBitmap(new Avalonia.PixelSize((int)surface.Bounds.Width, (int)surface.Bounds.Height));
        bitmap.Render(surface);
        using var stream = new MemoryStream(); bitmap.Save(stream, new PngBitmapEncoderOptions());
        return stream.ToArray();
    }

}
