using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wandur.Core.Settings;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.ViewModels;

public sealed partial class ThemeColorViewModel : ObservableObject
{
    private bool _syncing;
    private readonly Action _changed;
    private readonly string? _defaultHex;
    public void RefreshLanguage() => OnPropertyChanged(nameof(Label));
    [RelayCommand] private void Reset() { if (_defaultHex is not null) Hex = _defaultHex; }
    public string Key { get; }
    public string Label => Key switch
    {
        "Shell" => L.PaletteShell, "Panel" => L.PalettePanel, "Terminal" => L.PaletteTerminal,
        "Text" => L.PaletteText, "Muted" => L.PaletteMuted, "Accent" => L.PaletteAccent,
        "AccentSecondary" => L.PaletteAccentSecondary, "Border" => L.PaletteBorder, "TerminalText" => L.PaletteTerminalText,
        "Chrome" => L.PaletteChrome, "Selection" => L.PaletteSelection, "Button" => L.PaletteButton,
        "ButtonText" => L.PaletteButtonText, "PrimaryText" => L.PalettePrimaryText, "MapBackground" => L.PaletteMapBackground,
        "MapGrid" => L.PaletteMapGrid, "EditorBackground" => L.PaletteEditorBackground, "EditorText" => L.PaletteEditorText,
        "Ansi0" => L.AnsiBlack, "Ansi1" => L.AnsiRed, "Ansi2" => L.AnsiGreen, "Ansi3" => L.AnsiYellow,
        "Ansi4" => L.AnsiBlue, "Ansi5" => L.AnsiMagenta, "Ansi6" => L.AnsiCyan, "Ansi7" => L.AnsiWhite,
        "Ansi8" => L.AnsiBrightBlack, "Ansi9" => L.AnsiBrightRed, "Ansi10" => L.AnsiBrightGreen, "Ansi11" => L.AnsiBrightYellow,
        "Ansi12" => L.AnsiBrightBlue, "Ansi13" => L.AnsiBrightMagenta, "Ansi14" => L.AnsiBrightCyan, "Ansi15" => L.AnsiBrightWhite,
        _ => Key
    };
    [ObservableProperty] private string _hex;
    [ObservableProperty] private Color _color;
    public ThemeColorViewModel(string key, string hex, Action changed, string? defaultHex = null)
    { _defaultHex = defaultHex; Key = key; _hex = hex; _color = UserTheme.IsColor(hex) ? Color.Parse(hex) : Avalonia.Media.Colors.Black; _changed = changed; }
    partial void OnHexChanged(string value)
    {
        if (_syncing) return;
        _syncing = true;
        if (UserTheme.IsColor(value)) Color = Color.Parse(value);
        _syncing = false;
        _changed();
    }
    partial void OnColorChanged(Color value)
    {
        if (_syncing) return;
        _syncing = true; Hex = $"#{value.R:X2}{value.G:X2}{value.B:X2}"; _syncing = false;
        _changed();
    }
}

public sealed partial class ThemeChoiceViewModel(string id, string name) : ObservableObject
{
    public string Id { get; } = id;
    [ObservableProperty] private string _name = name;
}
