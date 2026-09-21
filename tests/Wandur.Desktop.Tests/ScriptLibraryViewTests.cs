using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Model.Core;
using Wandur.Core.Settings;
using Wandur.Desktop.Services;
using Wandur.Desktop.ViewModels;
using Wandur.Desktop.Views;
using Wandur.Core.Localization;

namespace Wandur.Desktop.Tests;

[Collection(UiLanguageCollection.Name)]
public sealed class ScriptLibraryViewTests
{
    [AvaloniaFact]
    public async Task SwitchingSessionViewsKeepsLoadedScriptsRunning()
    {
        var window = new MainWindow(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), new SettingsStore(Path.Combine(Path.GetTempPath(), "wandur-library-ui-" + Guid.NewGuid(), "settings.json")),
            new MemoryPasswordVault(), new MemoryRoomMapStore(), new InlineScriptFactory(), new MemoryScriptLibraryStore());
        try
        {
            window.Show(); await window.Sessions.OpenAsync();
            var first = window.Sessions.Active;
            first.Controller.Pages.SelectedPage = SessionPage.Diagnostics; Dispatcher.UIThread.RunJobs();
            using var model = new ScriptLibraryViewModel(first.Controller.ScriptLibrary);
            Assert.Equal(SessionPage.Diagnostics, first.Controller.Pages.SelectedPage);
            Assert.Equal(false, Avalonia.Application.Current!.Resources["DockDocumentControlTabStripVisible"]);
            Assert.Empty(window.OwnedWindows.OfType<ScriptsWindow>());
            Assert.Empty(window.GetVisualDescendants().OfType<ScriptLibraryView>());
            Assert.NotNull(first.Controller.Pages.Automation);
            model.Name = "Greeting"; model.Source = "mud.on('line', e => mud.echo(e.text));";
            await model.SaveCommand.ExecuteAsync(null);
            await model.Selected!.EnableCommand.ExecuteAsync(true);
            Assert.True(first.Controller.ScriptLibrary.Items[0].Runtime.IsRunning);
            first.Controller.Pages.SelectedPage = SessionPage.Play;
            Assert.Equal(false, Avalonia.Application.Current!.Resources["DockDocumentControlTabStripVisible"]);
            Assert.True(first.Controller.ScriptLibrary.Items[0].Runtime.IsRunning);
            first.Controller.Pages.SelectedPage = SessionPage.Diagnostics;
            Assert.Equal("Greeting", model.Name);
            await window.Sessions.OpenAsync();
            var second = window.Sessions.Active;
            var originalEditor = model;
            originalEditor.Source = "mud.echo('original only');";
            await originalEditor.SaveCommand.ExecuteAsync(null);
            Assert.DoesNotContain("original only", second.Controller.ScriptLibrary.Items[0].Source);
            await window.Sessions.CloseAsync(first); Dispatcher.UIThread.RunJobs();
            Assert.False(originalEditor.Selected!.Entry.Runtime.IsRunning);
        }
        finally { await window.Sessions.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task ReusableScriptsEditorHasCompactToolbarAndOutputIsOptional()
    {
        var path = Path.Combine(Path.GetTempPath(), "wandur-script-toolbar-" + Guid.NewGuid(), "settings.json");
        await using var controller = new WorkspaceController(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), new SettingsStore(path), new MemoryPasswordVault(), new MemoryRoomMapStore(), new InlineScriptFactory(), new MemoryScriptLibraryStore());
        await controller.StartAsync();
        using var model = new ScriptLibraryViewModel(controller.ScriptLibrary);
        var view = new ScriptLibraryView(model);
        var window = new Window { Width = 1000, Height = 700, Content = view };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<Button>(), b => b.Name == "SessionScripts");
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var editor = view;
            var source = editor.GetVisualDescendants().OfType<ScriptCodeEditor>().Single();
            Assert.True(source.Bounds.Height > editor.Bounds.Height * 0.75);
            var output = editor.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "WorldScriptOutput");
            Assert.False(output.IsEffectivelyVisible);
            editor.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().Single(t => t.Name == "ToggleScriptOutput").IsChecked = true;
            Dispatcher.UIThread.RunJobs(); Assert.True(output.IsEffectivelyVisible);
            var toolbar = editor.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "ScriptEditorToolbar");
            Assert.Contains(toolbar.GetVisualDescendants().OfType<Button>(), b => b.Name == "NewWorldScript");
            Assert.Contains(toolbar.GetVisualDescendants().OfType<Button>(), b => b.Name == "SaveWorldScript");
            await model.Items[0].EnableCommand.ExecuteAsync(true);
            Assert.True(controller.ScriptLibrary.Items[0].Runtime.IsRunning);
            controller.Pages.SelectedPage = SessionPage.Play;
            Assert.True(controller.ScriptLibrary.Items[0].Runtime.IsRunning);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("de")]
    [InlineData("pt-BR")]
    public async Task LibrarySupportsMultipleScriptsAndLocalizedEditor(string language)
    {
        var previous = Wandur.Core.Localization.UiLanguage.Culture;
        Wandur.Core.Localization.UiLanguage.Apply(language);
        await using var library = new WorldScriptLibrary(new InlineScriptFactory(), new MemoryScriptLibraryStore(), () => true, () => false, _ => Task.FromResult(true), _ => { });
        library.Configure("world", "World");
        using var model = new ScriptLibraryViewModel(library);
        var window = new Window { Width = 1000, Height = 760, Content = new ScriptLibraryView(model) };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var first = model.Selected;
            model.NewCommand.Execute(null); Assert.Equal(2, model.Items.Count); Assert.NotSame(first, model.Selected);
            var source = window.GetVisualDescendants().OfType<ScriptCodeEditor>().Single();
            source.Text = "mud.on('line', event => mud.echo(event.text));";
            source.TextArea.Focus(); source.CaretOffset = source.Text.Length;
            window.KeyTextInput(" // draft"); Dispatcher.UIThread.RunJobs();
            Assert.EndsWith(" // draft", source.Text);
            Assert.True(source.Document.UndoStack.CanUndo);
            var text = source.Text; var caret = source.CaretOffset;
            model.Name = "Observer";
            await model.SaveCommand.ExecuteAsync(null);
            await model.Selected!.EnableCommand.ExecuteAsync(true);
            Assert.Equal(text, source.Text); Assert.Equal(caret, source.CaretOffset); Assert.True(source.Document.UndoStack.CanUndo);
            Assert.False(first!.Enabled); Assert.True(model.Selected.Enabled);
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Equal("Observer", window.GetVisualDescendants().OfType<TextBox>().Single(b => b.Name == "WorldScriptName").Text);
            Assert.True(window.GetVisualDescendants().OfType<CheckBox>().Single(b => b.Name == "EnableWorldScript").IsChecked);
            Assert.Equal(Strings.ScriptSave, Avalonia.Automation.AutomationProperties.GetName(window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SaveWorldScript")));
            using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
            if (Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR") is { } directory)
            { Directory.CreateDirectory(directory); frame.Save(Path.Combine(directory, "script-library-" + language + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions()); }
            model.RequestDeleteCommand.Execute(null); Assert.True(model.ConfirmDelete);
            model.CancelDeleteCommand.Execute(null); await model.DeleteCommand.ExecuteAsync(null); Assert.Equal(2, model.Items.Count);
            model.RequestDeleteCommand.Execute(null); await model.DeleteCommand.ExecuteAsync(null); Assert.Single(model.Items);
        }
        finally { window.Close(); Wandur.Core.Localization.UiLanguage.Apply(previous.Name); }
    }
}
