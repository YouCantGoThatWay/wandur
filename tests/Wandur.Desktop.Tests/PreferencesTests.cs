using System.Globalization;
using Avalonia.Controls.Documents;
using Avalonia.VisualTree;
using Wandur.Core.Localization;
using Wandur.Core.Terminal;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Wandur.Core.Settings;
using Wandur.Desktop.Services;
using Wandur.Desktop.ViewModels;
using Wandur.Desktop.Views;

namespace Wandur.Desktop.Tests;

public sealed class PreferencesTests
{
    private sealed class Store : IClientSettingsStore
    {
        public ClientSettings Settings { get; set; } = new();
        public void SaveSettings(ClientSettings settings) { settings.Validate(); Settings = settings; }
    }
    [AvaloniaFact]
    public async Task LiveLanguageUpdatesMenusAndSessionControlsWithoutDisconnectingOrClearingInput()
    {
        var original = UiLanguage.Culture.Name; UiLanguage.Apply("en");
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), "wandur-live-language-" + Guid.NewGuid(), "settings.json"));
        var window = new MainWindow(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), store, new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        window.Show(); Dispatcher.UIThread.RunJobs();
        try
        {
            await window.Controller.StartAsync(); Dispatcher.UIThread.RunJobs();
            var input = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "CommandInput");
            input.Text = "keep this draft";
            var transcript = window.Controller.Terminal.PlainText;
            using var model = new PreferencesViewModel(window.Controller, window.Sessions.PreviewAppearanceSettings);
            model.Language = model.Languages.Single(l => l.Code == "fr"); Dispatcher.UIThread.RunJobs();
            Assert.Contains(NativeMenu.GetMenu(window)!.Items.OfType<NativeMenuItem>(), item => item.Header == Strings.View);
            Assert.Equal(Strings.Send, window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SendCommand").Content);
            Assert.Equal(Strings.EnterACommand, input.PlaceholderText);
            Assert.Equal(Strings.Workspace, window.Workspace.WorldsTool!.Title);
            Assert.Equal("keep this draft", input.Text);
            Assert.Equal(transcript, window.Controller.Terminal.PlainText);
            Assert.True(window.Controller.IsConnected);
            model.SaveCommand.Execute(null);
            Assert.Equal("fr", store.Load().Settings.Language);
        }
        finally { await window.Sessions.DisposeAsync(); window.Close(); UiLanguage.Apply(original); ThemeService.Apply(new()); }
    }

    [AvaloniaFact]
    public void LanguageSelectionUpdatesExistingLabelsAndCancelRestoresLanguageWithoutLosingDrafts()
    {
        var original = UiLanguage.Culture.Name;
        UiLanguage.Apply("en");
        var store = new Store { Settings = new() { Language = "en" } };
        var model = new PreferencesViewModel(store, ThemeService.Apply);
        var window = new OptionsDialog(model); window.Show();
        try
        {
            model.SectionIndex = 1; model.DuplicateThemeCommand.Execute(null); model.ThemeName = "Keep my name";
            var customId = model.Theme;
            model.Language = model.Languages.Single(l => l.Code == "de");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("de", UiLanguage.Culture.Name);
            Assert.True(LocalizedText.Instance["SettingsTitle"] == "Einstellungen", "Direct resource lookup: " + LocalizedText.Instance["SettingsTitle"]);
            Assert.Equal("Darstellung", model.SectionTitle);
            Assert.Equal("Einstellungen", window.Title);
            Assert.Equal("Darstellung", model.SectionTitle);
            Assert.Equal("Arbeitsbereich", model.Colors[0].Label);
            Assert.Equal(customId, model.Theme); Assert.Equal("Keep my name", model.ThemeName);
            Assert.Equal(Strings.SavePreferences, window.FindControl<Button>("SavePreferences")!.Content);
            window.Close();
            Assert.Equal("en", CultureInfo.CurrentUICulture.Name);
            Assert.Equal("en", store.Settings.Language); Assert.Empty(store.Settings.CustomThemes);
        }
        finally { window.Close(); UiLanguage.Apply(original); ThemeService.Apply(new()); }
    }
    [AvaloniaFact]
    public void ShortSectionsStayVisibleAfterScrollingAppearanceAndReturning()
    {
        var model = new PreferencesViewModel(new Store(), ThemeService.Apply);
        var window = new OptionsDialog(model); window.Show();
        try
        {
            model.SectionIndex = 1; Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var appearance = window.FindControl<ScrollViewer>("AppearanceScroll")!;
            appearance.ScrollToEnd(); window.UpdateLayout();
            Assert.True(appearance.Offset.Y > 100);
            foreach (var index in new[] { 0, 3, 4, 1, 0 })
            {
                model.SectionIndex = index; Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                var visible = new[] { "GeneralScroll", "AppearanceScroll", "MudColorsScroll", "TerminalScroll", "InputScroll" }
                    .Select(n => window.FindControl<ScrollViewer>(n)!).Where(v => v.IsVisible).ToArray();
                Assert.Single(visible);
                if (index != 1) Assert.Equal(0, visible[0].Offset.Y);
                Assert.True(visible[0].Bounds.Height > 100);
            }
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public void AnsiDraftResetSaveAndCancelAreIsolated()
    {
        var custom = UserTheme.FromPreset("Ember") with { Name = "Custom", AnsiColors = new() { [1] = "#123456" } };
        var store = new Store { Settings = new() { Theme = custom.Id, CustomThemes = [custom] } };
        using (var model = new PreferencesViewModel(store, ThemeService.Apply))
        {
            Assert.Equal("#123456", model.AnsiColors[1].Hex);
            model.AnsiColors[1].Hex = "#ABCDEF";
            Assert.Equal("#123456", store.Settings.CustomThemes[0].AnsiColors[1]);
            model.AnsiColors[1].ResetCommand.Execute(null);
            Assert.Equal(AnsiPalette.Defaults[1], model.AnsiColors[1].Hex);
            model.SaveCommand.Execute(null);
        }
        Assert.Empty(store.Settings.CustomThemes[0].AnsiColors);
        ThemeService.Apply(new());
    }
    [AvaloniaFact]
    public async Task AnsiOverridesRecolorExistingTranscriptButNotExplicitRgbColors()
    {
        var path = Path.Combine(Path.GetTempPath(), "wandur-ansi-ui-" + Guid.NewGuid(), "settings.json");
        await using var controller = new WorkspaceController(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), new SettingsStore(path), new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        controller.Terminal.Append("\x1b[31;44mindexed\x1b[38;2;205;49;49;48;2;97;175;239mrgb");
        var window = new Window { Width = 900, Height = 700, Content = new TerminalView(controller) }; window.Show();
        try
        {
            var transcript = window.GetVisualDescendants().OfType<Iciclecreek.Terminal.TerminalView>().Single(t => t.Name == "Transcript");
            var line = transcript.Terminal.Buffer.GetLine(0)!;
            var indexed = line[0]; var rgb = line[7];
            var custom = UserTheme.FromPreset("Ember") with { Name = "ANSI", AnsiColors = new() { [1] = "#112233", [4] = "#334455" } };
            ThemeService.Apply(new() { Theme = custom.Id, CustomThemes = [custom] }, UserTheme.FromPreset("Forest").ToWorldTheme());
            Dispatcher.UIThread.RunJobs();
            var palette = transcript.Terminal.Colors.Take();
            Assert.Equal(Color.Parse("#112233"), Iciclecreek.Avalonia.Terminal.BufferCellExtensions.GetForegroundColor(indexed, palette));
            Assert.Equal(Color.Parse("#334455"), Iciclecreek.Avalonia.Terminal.BufferCellExtensions.GetBackgroundColor(indexed, palette));
            Assert.Equal(Color.Parse(AnsiPalette.Defaults[1]), Iciclecreek.Avalonia.Terminal.BufferCellExtensions.GetForegroundColor(rgb, palette));
            Assert.Equal(Color.Parse(AnsiPalette.Defaults[4]), Iciclecreek.Avalonia.Terminal.BufferCellExtensions.GetBackgroundColor(rgb, palette));
            Assert.Same(line, transcript.Terminal.Buffer.GetLine(0));
        }
        finally { window.Close(); ThemeService.Apply(new()); }
    }

    // Disposing the preferences draft broadcasts a UI language change.
    [AvaloniaFact]
    public void ThemeEditsAreIsolatedAndCancelRestoresOriginalAppearance()
    {
        var custom = UserTheme.FromPreset("Ember") with { Name = "My theme" };
        var store = new Store { Settings = new() { Theme = custom.Id, CustomThemes = [custom] } };
        ClientSettings? preview = null;
        using (var model = new PreferencesViewModel(store, settings => preview = settings))
        {
            model.Colors.Single(c => c.Key == "Chrome").Hex = "#AABBCC";
            Assert.Equal("#AABBCC", preview!.CustomThemes[0].Colors["Chrome"]);
            Assert.NotEqual("#AABBCC", store.Settings.CustomThemes[0].Colors["Chrome"]);
            model.DeleteThemeCommand.Execute(null);
            Assert.Empty(preview.CustomThemes);
            Assert.Single(store.Settings.CustomThemes);
        }
        Assert.Same(store.Settings, preview);
    }
    [AvaloniaFact]
    public void InvalidDraftCannotBeSavedAndValidDraftPreservesConcurrentProfileChanges()
    {
        var store = new Store();
        using var model = new PreferencesViewModel(store, _ => { });
        model.DuplicateThemeCommand.Execute(null); model.ThemeName = "Space";
        var color = model.Colors.Single(c => c.Key == "Chrome");
        color.Hex = "bad"; model.SaveCommand.Execute(null);
        Assert.NotEmpty(model.Error); Assert.Empty(store.Settings.CustomThemes);
        model.Theme = "Paper"; model.Theme = model.Themes.Last().Id;
        Assert.Equal("bad", model.Colors.Single(c => c.Key == "Chrome").Hex);
        model.Colors.Single(c => c.Key == "Chrome").Hex = "#123456";
        store.Settings = store.Settings with { Profiles = [new() { Name = "New world", Host = "new.example" }] };
        model.UseWorldThemes = false; model.SaveCommand.Execute(null);
        Assert.Empty(model.Error); Assert.Single(store.Settings.Profiles);
        Assert.Equal("Space", Assert.Single(store.Settings.CustomThemes).Name);
        Assert.False(store.Settings.UseWorldThemes);
    }
    [AvaloniaFact]
    public void PresetCopiesPreserveMapAndEditorColors()
    {
        foreach (var preset in UserTheme.PresetNames)
        {
            ThemeService.Apply(new() { Theme = preset });
            var keys = new[] { "MapCanvasBrush", "MapGridBrush", "EditorBackgroundBrush", "EditorTextBrush" };
            var before = keys.Select(key => ((ISolidColorBrush)Application.Current!.Resources[key]!).Color).ToArray();
            var copy = UserTheme.FromPreset(preset) with { Name = "Copy" };
            ThemeService.Apply(new() { Theme = copy.Id, CustomThemes = [copy] });
            Assert.Equal(before, keys.Select(key => ((ISolidColorBrush)Application.Current!.Resources[key]!).Color));
        }
        ThemeService.Apply(new());
    }

    [AvaloniaFact]
    public void CustomColorsPreviewAcrossResourcesAndWorldThemePreferenceIsRespected()
    {
        var custom = UserTheme.FromPreset("Paper") with { Name = "Personal" };
        custom.Colors["Chrome"] = "#112233"; custom.Colors["MapGrid"] = "#445566";
        var settings = new ClientSettings { Theme = custom.Id, CustomThemes = [custom], UseWorldThemes = false };
        var world = UserTheme.FromPreset("Forest").ToWorldTheme();
        ThemeService.Apply(settings, world);
        Assert.Equal("#ff112233", ((ISolidColorBrush)Application.Current!.Resources["ChromeBrush"]!).Color.ToString());
        Assert.Equal("#ff445566", ((ISolidColorBrush)Application.Current.Resources["MapGridBrush"]!).Color.ToString());
        Assert.Equal(Avalonia.Styling.ThemeVariant.Light, Application.Current.RequestedThemeVariant);
        ThemeService.Apply(settings with { UseWorldThemes = true }, world);
        Assert.Equal(Avalonia.Styling.ThemeVariant.Dark, Application.Current.RequestedThemeVariant);
        Assert.Equal(world.Colors.Terminal, ((ISolidColorBrush)Application.Current.Resources["TerminalBrush"]!).Color.ToString()[3..].Insert(0, "#"), ignoreCase: true);
        ThemeService.Apply(new());
    }
    [AvaloniaFact]
    public void SidebarThemePickerAndColorPickerBindAndRender()
    {
        var store = new Store();
        var model = new PreferencesViewModel(store, ThemeService.Apply);
        var window = new OptionsDialog(model);
        window.Show();
        try
        {
            window.FindControl<ListBox>("SettingsSections")!.SelectedIndex = 1;
            window.FindControl<ComboBox>("ThemeChoice")!.SelectedValue = "Forest";
            model.DuplicateThemeCommand.Execute(null); model.ThemeName = "Endor"; model.UseWorldThemes = false;
            Dispatcher.UIThread.RunJobs();
            Assert.True(model.IsAppearance); Assert.True(model.IsCustom); Assert.Equal("Endor", model.Themes.Last().Name);
            model.Colors[0].Color = Color.Parse("#334455");
            Assert.Equal("#334455", model.Colors[0].Hex);
            var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/screenshots/settings"));
            Directory.CreateDirectory(directory);
            using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(directory, "custom-theme-editor.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            model.SectionIndex = 2; Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(directory, "ansi-colors.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            model.Language = model.Languages.Single(l => l.Code == "de"); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(directory, "ansi-colors-de.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            model.Language = model.Languages.Single(l => l.Code == "en");
            model.SaveCommand.Execute(null);
            Assert.Equal("#334455", store.Settings.CustomThemes[0].Colors["Shell"]);
        }
        finally { window.Close(); ThemeService.Apply(new()); }
    }
}
