using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wandur.Desktop.Services;

namespace Wandur.Desktop.ViewModels;

/// <summary>Live switches affect this session only; reload explicitly adopts saved definitions and defaults.</summary>
public sealed partial class SessionAutomationViewModel : ObservableObject, IDisposable
{
    private readonly WorldScriptLibrary _library;
    public ObservableCollection<ScriptLibraryItemViewModel> Items { get; } = [];
    public ObservableCollection<ScriptLibraryItemViewModel> Scripts { get; } = [];
    public bool IsEmpty => Scripts.Count == 0;
    public bool HasMacros => Items.Any(i => i.Entry.IsMacro);
    public bool MacrosEnabled { get => _library.MacrosEnabled; set => SetMacrosEnabledCommand.Execute(value); }
    [RelayCommand] private Task SetMacrosEnabledAsync(bool enabled) => _library.SetMacrosEnabledAsync(enabled);
    public string? Error => _library.Error;
    public bool HasError => !string.IsNullOrEmpty(Error);

    public SessionAutomationViewModel(WorldScriptLibrary library)
    {
        _library = library; library.Changed += Refresh;
        Wandur.Core.Localization.UiLanguage.Changed += Refresh; Refresh();
    }
    [RelayCommand] private Task ReloadAsync() => _library.ReloadAsync();
    [RelayCommand] private async Task StopAllAsync()
    {
        foreach (var item in Items.ToArray()) await item.EnableCommand.ExecuteAsync(false);
    }
    private void Refresh()
    {
        foreach (var item in Items.Where(i => !_library.Items.Contains(i.Entry)).ToArray()) Items.Remove(item);
        foreach (var entry in _library.Items)
            if (!Items.Any(i => i.Entry == entry)) Items.Add(new(_library, entry, persistEnabled: false));
        foreach (var item in Items) item.Refresh();
        foreach (var item in Scripts.Where(i => !Items.Contains(i) || i.Entry.IsMacro).ToArray()) Scripts.Remove(item);
        foreach (var item in Items.Where(i => !i.Entry.IsMacro)) if (!Scripts.Contains(item)) Scripts.Add(item);
        OnPropertyChanged(nameof(HasMacros)); OnPropertyChanged(nameof(MacrosEnabled));
        OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(Error)); OnPropertyChanged(nameof(HasError));
    }
    public void Dispose() { _library.Changed -= Refresh; Wandur.Core.Localization.UiLanguage.Changed -= Refresh; }
}
