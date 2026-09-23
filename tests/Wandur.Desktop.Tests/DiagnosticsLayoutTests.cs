using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Diagnostics;
using Wandur.Desktop.ViewModels;
using Wandur.Desktop.Views;

namespace Wandur.Desktop.Tests;

public sealed class DiagnosticsLayoutTests
{
    private static ProtocolDiagnosticsViewModel Messages()
    {
        var model = new ProtocolDiagnosticsViewModel();
        var now = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 80; i++)
            model.Append(now.AddSeconds(i), 201, Encoding.UTF8.GetBytes($"Ship.Telemetry.Sensor{i:00} {{\"status\":\"ready\",\"value\":{i}}}"));
        model.Append(now, 201, Encoding.UTF8.GetBytes("Room.Info {\"name\":\"Observation Deck\",\"description\":\"Lanterns illuminate the forward windows. A freighter passes the station.\",\"exits\":[\"north\",\"east\"],\"coordinates\":{\"x\":18,\"y\":42}}"));
        return model;
    }

    private static T Named<T>(Control view, string name) where T : Control =>
        view.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static void Layout(Window window)
    {
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
    }

    private static void Click(Window window, Control control)
    {
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
        Layout(window);
    }

    private static void Capture(Window window, string name)
    {
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        frame.Save(Path.Combine(directory, name + ".png"), new PngBitmapEncoderOptions());
    }

    private static void ScrollHorizontally(DiagnosticsBodyEditor editor, double offset)
    {
        var scroll = Named<ScrollViewer>(editor, "PART_ScrollViewer");
        scroll.Offset = new Vector(offset, scroll.Offset.Y);
    }

    [AvaloniaTheory]
    [InlineData(900, 650)]
    [InlineData(650, 650)]
    [InlineData(560, 650)]
    [InlineData(560, 480)]
    [InlineData(560, 400)]
    public void ManyKindsKeepSearchAndBothPanesUsableEvenWhenExpanded(int width, int height)
    {
        var model = Messages();
        var view = new ProtocolDiagnosticsView(model);
        var window = new Window { Content = view, Width = width, Height = height };
        try
        {
            window.Show(); Layout(window);
            var filter = Named<TextBox>(view, "DiagnosticsFilter");
            Assert.True(filter.Bounds.Width >= width - 48, "Search needs its own usable row.");
            var more = Named<ToggleButton>(view, "MoreKinds");
            Click(window, more);
            Assert.True(model.KindsExpanded);
            var chips = Named<ItemsControl>(view, "KindChips");
            var scroll = chips.GetVisualAncestors().OfType<ScrollViewer>().First();
            Assert.True(scroll.Bounds.Height <= 88, "Expanded kinds must not displace the messages.");
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
            var last = chips.GetVisualDescendants().OfType<ToggleButton>().Last();
            last.BringIntoView(); Layout(window);
            Assert.True(scroll.Offset.Y > 0);
            Click(window, last);
            Assert.Single(model.Visible);
            Click(window, Named<Button>(view, "AllKinds"));
            Assert.Equal(81, model.Visible.Count);
            scroll.Offset = default; Layout(window);
            var list = Named<ListBox>(view, "ProtocolMessages");
            var detail = Named<DiagnosticsBodyEditor>(view, "ProtocolMessageDetail");
            Assert.True(list.Bounds.Height >= (height >= 650 ? 140 : 60));
            Assert.True(detail.Bounds.Height >= (height >= 650 ? 180 : 90));
            var listOrigin = list.TranslatePoint(default, view)!.Value;
            var detailOrigin = detail.TranslatePoint(default, view)!.Value;
            if (width == 900)
            {
                Assert.True(detailOrigin.X >= listOrigin.X + list.Bounds.Width);
                Assert.True(list.Bounds.Width >= 280);
                Assert.True(detail.Bounds.Width >= 320);
            }
            else Assert.True(detailOrigin.Y >= listOrigin.Y + list.Bounds.Height);
            Assert.True(detailOrigin.Y + detail.Bounds.Height <= view.Bounds.Height + 1);
            Capture(window, $"diagnostics-messages-{width}x{height}");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ResizingChangesTheSplitWithoutLosingSelectionOrFollowState()
    {
        var model = Messages();
        var view = new ProtocolDiagnosticsView(model);
        var window = new Window { Content = view, Width = 900, Height = 650 };
        try
        {
            window.Show(); Layout(window);
            var list = Named<ListBox>(view, "ProtocolMessages");
            list.SelectedItem = model.Entries[0]; Layout(window);
            var selected = model.SelectedEntry;
            Assert.False(model.Follow);
            var splitter = view.GetVisualDescendants().OfType<GridSplitter>().Single();
            Assert.Equal(GridResizeDirection.Columns, splitter.ResizeDirection);
            window.Width = 560; Layout(window);
            Assert.Equal(GridResizeDirection.Rows, splitter.ResizeDirection);
            window.Width = 900; Layout(window);
            Assert.Equal(GridResizeDirection.Columns, splitter.ResizeDirection);
            Assert.Same(selected, list.SelectedItem);
            Assert.False(model.Follow);
            Assert.Equal(selected!.Content!.Body, Named<DiagnosticsBodyEditor>(view, "ProtocolMessageDetail").Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonWrappingBodiesCanScrollToTheEndOfALongLine(bool startWrapped)
    {
        var editor = new DiagnosticsBodyEditor(wordWrap: startWrapped) { SourceText = new string('x', 240) + "END" };
        var window = new Window { Content = editor, Width = 560, Height = 300 };
        try
        {
            window.Show(); Layout(window);
            editor.WordWrap = false; Layout(window);
            Assert.Equal(ScrollBarVisibility.Auto, editor.HorizontalScrollBarVisibility);
            ScrollHorizontally(editor, 1000); Layout(window);
            Assert.True(editor.HorizontalOffset > 0, $"offset={editor.HorizontalOffset}, extent={editor.ExtentWidth}, viewport={editor.ViewportWidth}, bounds={editor.Bounds}, text={editor.Text.Length}");
            Assert.True(editor.ExtentWidth > editor.ViewportWidth);
            editor.WordWrap = true; Layout(window);
            Assert.Equal(ScrollBarVisibility.Disabled, editor.HorizontalScrollBarVisibility);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(900)]
    [InlineData(650)]
    [InlineData(560)]
    public void SchemaAndConsoleRemainReadableAndScrollableAtSupportedWidths(int width)
    {
        var model = new ProtocolDiagnosticsViewModel();
        model.Append(DateTimeOffset.Now, 201, Encoding.UTF8.GetBytes("Room.Info {\"name\":\"Observation Deck\",\"" + new string('x', 160) + "\":1}"));
        var log = new ConsoleLog();
        log.Append(ConsoleEntryKind.Received, "Observation Deck\r\nLanterns illuminate the forward windows.\r\n", false);
        log.Append(ConsoleEntryKind.Sent, "look", false);
        log.Append(ConsoleEntryKind.Received, new string('x', 200) + "\r\n", false);
        log.Append(ConsoleEntryKind.Received, "fixture-secret", true);
        var view = new ProtocolDiagnosticsView(model, log);
        var window = new Window { Content = view, Width = width, Height = 650 };
        try
        {
            window.Show(); Layout(window);
            var tabs = Named<TabControl>(view, "ProtocolDiagnosticTabs");
            tabs.SelectedIndex = 1; Layout(window);
            var schema = Named<DiagnosticsBodyEditor>(view, "ProtocolSchemaDetail");
            ScrollHorizontally(schema, 500); Layout(window);
            Assert.True(schema.HorizontalOffset > 0, $"offset={schema.HorizontalOffset}, extent={schema.ExtentWidth}, viewport={schema.ViewportWidth}, bounds={schema.Bounds}, text={schema.Text.Length}");
            ScrollHorizontally(schema, 0); Layout(window);
            Capture(window, $"diagnostics-schema-{width}x650");
            tabs.SelectedIndex = 2; Layout(window);
            var editor = Named<DiagnosticsBodyEditor>(view, "ConsoleText");
            Assert.DoesNotContain("fixture-secret", editor.Text);
            var hint = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == Wandur.Core.Localization.Strings.ConsolePrivate);
            var pause = Named<ToggleButton>(view, "ConsolePause");
            var hintOrigin = hint.TranslatePoint(default, view)!.Value;
            var pauseOrigin = pause.TranslatePoint(default, view)!.Value;
            Assert.True(hintOrigin.Y >= pauseOrigin.Y + pause.Bounds.Height, "Privacy notice should have its own row.");
            Assert.True(pause.Bounds.Height >= 30);
            Click(window, Named<ToggleButton>(view, "ConsoleWrap"));
            Assert.False(editor.WordWrap);
            ScrollHorizontally(editor, 500); Layout(window);
            Assert.True(editor.HorizontalOffset > 0);
            ScrollHorizontally(editor, 0); Layout(window);
            Capture(window, $"diagnostics-console-{width}x650");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ConsolePrivacyNoticeDoesNotOverlapActionsAtNarrowWidths()
    {
        var view = new ConsoleView(new ConsoleLog());
        var window = new Window { Content = view, Width = 560, Height = 650 };
        try
        {
            window.Show(); Layout(window);
            var hint = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == Wandur.Core.Localization.Strings.ConsolePrivate);
            var pause = Named<ToggleButton>(view, "ConsolePause");
            Assert.True(hint.TranslatePoint(default, view)!.Value.Y >= pause.TranslatePoint(default, view)!.Value.Y + pause.Bounds.Height);
            Assert.True(pause.Bounds.Height >= 30);
        }
        finally { window.Close(); }
    }
}
