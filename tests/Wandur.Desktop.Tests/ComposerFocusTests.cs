using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Settings;
using Wandur.Desktop.Terminal;
using Wandur.Desktop.Views;

namespace Wandur.Desktop.Tests;

/// <summary>The command box takes the keyboard when a session opens, when its tab comes to the front, and on a plain click in the transcript.</summary>
public sealed class ComposerFocusTests
{
    private static MainWindow CreateWindow()
    {
        var window = new MainWindow(new TranscriptDisplayFactory(), new SettingsStore(Path.Combine(Path.GetTempPath(), "wandur-focus-" + Guid.NewGuid(), "settings.json")), new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static T Find<T>(Visual root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
    private static TextBox Composer(Visual root) => Find<TextBox>(root, "CommandInput");

    [AvaloniaFact]
    public async Task OpeningAWorldFocusesTheComposerAndSoDoesBringingItsTabBack()
    {
        var window = CreateWindow();
        try
        {
            var elsewhere = Find<Button>(window, "AddSavedWorld");
            elsewhere.Focus();
            Assert.True(elsewhere.IsFocused);
            await window.Sessions.OpenAsync();
            await ScriptSessionTests.WaitFor(() => window.Controller.IsConnected);
            Dispatcher.UIThread.RunJobs();
            var first = window.Sessions.Active;
            var composer = Composer(window);
            Assert.True(composer.IsFocused, "the composer takes focus once the session is connected");

            // A second session: its own composer takes over.
            await window.Sessions.OpenAsync();
            await ScriptSessionTests.WaitFor(() => window.Sessions.Active.Controller.IsConnected);
            Dispatcher.UIThread.RunJobs();
            var second = window.Sessions.Active;
            Assert.NotSame(first, second);
            Assert.True(Composer(window).IsFocused);

            // Focus wanders to the library; choosing the first tab again brings it back to that tab's composer.
            elsewhere.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.True(elsewhere.IsFocused);
            Assert.False(Composer(window).IsFocused);
            window.Sessions.Select(first);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(first, window.Sessions.Active);
            Assert.True(Composer(window).IsFocused, "switching tabs focuses the composer of the tab shown");
        }
        finally { await window.Sessions.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task ADialogOpenWhenTheSessionConnectsKeepsTheKeyboard()
    {
        var window = CreateWindow();
        try
        {
            window.Controller.SaveSettings(window.Controller.Settings with { Profiles = [new ConnectionProfile { Name = "Saved", Host = "saved.example.org" }] });
            Dispatcher.UIThread.RunJobs();
            Find<Button>(window, "EditSavedWorld").Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var dialog = Assert.Single(window.OwnedWindows.OfType<ProfileDialog>());
            var name = Find<TextBox>(dialog, "WorldName");
            name.Focus();
            Assert.True(name.IsFocused);
            Assert.Same(name, window.FocusManager!.GetFocusedElement());
            await window.Sessions.OpenAsync();
            await ScriptSessionTests.WaitFor(() => window.Controller.IsConnected);
            Dispatcher.UIThread.RunJobs();
            Assert.True(name.IsFocused, "the dialog keeps the keyboard");
            Assert.False(Composer(window).IsFocused);
            dialog.Close();
        }
        finally { await window.Sessions.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task AClickOnTheTranscriptFocusesTheComposerButADragThatSelectsTextDoesNot()
    {
        await using var controller = new WorkspaceController(new TranscriptDisplayFactory(), new SettingsStore(Path.Combine(Path.GetTempPath(), "wandur-click-" + Guid.NewGuid() + ".json")), new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        var other = new TextBox { Name = "Elsewhere", Width = 200 };
        var view = new TerminalView(controller);
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Children = { other, view } };
        Grid.SetRow(view, 1);
        var window = new Window { Width = 900, Height = 600, Content = layout };
        window.Show();
        try
        {
            await controller.StartAsync();
            await ScriptSessionTests.WaitFor(() => controller.IsConnected);
            for (var i = 0; i < 20; i++) controller.Terminal.Append($"Line {i} of a transcript with words to select\r\n");
            Dispatcher.UIThread.RunJobs();
            var composer = Composer(window);
            var surface = Assert.Single(window.GetVisualDescendants().OfType<MudTerminalSurface>());
            other.Focus();
            Assert.True(other.IsFocused);

            // A click: down and up in the same place, nothing selected.
            var start = surface.TranslatePoint(new Point(30, 20), window)!.Value;
            window.MouseDown(start, MouseButton.Left); window.MouseUp(start, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.False(controller.Display.HasSelection);
            Assert.True(composer.IsFocused, "a plain click on the transcript focuses the composer");

            // A drag across text: the selection stays and the keyboard is not moved away from it.
            other.Focus();
            var end = start + new Vector(200, 0);
            window.MouseDown(start, MouseButton.Left); window.MouseMove(start + new Vector(60, 0)); window.MouseMove(end); window.MouseUp(end, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.True(controller.Display.HasSelection, "dragging across text selects it");
            Assert.False(composer.IsFocused, "a drag that selected text leaves focus alone");

            // A right click is not a click to type.
            other.Focus();
            surface.Terminal.Selection.ClearSelection();
            window.MouseDown(start, MouseButton.Right); window.MouseUp(start, MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
            Assert.False(composer.IsFocused);

            // A click on the tail pane below the divider counts the same as one on the transcript.
            controller.ApplySettings(controller.Settings with { ScrollTailShare = 0.25 });
            surface.ViewportY = 2; Dispatcher.UIThread.RunJobs();
            var tail = Assert.Single(window.GetVisualDescendants().OfType<TranscriptTailPane>());
            Assert.True(tail.IsVisible);
            var onTail = tail.TranslatePoint(new Point(tail.Bounds.Width / 2, tail.Bounds.Height / 2), window)!.Value;
            window.MouseDown(onTail, MouseButton.Left); window.MouseUp(onTail, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.True(composer.IsFocused, "a click on the live view focuses the composer");
        }
        finally { window.Close(); }
    }
}
