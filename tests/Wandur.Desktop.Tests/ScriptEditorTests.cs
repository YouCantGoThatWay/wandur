using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;
using Avalonia.Media;
using Wandur.Core.Settings;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Localization;
using Wandur.Core.Scripting;
using Wandur.Desktop.Services;
using Wandur.Desktop.ViewModels;
using Wandur.Desktop.Views;

namespace Wandur.Desktop.Tests;

public sealed class ScriptEditorTests
{
    [AvaloniaFact]
    public async Task ClosingEditorRetainsUnsavedSourceWithoutStartingScript()
    {
        await using var scripts = CreateScripts(new MemoryStore());
        scripts.Configure("demo", "Demo world");
        var model = new ScriptEditorViewModel(scripts);
        var window = new ScriptsWindow(model);
        window.Show(); Dispatcher.UIThread.RunJobs();
        var source = window.GetVisualDescendants().OfType<TextEditor>().Single(c => c.Name == "ScriptSource");
        source.Text = "mud.echo('retained');";
        Dispatcher.UIThread.RunJobs();
        window.Close();
        using var reopened = new ScriptEditorViewModel(scripts);
        Assert.Equal("mud.echo('retained');", reopened.Source);
        Assert.False(reopened.IsRunning);
        Assert.Empty(reopened.Log);
    }

    [AvaloniaFact]
    public async Task RunStopAndExplicitSaveApplyToTheOriginalWorld()
    {
        var store = new MemoryStore();
        await using var original = CreateScripts(store);
        await using var other = CreateScripts(store);
        original.Configure("original", "Original world"); other.Configure("other", "Other world");
        using var model = new ScriptEditorViewModel(original);
        model.Source = "mud.echo('hello from original');";
        Assert.Empty(store.Sources);
        Assert.Contains("Original world", model.Title);
        await model.RunCommand.ExecuteAsync(null);
        Assert.True(model.IsRunning);
        Assert.Contains("hello from original", model.Log);
        Assert.False(other.IsRunning);
        Assert.Empty(other.Log);
        model.SaveCommand.Execute(null);
        Assert.Equal(model.Source, store.Sources["original"]);
        Assert.False(store.Sources.ContainsKey("other"));
        model.StopCommand.Execute(null);
        Assert.False(model.IsRunning);
        Assert.Equal(Strings.ScriptStopped, model.Status);
    }

    [AvaloniaFact]
    public async Task DisconnectedEditorDisablesRunAndDisposalUnsubscribesFromChanges()
    {
        await using var scripts = CreateScripts(new MemoryStore(), false);
        using var model = new ScriptEditorViewModel(scripts);
        Assert.False(model.RunCommand.CanExecute(null));
        Assert.False(model.StopCommand.CanExecute(null));
        var changes = 0;
        model.PropertyChanged += (_, _) => changes++;
        scripts.Source = "mud.echo('change');";
        Assert.True(changes > 0);
        model.Dispose(); changes = 0;
        scripts.Source = "mud.echo('after disposal');";
        Assert.Equal(0, changes);
    }

    [AvaloniaFact]
    public async Task RuntimeErrorsAppearInTheEditorAndLeaveRunAvailable()
    {
        await using var scripts = CreateScripts(new MemoryStore());
        scripts.Configure("demo", "Demo world");
        using var model = new ScriptEditorViewModel(scripts);
        var window = new ScriptsWindow(model);
        try
        {
            window.Show();
            model.Source = "throw new Error('Visible script failure');";
            await model.RunCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(model.IsRunning);
            Assert.True(model.HasError);
            Assert.Contains("Visible script failure", model.Error);
            Assert.True(model.RunCommand.CanExecute(null));
            var error = window.GetVisualDescendants().OfType<TextBlock>().Single(c => c.Name == "ScriptError");
            Assert.True(error.IsVisible);
            Assert.Equal(model.Error, error.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task EditorStatusReflectsPrivacyPauseAndResume()
    {
        var privateInput = false;
        await using var scripts = new SessionScripts(new EngineFactory(), new MemoryStore(),
            () => true, () => privateInput, _ => Task.FromResult(true), _ => { });
        scripts.Configure("demo", "Demo world");
        using var model = new ScriptEditorViewModel(scripts);
        var window = new ScriptsWindow(model);
        try
        {
            window.Show();
            await model.RunCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            var status = window.GetVisualDescendants().OfType<TextBlock>().Single(c => c.Name == "ScriptStatus");
            Assert.Equal(Strings.ScriptRunning, status.Text);
            privateInput = true; scripts.RefreshState(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(Strings.ScriptPaused, status.Text);
            Assert.True(model.IsPaused);
            Assert.False(model.RunCommand.CanExecute(null));
            Assert.True(model.StopCommand.CanExecute(null));
            privateInput = false; scripts.RefreshState(); Dispatcher.UIThread.RunJobs();
            Assert.False(model.IsPaused);
            Assert.Equal(Strings.ScriptRunning, status.Text);
            model.StopCommand.Execute(null); Dispatcher.UIThread.RunJobs();
            Assert.Equal(Strings.ScriptStopped, status.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task SaveKeyboardShortcutPersistsTheFocusedEditorsSource()
    {
        var store = new MemoryStore();
        await using var scripts = CreateScripts(store);
        scripts.Configure("demo", "Demo world");
        using var model = new ScriptEditorViewModel(scripts);
        var window = new ScriptsWindow(model);
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var source = window.GetVisualDescendants().OfType<TextEditor>().Single(c => c.Name == "ScriptSource");
            source.Text = "mud.echo('saved using keyboard');";
            source.TextArea.Focus(); Dispatcher.UIThread.RunJobs();
            Assert.Empty(store.Sources);
            var modifier = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
            window.KeyPressQwerty(PhysicalKey.S, modifier);
            window.KeyReleaseQwerty(PhysicalKey.S, modifier);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(source.Text, store.Sources.GetValueOrDefault("demo"));
            Assert.Contains(Strings.ScriptSaved, model.Log);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task EditingPreservesCaretAndUndoAcrossScriptOutputUpdates()
    {
        await using var scripts = CreateScripts(new MemoryStore());
        scripts.Configure("demo", "Demo world");
        using var model = new ScriptEditorViewModel(scripts);
        model.Source = "mud.echo('hello');";
        var window = new ScriptsWindow(model);
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var editor = window.GetVisualDescendants().OfType<TextEditor>().Single(c => c.Name == "ScriptSource");
            editor.TextArea.Focus(); editor.CaretOffset = editor.Text.Length;
            window.KeyTextInput(" // edited"); Dispatcher.UIThread.RunJobs();
            var edited = editor.Text;
            var caret = editor.CaretOffset;
            Assert.Equal("mud.echo('hello'); // edited", model.Source);
            Assert.True(editor.Document.UndoStack.CanUndo);
            await model.RunCommand.ExecuteAsync(null); model.SaveCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("hello", model.Log);
            Assert.Equal(edited, editor.Text); Assert.Equal(caret, editor.CaretOffset);
            Assert.True(editor.Document.UndoStack.CanUndo);
            editor.Undo();
            Assert.Equal("mud.echo('hello');", model.Source);
            editor.Redo();
            Assert.Equal(edited, model.Source);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task JavaScriptTokensRemainReadableWhenSwitchingLightAndDarkThemes()
    {
        await using var scripts = CreateScripts(new MemoryStore());
        scripts.Configure("demo", "Demo world");
        using var model = new ScriptEditorViewModel(scripts);
        model.Source = """
            // Count matching lines
            const count = 42;
            let label = `Count: ${count + 1}`;
            mud.trigger(/^Exits: (.+)$/i, match => mud.echo(match[1]));
            /* A multiline
               comment */
            mud.echo("Ready");
            const escaped = "say \"hello\"";
            const pattern = /a\/[a-z]+/gi;
            const template = `first line
            second ${true ? "yes" : "no"}`;
            """;
        var window = new ScriptsWindow(model);
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var editor = window.GetVisualDescendants().OfType<TextEditor>().Single(c => c.Name == "ScriptSource");
            Color? darkKeyword = null;
            foreach (var theme in new[] { "Ember", "Paper", "Moonlight" })
            {
                ThemeService.Apply(new ClientSettings { Theme = theme });
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                using var highlighter = new DocumentHighlighter(editor.Document, editor.SyntaxHighlighting);
                var tokens = Enumerable.Range(1, editor.Document.LineCount)
                    .SelectMany(line => highlighter.HighlightLine(line).Sections)
                    .Select(section => (section.Color.Name, Text: editor.Document.GetText(section.Offset, section.Length))).ToArray();
                Assert.Contains(tokens, token => token.Name == "Keyword" && token.Text == "const");
                Assert.Contains(tokens, token => token.Name == "Number" && token.Text == "42");
                Assert.Contains(tokens, token => token.Name == "Number" && token.Text == "1");
                Assert.Contains(tokens, token => token.Name == "Comment" && token.Text.Contains("multiline"));
                Assert.Contains(tokens, token => token.Name == "Comment" && token.Text.Contains("comment"));
                Assert.Contains(tokens, token => token.Name == "String" && token.Text.Contains("Count:"));
                Assert.Contains(tokens, token => token.Name == "String" && token.Text.Contains("Ready"));
                Assert.Contains(tokens, token => token.Name == "Regex" && token.Text == "/^Exits: (.+)$/i");
                Assert.Contains(tokens, token => token.Name == "Api" && token.Text == "trigger");
                Assert.Contains(tokens, token => token.Name == "Escape" && token.Text == "\\\"");
                Assert.Contains(tokens, token => token.Name == "Regex" && token.Text == "/a\\/[a-z]+/gi");
                Assert.Contains(tokens, token => token.Name == "String" && token.Text.Contains("second"));
                Assert.Contains(tokens, token => token.Name == "Keyword" && token.Text == "true");
                var background = Assert.IsAssignableFrom<ISolidColorBrush>(editor.Background).Color;
                foreach (var name in new[] { "Keyword", "Comment", "String", "Number", "Regex", "Api", "Escape" })
                {
                    var color = editor.SyntaxHighlighting.GetNamedColor(name).Foreground.GetColor(null)!.Value;
                    Assert.True(Contrast(color, background) >= 4.5, $"{theme} {name} contrast is {Contrast(color, background)}.");
                }
                var keyword = editor.SyntaxHighlighting.GetNamedColor("Keyword").Foreground.GetColor(null)!.Value;
                if (theme == "Ember") darkKeyword = keyword;
                else if (theme == "Paper") Assert.NotEqual(darkKeyword, keyword);
                else Assert.Equal(darkKeyword, keyword);
                Assert.Equal(model.Source, editor.Text);
                using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
                if (Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR") is { } directory)
                {
                    Directory.CreateDirectory(directory); frame.Save(Path.Combine(directory, "scripts-syntax-" + theme + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
                }
            }
        }
        finally { window.Close(); ThemeService.Apply(new ClientSettings()); }
    }

    private static double Contrast(Color a, Color b)
    {
        static double Channel(byte value) { var s = value / 255d; return s <= .04045 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4); }
        static double Luminance(Color c) => .2126 * Channel(c.R) + .7152 * Channel(c.G) + .0722 * Channel(c.B);
        var first = Luminance(a); var second = Luminance(b);
        return (Math.Max(first, second) + .05) / (Math.Min(first, second) + .05);
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("de")]
    [InlineData("pt-BR")]
    public async Task EditorRendersLocalizedControlsAndScrollableSource(string language)
    {
        var before = Wandur.Core.Localization.UiLanguage.Culture;
        await using var scripts = CreateScripts(new MemoryStore());
        ScriptsWindow? window = null;
        try
        {
            Wandur.Core.Localization.UiLanguage.Apply(language);
            scripts.Configure("demo", "Demo world");
            Assert.Null(new JavaScriptEngine().Load(scripts.Source).Error);
            window = new ScriptsWindow(new ScriptEditorViewModel(scripts));
            window.Show(); Dispatcher.UIThread.RunJobs();
            var controls = window.GetVisualDescendants().ToArray();
            Assert.Equal(Strings.ScriptRun, controls.OfType<Button>().Single(c => c.Name == "RunScript").Content);
            Assert.Equal(Strings.ScriptStop, controls.OfType<Button>().Single(c => c.Name == "StopScript").Content);
            Assert.Equal(Strings.ScriptSave, controls.OfType<Button>().Single(c => c.Name == "SaveScript").Content);
            var source = controls.OfType<TextEditor>().Single(c => c.Name == "ScriptSource");
            Assert.True(source.ShowLineNumbers);
            Assert.Equal(ScrollBarVisibility.Auto, source.HorizontalScrollBarVisibility);
            using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
            if (Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR") is { } directory)
            {
                Directory.CreateDirectory(directory); frame.Save(Path.Combine(directory, "scripts-" + language + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
            window.Width = window.MinWidth; window.Height = window.MinHeight;
            controls.OfType<Expander>().Single(c => c.Name == "ScriptHelp").IsExpanded = true;
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.True(source.Bounds.Height >= 80, $"Source editor shrank to {source.Bounds.Height} pixels.");
            using var expanded = window.CaptureRenderedFrame(); Assert.NotNull(expanded);
            if (Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR") is { } expandedDirectory)
                expanded.Save(Path.Combine(expandedDirectory, "scripts-" + language + "-compact-help.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
        finally { window?.Close(); Wandur.Core.Localization.UiLanguage.Apply(before.Name); }
    }

    private static SessionScripts CreateScripts(MemoryStore store, bool connected = true)
        => new(new EngineFactory(), store, () => connected, () => false, _ => Task.FromResult(true), _ => { });

    private sealed class MemoryStore : IWorldScriptStore
    {
        public Dictionary<string, string> Sources { get; } = [];
        public string? Load(string worldKey) => Sources.GetValueOrDefault(worldKey);
        public void Save(string worldKey, string source) => Sources[worldKey] = source;
    }

    // Keep editor tests deterministic while exercising the real JavaScript engine.
    private sealed class EngineFactory : IScriptRuntimeFactory
    {
        public IScriptRuntime Create() => new EngineRuntime();
    }
    private sealed class EngineRuntime : IScriptRuntime
    {
        private readonly JavaScriptEngine _engine = new();
        private bool _stopped;
        public bool IsRunning => !_stopped && _engine.IsRunning;
        public Task<ScriptResult> LoadAsync(string source, CancellationToken cancellationToken = default, bool restrictedSend = false) => Task.FromResult(_engine.Load(source, restrictedSend));
        public Task<ScriptResult> DispatchAsync(ScriptEvent input, CancellationToken cancellationToken = default) => Task.FromResult(_engine.Dispatch(input));
        public void Stop() => _stopped = true;
        public ValueTask DisposeAsync() { Stop(); return ValueTask.CompletedTask; }
    }
}
