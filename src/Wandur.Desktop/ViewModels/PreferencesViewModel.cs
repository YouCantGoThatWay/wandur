using System.Collections.ObjectModel;
using System.Globalization;
using Wandur.Core.Terminal;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wandur.Core.Localization;
using Wandur.Core.Settings;
using Wandur.Desktop.Services;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.ViewModels;

public sealed partial class PreferencesViewModel : ObservableObject, IDisposable
{
    private readonly IClientSettingsStore _store;
    private readonly Action<ClientSettings> _preview;
    private readonly ClientSettings _original;
    private readonly List<UserTheme> _drafts;
    private bool _saved, _loading;
    private readonly string _originalLanguage = UiLanguage.Culture.Name;
    public ObservableCollection<ThemeChoiceViewModel> Themes { get; } = [];
    public ObservableCollection<ThemeColorViewModel> Colors { get; } = [];
    public IReadOnlyList<ThemeChoiceViewModel> Sections { get; } = [new("General", L.SettingsGeneral), new("Appearance", L.SettingsAppearance), new("MudColors", L.SettingsMudColors), new("Terminal", L.SettingsTerminal), new("Input", L.SettingsInput)];
    public ObservableCollection<ThemeColorViewModel> AnsiColors { get; } = [];
    public IReadOnlyList<LanguageChoice> Languages { get; } = UiLanguage.Choices;
    [ObservableProperty] private int _sectionIndex;
    public bool IsGeneral => SectionIndex == 0;
    public bool IsAppearance => SectionIndex == 1;
    public bool IsMudColors => SectionIndex == 2;
    public bool IsTerminal => SectionIndex == 3;
    public bool IsInput => SectionIndex == 4;
    public string SectionTitle => Sections[Math.Clamp(SectionIndex, 0, Sections.Count - 1)].Name;
    public bool IsCustom => _drafts.Any(t => t.Id == Theme);
    [ObservableProperty] private string _theme;
    [ObservableProperty] private string _themeName = "";
    [ObservableProperty] private bool _isLight;
    [ObservableProperty] private bool _useWorldThemes;
    [ObservableProperty] private decimal? _fontSize;
    [ObservableProperty] private string? _foreground;
    [ObservableProperty] private string? _background;
    [ObservableProperty] private bool _localEcho;
    [ObservableProperty] private bool _allowBlinkingText;
    [ObservableProperty] private bool _showChannelsPanel;
    [ObservableProperty] private bool _composerSuggestions;
    [ObservableProperty] private decimal? _scrollTailPercent;
    [ObservableProperty] private LanguageChoice _language;
    [ObservableProperty] private string _error = "";
    public double PreviewFontSize => (double)(FontSize ?? 15);
    /// <summary>
    /// An emptied field means the shipped default rather than a validation error the reader cannot see. The
    /// reader thinks in percent, and a share under a tenth of the window is too small to read, so it turns the split off.
    /// </summary>
    private double TailShare { get { var percent = (int)Math.Round(ScrollTailPercent ?? 25); return percent < 10 ? 0 : Math.Clamp(percent, 10, 60) / 100d; } }
    public event Action? CloseRequested;

    public PreferencesViewModel(IClientSettingsStore store, Action<ClientSettings> preview)
    {
        _store = store; _preview = preview; _original = store.Settings;
        _drafts = _original.CustomThemes.Select(t => t with { Colors = new(t.Colors), AnsiColors = new(t.AnsiColors) }).ToList();
        foreach (var id in UserTheme.PresetNames) Themes.Add(new(id, PresetName(id)));
        foreach (var theme in _drafts) Themes.Add(new(theme.Id, theme.Name));
        _theme = _original.Theme; _fontSize = (decimal)_original.FontSize;
        _foreground = _original.Foreground; _background = _original.Background; _localEcho = _original.LocalEcho;
        _useWorldThemes = _original.UseWorldThemes;
        _allowBlinkingText = _original.AllowBlinkingText;
        _showChannelsPanel = _original.ShowChannelsPanel;
        _composerSuggestions = _original.ComposerSuggestions;
        _scrollTailPercent = (decimal)Math.Round(_original.ScrollTailShare * 100);
        _language = Languages.First(l => l.Code == _original.Language);
        LoadPalette();
        UiLanguage.Changed += RefreshLanguage;
    }
    private static string PresetName(string id) => UserTheme.DisplayName(id);
    private ClientSettings Values() => _store.Settings with
    {
        Theme = Theme, FontSize = PreviewFontSize, Foreground = string.IsNullOrWhiteSpace(Foreground) ? null : Foreground.Trim(),
        Background = string.IsNullOrWhiteSpace(Background) ? null : Background.Trim(), LocalEcho = LocalEcho, Language = Language.Code,
        AllowBlinkingText = AllowBlinkingText, UseWorldThemes = UseWorldThemes, ShowChannelsPanel = ShowChannelsPanel, ComposerSuggestions = ComposerSuggestions, ScrollTailShare = TailShare, CustomThemes = _drafts.Select(t => t with { Colors = new(t.Colors), AnsiColors = new(t.AnsiColors) }).ToList()
    };
    private void LoadPalette()
    {
        _loading = true;
        var palette = _drafts.FirstOrDefault(t => t.Id == Theme) ?? UserTheme.FromPreset(Theme);
        ThemeName = IsCustom ? palette.Name : PresetName(Theme); IsLight = palette.IsLight;
        Colors.Clear();
        foreach (var key in UserTheme.ColorKeys) Colors.Add(new(key, palette.Colors[key], EditPalette));
        AnsiColors.Clear();
        // Preset rows show the preset's own palette; drafts reset to the shipped defaults.
        for (var i = 0; i < AnsiPalette.Defaults.Count; i++)
            AnsiColors.Add(new("Ansi" + i, palette.AnsiColors.GetValueOrDefault(i) ?? AnsiPalette.Defaults[i], EditPalette,
                IsCustom ? AnsiPalette.Defaults[i] : palette.AnsiColors.GetValueOrDefault(i) ?? AnsiPalette.Defaults[i]));
        _loading = false;
        OnPropertyChanged(nameof(IsCustom));
        DeleteThemeCommand.NotifyCanExecuteChanged();
    }
    private void EditPalette()
    {
        if (_loading || !IsCustom) return;
        var index = _drafts.FindIndex(t => t.Id == Theme);
        _drafts[index] = _drafts[index] with { Name = ThemeName.Trim(), IsLight = IsLight, Colors = Colors.ToDictionary(c => c.Key, c => c.Hex),
            AnsiColors = AnsiColors.Select((c, i) => (Color: c, Index: i)).Where(c => !string.Equals(c.Color.Hex, AnsiPalette.Defaults[c.Index], StringComparison.OrdinalIgnoreCase)).ToDictionary(c => c.Index, c => c.Color.Hex) };
        Themes.First(t => t.Id == Theme).Name = ThemeName;
        Preview();
    }
    private void RefreshLanguage()
    {
        var names = new[] { L.SettingsGeneral, L.SettingsAppearance, L.SettingsMudColors, L.SettingsTerminal, L.SettingsInput };
        for (var i = 0; i < Sections.Count; i++) Sections[i].Name = names[i];
        foreach (var choice in Themes.Where(t => UserTheme.PresetNames.Contains(t.Id))) choice.Name = PresetName(choice.Id);
        foreach (var color in Colors.Concat(AnsiColors)) color.RefreshLanguage();
        OnPropertyChanged(nameof(SectionTitle));
    }
    partial void OnLanguageChanged(LanguageChoice value) { if (value is not null) UiLanguage.Apply(value.Code); }
    partial void OnThemeNameChanged(string value) => EditPalette();
    partial void OnIsLightChanged(bool value) => EditPalette();
    partial void OnUseWorldThemesChanged(bool value) => Preview();
    partial void OnSectionIndexChanged(int value)
    { foreach (var property in new[] { nameof(IsGeneral), nameof(IsAppearance), nameof(IsMudColors), nameof(IsTerminal), nameof(IsInput), nameof(SectionTitle) }) OnPropertyChanged(property); }
    partial void OnThemeChanged(string value) { LoadPalette(); Preview(); }
    partial void OnFontSizeChanged(decimal? value) { OnPropertyChanged(nameof(PreviewFontSize)); Preview(); }
    partial void OnAllowBlinkingTextChanged(bool value) => Preview();
    partial void OnShowChannelsPanelChanged(bool value) => Preview();
    partial void OnComposerSuggestionsChanged(bool value) => Preview();
    partial void OnScrollTailPercentChanged(decimal? value) => Preview();
    partial void OnForegroundChanged(string? value) => Preview();
    partial void OnBackgroundChanged(string? value) => Preview();
    [RelayCommand]
    private void DuplicateTheme()
    {
        var source = _drafts.FirstOrDefault(t => t.Id == Theme) ?? UserTheme.FromPreset(Theme);
        var number = 1;
        string name;
        do { name = L.Format(L.ThemeCopyName, ThemeName.Length > 70 ? ThemeName[..70] : ThemeName, number++); }
        while (Themes.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)));
        var copy = source with { Id = "custom-" + Guid.NewGuid().ToString("N"), Name = name, Colors = new(source.Colors), AnsiColors = new(source.AnsiColors) };
        _drafts.Add(copy); Themes.Add(new(copy.Id, copy.Name)); Theme = copy.Id;
    }
    [RelayCommand(CanExecute = nameof(IsCustom))]
    private void DeleteTheme()
    {
        var id = Theme;
        Theme = "Ember";
        _drafts.RemoveAll(t => t.Id == id);
        Themes.Remove(Themes.First(t => t.Id == id));
        Preview();
    }
    private void Preview()
    {
        if (_loading) return;
        try { var settings = Values(); settings.Validate(); _preview(settings); Error = ""; }
        catch (ArgumentException ex) { Error = ex.Message; }
    }
    [RelayCommand]
    private void Save()
    {
        try { var settings = Values(); settings.Validate(); _store.SaveSettings(settings); _saved = true; CloseRequested?.Invoke(); }
        catch (Exception ex) { Error = ex.Message; }
    }
    public void Dispose()
    {
        UiLanguage.Changed -= RefreshLanguage;
        if (!_saved) { UiLanguage.Apply(_originalLanguage); _preview(_original); }
    }
}
