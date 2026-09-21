using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Discovery;
using Wandur.Core.Localization;
using Wandur.Core.Settings;
using Wandur.Desktop;
using Wandur.Desktop.ViewModels;
using Wandur.Desktop.Views;

namespace Wandur.Desktop.Tests;

[Collection(UiLanguageCollection.Name)]
public sealed class LocalizationViewTests
{
    [AvaloniaTheory]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("de")]
    [InlineData("pt-BR")]
    public async Task LocalizedDialogsRenderAndLanguageChoicePersists(string language)
    {
        var before = Wandur.Core.Localization.UiLanguage.Culture;
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), "wandur-locale-" + Guid.NewGuid(), "settings.json"));
        await using var controller = new WorkspaceController(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), store, new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        var profile = new ConnectionProfile { Name = "Legends of the Jedi", Host = "legendsofthejedi.com", Port = 5656 };
        controller.SaveSettings(controller.Settings with { Profiles = [profile] });
        ProfileDialog? editor = null; OptionsDialog? options = null;
        try
        {
            Wandur.Core.Localization.UiLanguage.Apply(language);
            editor = new ProfileDialog(new ProfileEditorViewModel(controller, new EmptyDirectory(), profile));
            editor.Show(); Dispatcher.UIThread.RunJobs();
            var save = editor.GetVisualDescendants().OfType<Button>().Single(c => c.Name == "SaveWorld");
            Assert.Equal(Strings.SaveWorld, save.Content);
            Assert.NotEqual("Save world", save.Content);
            Capture(editor, "profile-" + language + ".png");
            editor.Close();
            var model = new PreferencesViewModel(controller, ThemeService.Apply);
            options = new OptionsDialog(model); options.Show(); Dispatcher.UIThread.RunJobs();
            var choice = options.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "LanguageChoice");
            choice.SelectedItem = model.Languages.Single(l => l.Code == language);
            Capture(options, "preferences-" + language + ".png");
            model.SaveCommand.Execute(null);
            Assert.False(options.IsVisible);
            Assert.Equal(language, store.Load().Settings.Language);
            Assert.Equal(language, CultureInfo.CurrentUICulture.Name);
        }
        finally { editor?.Close(); options?.Close(); Wandur.Core.Localization.UiLanguage.Apply(before.Name); Directory.Delete(Path.GetDirectoryName(store.FilePath)!, true); }
    }

    private static void Capture(Window window, string name)
    {
        using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
        if (Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR") is { } directory) { Directory.CreateDirectory(directory); frame.Save(Path.Combine(directory, name), new Avalonia.Media.Imaging.PngBitmapEncoderOptions()); }
    }
    private sealed class EmptyDirectory : IWorldDirectory
    {
        public Task<WorldNameSuggestion?> LookupAsync(string host, int port, CancellationToken cancellationToken = default) => Task.FromResult<WorldNameSuggestion?>(null);
    }
}
