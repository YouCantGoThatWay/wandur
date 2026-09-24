using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Wandur.Core.Discovery;
using Wandur.Core.Localization;
using Wandur.Core.Settings;
using Wandur.Desktop.Terminal;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.Tests;

public sealed class ThemeMenuButtonTests
{
    [AvaloniaFact]
    public async Task OpeningShowsAccessibleSwatchesAndTheFallbackWithoutSaving()
    {
        await using var fixture = new Fixture(new() { Theme = "Paper", UseWorldThemes = true });
        var before = File.ReadAllText(fixture.Store.FilePath);
        var menu = fixture.Open();

        Assert.Equal(L.Theme, AutomationProperties.GetName(fixture.Button));
        Assert.Equal(L.Theme, ToolTip.GetTip(fixture.Button));
        Assert.Equal(44, fixture.Button.Bounds.Width);
        Assert.Equal(30, fixture.Button.Bounds.Height);
        Assert.Equal(UserTheme.PresetNames.Count + 1, menu.Items.OfType<MenuItem>().Count());
        Assert.True(Item(menu, L.FollowMudTheme).IsChecked);
        var paper = Item(menu, UserTheme.DisplayName("Paper"));
        Assert.True(paper.IsChecked);
        Assert.False(Item(menu, UserTheme.DisplayName("Ember")).IsChecked);
        var peer = ControlAutomationPeer.CreatePeerForElement(paper)!;
        Assert.Equal(UserTheme.DisplayName("Paper"), peer.GetName());
        Assert.Equal(ToggleState.On, Assert.IsAssignableFrom<IToggleProvider>(peer).ToggleState);
        var header = Assert.IsType<StackPanel>(paper.Header);
        Assert.Contains(header.Children.OfType<TextBlock>(), text => text.Text == UserTheme.DisplayName("Paper"));
        var swatches = header.Children.OfType<Border>().Select(border =>
            Assert.IsAssignableFrom<ISolidColorBrush>(border.Background).Color).ToArray();
        Assert.Contains(Color.Parse("#FFFFFF"), swatches);
        Assert.Contains(Color.Parse("#9A7443"), swatches);
        Assert.Equal(before, File.ReadAllText(fixture.Store.FilePath));
    }

    [AvaloniaFact]
    public async Task SelectingAPresetSavesItAndDisablesWorldThemesWithoutChangingOtherSettings()
    {
        await using var fixture = new Fixture(new() { Theme = "Paper", UseWorldThemes = true });
        ClientSettings? saved = null;
        fixture.Controller.SettingsSaved += settings => saved = settings;
        var original = fixture.Controller.Settings;

        Select(Item(fixture.Open(), UserTheme.DisplayName("Forest")));

        Assert.NotNull(saved);
        Assert.Equal("Forest", saved.Theme);
        Assert.False(saved.UseWorldThemes);
        Assert.Same(original.Profiles, saved.Profiles);
        Assert.Same(original.CustomThemes, saved.CustomThemes);
        Assert.Equal(JsonSerializer.Serialize(original with { Theme = "Forest", UseWorldThemes = false }),
            JsonSerializer.Serialize(fixture.Store.Load().Settings));
        Assert.True(Item(fixture.Open(), UserTheme.DisplayName("Forest")).IsChecked);
    }

    [AvaloniaFact]
    public async Task FollowTogglesPolicyAndPreservesTheSavedCustomFallback()
    {
        var custom = UserTheme.FromPreset("Paper") with { Id = "custom-my-palette", Name = "My palette" };
        await using var fixture = new Fixture(new() { Theme = custom.Id, CustomThemes = [custom], UseWorldThemes = false });
        fixture.Controller.StageWorldTheme(WorldTheme());
        fixture.Controller.ApplySettings(fixture.Controller.Settings);

        Select(Item(fixture.Open(), L.FollowMudTheme));
        Assert.True(fixture.Store.Load().Settings.UseWorldThemes);
        Assert.Equal(custom.Id, fixture.Store.Load().Settings.Theme);
        var following = fixture.Open();
        Assert.True(Item(following, L.FollowMudTheme).IsChecked);
        Assert.False(Item(following, custom.Name).IsChecked);
        Assert.DoesNotContain(following.Items.OfType<MenuItem>(), item =>
            AutomationProperties.GetName(item) != L.FollowMudTheme && item.IsChecked);

        Select(Item(following, L.FollowMudTheme));
        Assert.False(fixture.Store.Load().Settings.UseWorldThemes);
        Assert.Equal(custom.Id, fixture.Store.Load().Settings.Theme);
        Assert.True(Item(fixture.Open(), custom.Name).IsChecked);
    }

    [AvaloniaFact]
    public async Task CustomSelectionUsesTheSavedIdAndReopeningReflectsChangedSettings()
    {
        await using var fixture = new Fixture(new() { Theme = "Paper" });
        var menu = fixture.Open();
        var custom = UserTheme.FromPreset("Forest") with { Id = "custom-new-palette", Name = "New palette" };
        fixture.Controller.SaveSettings(fixture.Controller.Settings with { CustomThemes = [custom] });

        Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), item => AutomationProperties.GetName(item) == custom.Name);
        Select(Item(fixture.Open(), custom.Name));
        Assert.Equal(custom.Id, fixture.Store.Load().Settings.Theme);
        Assert.False(fixture.Store.Load().Settings.UseWorldThemes);
        Assert.True(Item(fixture.Open(), custom.Name).IsChecked);

        fixture.Controller.SaveSettings(fixture.Controller.Settings with { Theme = "Ember", CustomThemes = [] });
        var refreshed = fixture.Open();
        Assert.True(Item(refreshed, UserTheme.DisplayName("Ember")).IsChecked);
        Assert.DoesNotContain(refreshed.Items.OfType<MenuItem>(), item => AutomationProperties.GetName(item) == custom.Name);
    }

    [AvaloniaFact]
    public async Task OpeningAndSelectingResolveTheCurrentControllerAndLatestSettings()
    {
        await using var first = new Fixture(new() { Theme = "Paper", UseWorldThemes = false });
        await using var second = new Fixture(new() { Theme = "Forest", UseWorldThemes = false });
        first.Current = second.Controller;
        Assert.True(Item(first.Open(), UserTheme.DisplayName("Forest")).IsChecked);

        var ember = Item(first.Open(), UserTheme.DisplayName("Ember"));
        first.Current = first.Controller;
        var custom = UserTheme.FromPreset("Paper") with { Id = "custom-added-later", Name = "Added later" };
        first.Controller.SaveSettings(first.Controller.Settings with { CustomThemes = [custom] });
        Select(ember);

        Assert.Equal("Ember", first.Store.Load().Settings.Theme);
        Assert.Equal(custom.Id, Assert.Single(first.Store.Load().Settings.CustomThemes).Id);
        Assert.Equal("Forest", second.Store.Load().Settings.Theme);
    }

    [AvaloniaFact]
    public async Task FollowingWithoutAWorldThemeChecksTheFallbackOnTheNextOpening()
    {
        await using var fixture = new Fixture(new() { Theme = "Moonlight", UseWorldThemes = true });
        fixture.Controller.StageWorldTheme(WorldTheme());
        fixture.Controller.ApplySettings(fixture.Controller.Settings);
        Assert.False(Item(fixture.Open(), UserTheme.DisplayName("Moonlight")).IsChecked);

        fixture.Controller.StageWorldTheme(null);
        fixture.Controller.ApplySettings(fixture.Controller.Settings);
        var menu = fixture.Open();
        Assert.True(Item(menu, UserTheme.DisplayName("Moonlight")).IsChecked);
        Assert.True(Item(menu, L.FollowMudTheme).IsChecked);
    }

    [AvaloniaFact]
    public async Task SaveFailureShowsANoticeAndKeepsTheSavedSelection()
    {
        await using var fixture = new Fixture(new() { Theme = "Paper", UseWorldThemes = false }, readOnly: true);
        var error = Record.Exception(() => Select(Item(fixture.Open(), UserTheme.DisplayName("Ember"))));
        Assert.Null(error);
        Assert.Equal("Read-only test storage", fixture.Controller.Notice);
        Assert.Equal("Paper", fixture.Store.Load().Settings.Theme);
        Assert.Equal("Paper", fixture.Controller.Settings.Theme);
        Assert.True(Item(fixture.Open(), UserTheme.DisplayName("Paper")).IsChecked);
    }

    [AvaloniaFact]
    public async Task KeyboardOpensTheMenuAndLabelsFollowTheCurrentLanguage()
    {
        var originalLanguage = UiLanguage.Culture.Name;
        await using var fixture = new Fixture(new() { Theme = "Paper" });
        try
        {
            UiLanguage.Apply("fr");
            Dispatcher.UIThread.RunJobs();
            Assert.True(fixture.Button.Focus());
            fixture.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            fixture.Window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Dispatcher.UIThread.RunJobs();
            var menu = Assert.IsType<MenuFlyout>(fixture.Button.Flyout);
            Assert.True(menu.IsOpen);
            Assert.Equal(L.Theme, AutomationProperties.GetName(fixture.Button));
            Assert.Equal(L.Theme, ToolTip.GetTip(fixture.Button));
            Assert.True(Item(menu, UserTheme.DisplayName("Paper")).IsChecked);
            Assert.NotNull(Item(menu, L.FollowMudTheme));
        }
        finally { UiLanguage.Apply(originalLanguage); }
    }

    private static WorldTheme WorldTheme() => JsonSerializer.Deserialize<WorldTheme>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "world-theme.json")))!;

    private static MenuItem Item(MenuFlyout menu, string label) =>
        Assert.Single(menu.Items.OfType<MenuItem>(), item => AutomationProperties.GetName(item) == label);

    private static void Select(MenuItem item)
    {
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "wandur-theme-menu-" + Guid.NewGuid());
        public SettingsStore Store { get; }
        public WorkspaceController Controller { get; }
        public WorkspaceController Current { get; set; }
        public ThemeMenuButton Button { get; }
        public Window Window { get; }

        public Fixture(ClientSettings settings, bool readOnly = false)
        {
            Store = new(Path.Combine(_directory, "settings.json"));
            Store.Save(settings);
            Controller = new(new TranscriptDisplayFactory(), readOnly ? new ReadOnlyStore(Store) : Store,
                new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
            Current = Controller;
            Button = new(() => Current) { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            Window = new() { Width = 360, Height = 500, Content = Button };
            Window.Show();
            Dispatcher.UIThread.RunJobs();
        }

        public MenuFlyout Open()
        {
            var menu = Assert.IsType<MenuFlyout>(Button.Flyout);
            menu.Hide();
            Dispatcher.UIThread.RunJobs();
            var point = Button.TranslatePoint(new Point(Button.Bounds.Width / 2, Button.Bounds.Height / 2), Window)!.Value;
            Window.MouseDown(point, MouseButton.Left);
            Window.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.True(menu.IsOpen);
            return menu;
        }

        public async ValueTask DisposeAsync()
        {
            Button.Flyout?.Hide();
            Window.Close();
            await Controller.DisposeAsync();
            ThemeService.Apply(new());
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class ReadOnlyStore(ISettingsStore inner) : ISettingsStore
    {
        public string FilePath => inner.FilePath;
        public SettingsLoadResult Load() => inner.Load();
        public void Save(ClientSettings settings) => throw new IOException("Read-only test storage");
    }
}
