using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wandur.Core.Scripting;
using Wandur.Desktop.Services;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.ViewModels;

/// <summary>Form drafts belong to the session; only saved definitions are executable.</summary>
public sealed partial class MacroLibraryViewModel : ObservableObject, IDisposable
{
    private readonly WorldScriptLibrary _library;
    private ScriptLibraryItemViewModel? _selected;
    private bool _disposed;
    [ObservableProperty] private bool _confirmDelete;
    public ObservableCollection<ScriptLibraryItemViewModel> Items { get; } = [];
    public ScriptLibraryItemViewModel? Selected
    {
        get => _selected;
        set { if (SetProperty(ref _selected, value)) { ConfirmDelete = false; RefreshSelection(); } }
    }
    private MacroDefinition? Definition => Selected?.Entry.Macro;
    private void Edit(Func<MacroDefinition, MacroDefinition> edit)
    {
        if (Selected?.Entry is { Macro: { } macro } entry) entry.Macro = edit(macro);
    }
    public string Name { get => Selected?.Entry.Name ?? ""; set { if (Selected is { } item) item.Entry.Name = value; } }
    public string Pattern { get => Definition?.Pattern ?? ""; set => Edit(m => m with { Pattern = value ?? "" }); }
    public string? ShortcutKey
    {
        get => IsShortcut ? Pattern : null;
        set { if (IsShortcut && value is not null) Pattern = value; }
    }
    public string Commands { get => Definition?.Commands ?? ""; set => Edit(m => m with { Commands = value ?? "" }); }
    public bool IgnoreCase { get => Definition?.IgnoreCase ?? false; set => Edit(m => m with { IgnoreCase = value }); }
    public int KindIndex
    {
        get => (int)(Definition?.Kind ?? MacroKind.Trigger);
        set { if (Enum.IsDefined(typeof(MacroKind), value)) Edit(m => m with { Kind = (MacroKind)value, Pattern = (MacroKind)value == MacroKind.Shortcut && m.Kind != MacroKind.Shortcut ? "F1" : m.Pattern }); }
    }
    public int MatchIndex { get => (int)(Definition?.Match ?? MacroMatch.Contains); set { if (Enum.IsDefined(typeof(MacroMatch), value)) Edit(m => m with { Match = (MacroMatch)value }); } }
    public decimal? Interval { get => Definition?.IntervalSeconds; set => Edit(m => m with { IntervalSeconds = value is >= 1 and <= 86400 ? (int)value.Value : 0 }); }
    public bool Enabled { get => Selected?.Enabled ?? false; set { if (Selected is { } item) item.Enabled = value; } }
    public bool HasSelected => Selected is not null;
    public bool IsEmpty => Items.Count == 0;
    public bool IsText => Definition?.Kind is MacroKind.Trigger or MacroKind.Alias;
    public bool IsTrigger => Definition?.Kind == MacroKind.Trigger;
    public bool IsAlias => Definition?.Kind == MacroKind.Alias;
    public bool IsTimer => Definition?.Kind == MacroKind.Timer;
    public bool IsShortcut => Definition?.Kind == MacroKind.Shortcut;
    public IReadOnlyList<LocalizedChoiceViewModel> Kinds { get; } = [new(nameof(L.MacroTrigger)), new(nameof(L.MacroAlias)), new(nameof(L.MacroTimer)), new(nameof(L.MacroShortcut))];
    public IReadOnlyList<LocalizedChoiceViewModel> Matches { get; } = [new(nameof(L.MacroContains)), new(nameof(L.MacroStartsWith)), new(nameof(L.MacroExact))];
    public string Status => Selected?.Status ?? "";
    public string? Error => _library.Error ?? Selected?.Entry.Runtime.Error;
    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool HasUnsavedChanges => Selected?.Entry.HasUnsavedChanges == true;

    public MacroLibraryViewModel(WorldScriptLibrary library)
    {
        _library = library;
        library.Changed += Refresh;
        Wandur.Core.Localization.UiLanguage.Changed += LanguageChanged;
        Refresh();
    }
    [RelayCommand] private void New() { var entry = _library.AddMacro(); Refresh(); Selected = Items.FirstOrDefault(i => i.Entry == entry); }
    [RelayCommand] private async Task SaveAsync() { if (Selected is { } item) await _library.SaveAsync(item.Entry); Refresh(); }
    [RelayCommand] private void RequestDelete() => ConfirmDelete = HasSelected;
    [RelayCommand] private void CancelDelete() => ConfirmDelete = false;
    [RelayCommand] private async Task DeleteAsync()
    {
        if (Selected is { } item && ConfirmDelete) await _library.DeleteAsync(item.Entry);
        ConfirmDelete = false; Refresh();
    }
    private void LanguageChanged()
    {
        foreach (var option in Kinds.Concat(Matches)) option.Refresh();
        Refresh();
    }
    private void Refresh()
    {
        if (_disposed) return;
        foreach (var item in Items.Where(i => !_library.Items.Contains(i.Entry)).ToArray()) Items.Remove(item);
        foreach (var entry in _library.Items.Where(e => e.IsMacro))
            if (!Items.Any(i => i.Entry == entry)) Items.Add(new(_library, entry));
        foreach (var item in Items) item.Refresh();
        if (Selected is null || !Items.Contains(Selected)) Selected = Items.FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty)); RefreshSelection();
    }
    private void RefreshSelection()
    {
        foreach (var name in new[] { nameof(Name), nameof(Pattern), nameof(ShortcutKey), nameof(Commands), nameof(IgnoreCase), nameof(KindIndex), nameof(MatchIndex), nameof(Interval), nameof(Enabled), nameof(HasSelected), nameof(IsText), nameof(IsTrigger), nameof(IsAlias), nameof(IsTimer), nameof(IsShortcut), nameof(Status), nameof(Error), nameof(HasError), nameof(HasUnsavedChanges) }) OnPropertyChanged(name);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _library.Changed -= Refresh;
        Wandur.Core.Localization.UiLanguage.Changed -= LanguageChanged;
    }
}
