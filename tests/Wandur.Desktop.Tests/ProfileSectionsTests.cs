using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Discovery;
using Wandur.Core.Localization;
using Wandur.Core.Settings;
using Wandur.Desktop.Services;
using Wandur.Desktop.ViewModels;
using Wandur.Desktop.Views;

namespace Wandur.Desktop.Tests;

public sealed class ProfileSectionsTests
{
    [AvaloniaFact]
    public async Task ConfigurationTargetsItsOriginatingProfileWithDuplicateConnections()
    {
        var settings = new SettingsStore(Path.Combine(Path.GetTempPath(), "wandur-profile-origin-" + Guid.NewGuid(), "settings.json"));
        var window = new MainWindow(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), settings, new MemoryPasswordVault(), new MemoryRoomMapStore(), new InlineScriptFactory(), new MemoryScriptLibraryStore());
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var firstProfile = new ConnectionProfile { Name = "Ackbar", Host = "127.0.0.1", Port = port, Username = "Ackbar" };
        var secondProfile = firstProfile with { Id = Guid.NewGuid(), Name = "Other character", Username = "Other" };
        ProfileDialog? dialog = null;
        try
        {
            window.Show(); window.Controller.SaveSettings(window.Controller.Settings with { Profiles = [firstProfile, secondProfile] });
            await window.Sessions.OpenAsync(firstProfile); using var firstSocket = await listener.AcceptTcpClientAsync();
            var first = window.Sessions.Active;
            await window.Sessions.OpenAsync(secondProfile); using var secondSocket = await listener.AcceptTcpClientAsync();
            var second = window.Sessions.Active;
            var editing = window.EditSessionAutomationAsync(first.Controller, 2);
            Dispatcher.UIThread.RunJobs(); dialog = Assert.Single(window.OwnedWindows.OfType<ProfileDialog>());
            var model = Assert.IsType<ProfileEditorViewModel>(dialog.DataContext);
            Assert.Equal(firstProfile.Id, model.SelectedProfile!.Id); Assert.Equal(2, model.SectionIndex);
            Assert.True(first.Controller.IsConnected); Assert.True(second.Controller.IsConnected);
            Assert.Same(second, window.Sessions.Active);
            dialog.Close(); await editing;
        }
        finally { dialog?.Close(); await window.Sessions.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task ApplyCommitsAllSectionsWithoutDuplicatingTheProfile()
    {
        var settings = new SettingsStore(Path.Combine(Path.GetTempPath(), "wandur-profile-sections-" + Guid.NewGuid(), "settings.json"));
        var scripts = new MemoryScriptLibraryStore();
        await using var controller = new WorkspaceController(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), settings, new MemoryPasswordVault(), new MemoryRoomMapStore(), new InlineScriptFactory(), scripts);
        using var model = new ProfileEditorViewModel(controller, new EmptyDirectory(), automationFactory: new ProfileAutomationFactory(new InlineScriptFactory(), scripts));
        var closed = false; model.CloseRequested += () => closed = true;
        model.SectionIndex = 2;
        Assert.True(model.CanEditAutomation);
        model.Host = "mud.example:4500"; model.WorldName = "Example";
        await model.ApplyCommand.ExecuteAsync(null);
        Assert.False(closed); Assert.True(model.CanEditAutomation);
        var id = Assert.Single(controller.Settings.Profiles).Id;
        Assert.Equal(id, model.SelectedProfile!.Id);
        model.Automation!.Scripts.NewCommand.Execute(null);
        model.Automation.Scripts.Name = "Offline script";
        model.Automation.Scripts.Source = "mud.send('look');";
        await model.Automation.Scripts.SaveCommand.ExecuteAsync(null);
        await model.Automation.Scripts.Selected!.EnableCommand.ExecuteAsync(true);
        Assert.False(model.Automation.Scripts.Selected.Entry.Runtime.IsRunning);
        Assert.DoesNotContain(scripts.Load("mud.example:4500:False"), s => s.Name == "Offline script");
        model.WorldName = "Renamed";
        await model.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(id, Assert.Single(controller.Settings.Profiles).Id);
        Assert.Equal("Renamed", model.SelectedProfile!.Name);
        Assert.Contains(scripts.Load("mud.example:4500:False"), s => s.Name == "Offline script");
        await model.SaveCommand.ExecuteAsync(null);
        Assert.True(closed); Assert.Single(controller.Settings.Profiles);
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("fr")]
    [InlineData("es")]
    [InlineData("pt-BR")]
    public async Task SectionNavigationPreservesEditorDraftsAndCanExpandTheCodeArea(string language)
    {
        var before = UiLanguage.Culture;
        var settings = new SettingsStore(Path.Combine(Path.GetTempPath(), "wandur-profile-layout-" + Guid.NewGuid(), "settings.json"));
        var scripts = new MemoryScriptLibraryStore();
        await using var controller = new WorkspaceController(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), settings, new MemoryPasswordVault(), new MemoryRoomMapStore(), new InlineScriptFactory(), scripts);
        var profile = new ConnectionProfile { Name = "Legends of the Jedi", Host = "legendsofthejedi.com", Port = 5656 };
        controller.SaveSettings(controller.Settings with { Profiles = [profile] });
        var model = new ProfileEditorViewModel(controller, new EmptyDirectory(), profile, new ProfileAutomationFactory(new InlineScriptFactory(), scripts));
        var window = new ProfileDialog(model);
        try
        {
            UiLanguage.Apply(language); window.Show(); Dispatcher.UIThread.RunJobs();
            Assert.True(window.CanResize);
            var sections = Find<ListBox>(window, "ProfileSections"); Assert.Equal(6, sections.ItemCount);
            sections.SelectedIndex = 1; Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Assert.True(model.IsLogin);
            Find<TextBox>(window, "LoginUsername").Text = "Ackbar";
            sections.SelectedIndex = 2; Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var editor = Find<ScriptCodeEditor>(window, "WorldScriptSource");
            editor.Text = "mud.on(Events.Line, event => {\n    mud.echo(event.text);\n}); // draft";
            var width = editor.Bounds.Width;
            Assert.True(editor.Bounds.Height > window.ClientSize.Height * .65);
            model.SectionsVisible = false; Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.True(editor.Bounds.Width > width + 140);
            model.SectionsVisible = true;
            sections.SelectedIndex = 3; Dispatcher.UIThread.RunJobs();
            Assert.Single(window.GetVisualDescendants().OfType<MacroLibraryView>());
            sections.SelectedIndex = 2; Dispatcher.UIThread.RunJobs();
            Assert.Same(editor, Find<ScriptCodeEditor>(window, "WorldScriptSource"));
            Assert.EndsWith("// draft", editor.Text); Assert.Equal("Ackbar", model.Username);
            UiLanguage.Apply(language == "de" ? "en" : "de");
            Assert.Equal(2, sections.SelectedIndex); Assert.True(model.IsScripts);
            UiLanguage.Apply(language); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            if (Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR") is { } directory)
            {
                Directory.CreateDirectory(directory); using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
                frame.Save(Path.Combine(directory, "profile-scripts-" + language + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
        }
        finally { model.ConfirmDiscardAsync = () => Task.FromResult(true); window.Close(); UiLanguage.Apply(before.Name); }
    }

    private static T Find<T>(Control root, string name) where T : Control 
    {
        var control = root.FindControl<T>(name) ?? root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);
        Assert.True(control is not null, "Missing control: " + name);
        return control!;
    }
    private sealed class EmptyDirectory : IWorldDirectory
    {
        public Task<WorldNameSuggestion?> LookupAsync(string host, int port, CancellationToken cancellationToken = default) => Task.FromResult<WorldNameSuggestion?>(null);
    }
}
