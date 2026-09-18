using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Localization;
using Wandur.Core.Scripting;
using Wandur.Core.Settings;
using Wandur.Desktop.Services;
using Wandur.Desktop.ViewModels;
using Wandur.Desktop.Views;

namespace Wandur.Desktop.Tests;

public sealed class MacroLibraryTests
{
    [AvaloniaFact]
    public async Task MacroDraftsUseSavedRulesAndRemainIndependentAcrossSessions()
    {
        var store = new MemoryScriptLibraryStore(); var sent = new List<string>(); var privateInput = false;
        await using var library = new WorldScriptLibrary(new InlineScriptFactory(), store, () => true, () => privateInput,
            command => { sent.Add(command); return Task.FromResult(true); }, _ => { });
        library.Configure("world", "World");
        using var model = new MacroLibraryViewModel(library);
        using var scripts = new ScriptLibraryViewModel(library);
        model.NewCommand.Execute(null);
        Assert.Single(model.Items); Assert.Single(scripts.Items); Assert.False(model.Enabled);
        model.Name = "Status"; model.KindIndex = (int)MacroKind.Alias; model.Pattern = "h"; model.Commands = "score";
        await model.SaveCommand.ExecuteAsync(null);
        model.Commands = "look";
        await model.Selected!.EnableCommand.ExecuteAsync(true);
        Assert.True(await library.HandleCommandAsync("h")); Assert.Equal("score", Assert.Single(sent));
        Assert.True(model.HasUnsavedChanges);
        await using var second = new WorldScriptLibrary(new InlineScriptFactory(), store, () => true, () => false, _ => Task.FromResult(true), _ => { });
        second.Configure("world", "World");
        Assert.Equal("score", second.Items.Single(e => e.IsMacro).Macro!.Commands);
        model.Commands = ""; await model.SaveCommand.ExecuteAsync(null);
        Assert.True(model.HasError); Assert.True(model.HasUnsavedChanges);
        Assert.Equal("score", store.Load("world").Single(e => e.Macro is not null).Macro!.Commands);
        model.Commands = "look"; await model.SaveCommand.ExecuteAsync(null);
        Assert.False(model.HasError); Assert.False(model.HasUnsavedChanges);
        privateInput = true; library.RefreshState();
        Assert.False(await library.HandleCommandAsync("h")); Assert.Single(sent);
        privateInput = false; library.RefreshState();
        Assert.True(await library.HandleCommandAsync("h")); Assert.Equal("look", sent.Last());
        model.RequestDeleteCommand.Execute(null); await model.DeleteCommand.ExecuteAsync(null);
        Assert.Empty(model.Items); Assert.Single(scripts.Items); Assert.Single(store.Load("world"));
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("pt-BR")]
    public async Task MacroFormsKeepTheirValuesAcrossKindsAndLanguages(string language)
    {
        var previous = UiLanguage.Culture;
        await using var library = new WorldScriptLibrary(new InlineScriptFactory(), new MemoryScriptLibraryStore(), () => true, () => false, _ => Task.FromResult(true), _ => { });
        library.Configure("world", "World");
        using var model = new MacroLibraryViewModel(library);
        model.NewCommand.Execute(null); model.Pattern = "You are hungry";
        var window = new Window { Width = 940, Height = 760, Content = new MacroLibraryView(model) };
        try
        {
            UiLanguage.Apply(language); window.Show(); Dispatcher.UIThread.RunJobs();
            Assert.Equal("You are hungry", model.Pattern);
            Find<ComboBox>(window, "MacroKind").SelectedIndex = (int)MacroKind.Timer;
            Find<NumericUpDown>(window, "MacroInterval").Value = 45;
            Assert.True(model.IsTimer); Assert.Equal(45m, model.Interval);
            Find<ComboBox>(window, "MacroKind").SelectedIndex = (int)MacroKind.Shortcut;
            Find<ComboBox>(window, "MacroKey").SelectedItem = "F4";
            Assert.Equal("F4", model.Pattern);
            UiLanguage.Apply(language == "de" ? "fr" : "de"); Dispatcher.UIThread.RunJobs();
            Assert.True(model.IsShortcut); Assert.Equal("F4", model.Pattern);
            Assert.Equal(0, Find<ComboBox>(window, "MacroMatch").SelectedIndex);
            UiLanguage.Apply(language);
            Find<ComboBox>(window, "MacroKind").SelectedIndex = (int)MacroKind.Trigger;
            Find<TextBox>(window, "MacroPattern").Text = "You are hungry";
            Find<TextBox>(window, "MacroCommands").Text = "eat bread\ndrink water";
            Assert.Equal("eat bread\ndrink water", model.Commands);
            await model.SaveCommand.ExecuteAsync(null);
            Assert.False(model.HasUnsavedChanges);
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Assert.True(Find<TextBox>(window, "MacroCommands").Bounds.Height >= 140);
            Assert.Equal(Strings.MacroNew, Avalonia.Automation.AutomationProperties.GetName(Find<Button>(window, "NewMacro")));
            if (Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR") is { } directory)
            {
                Directory.CreateDirectory(directory); using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
                frame.Save(Path.Combine(directory, "macros-" + language + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
        }
        finally { window.Close(); UiLanguage.Apply(previous.Name); }
    }

    [AvaloniaFact]
    public async Task LiveMenuKeepsRunningRulesAndFunctionKeysRespectPrivateInput()
    {
        var path = Path.Combine(Path.GetTempPath(), "wandur-macro-ui-" + Guid.NewGuid(), "settings.json");
        await using var controller = new WorkspaceController(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), new SettingsStore(path), new MemoryPasswordVault(), new MemoryRoomMapStore(), new InlineScriptFactory(), new MemoryScriptLibraryStore());
        await controller.StartAsync();
        var window = new Window { Width = 1000, Height = 720, Content = new TerminalView(controller) };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            using var model = new MacroLibraryViewModel(controller.ScriptLibrary);
            Assert.Equal(new[] { "TerminalViewTab", "DiagnosticsViewTab" }, window.GetVisualDescendants().OfType<TabStripItem>().Select(t => t.Name));
            Assert.Empty(window.GetVisualDescendants().OfType<MacroLibraryView>());
            Assert.NotNull(Find<Button>(window, "SessionScripts"));
            Assert.NotNull(Find<ToggleButton>(window, "SessionMacros"));
            model.NewCommand.Execute(null); model.KindIndex = (int)MacroKind.Shortcut; model.Pattern = "F4"; model.Commands = "look";
            await model.SaveCommand.ExecuteAsync(null); await model.Selected!.EnableCommand.ExecuteAsync(true);
            controller.Pages.SelectedPage = SessionPage.Play;
            Assert.True(model.Selected.Entry.Runtime.IsRunning);
            var input = Find<TextBox>(window, "CommandInput"); input.Focus();
            var before = controller.CommandsSent;
            window.KeyPress(Key.F4, RawInputModifiers.None, PhysicalKey.F4, null); window.KeyRelease(Key.F4, RawInputModifiers.None, PhysicalKey.F4, null);
            await ScriptSessionTests.WaitFor(() => controller.CommandsSent > before);
            controller.SetManualPrivate(true);
            Assert.False(controller.ScriptLibrary.HandleShortcut("F4"));
            Assert.True(model.Selected.Entry.Runtime.IsPaused);
            controller.SetManualPrivate(false);
            controller.Pages.SelectedPage = SessionPage.Diagnostics;
            Assert.True(model.Selected.Entry.Runtime.IsRunning); Assert.Equal("F4", model.Pattern);
        }
        finally { window.Close(); }
    }

    private static T Find<T>(Control root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
}
