using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wandur.Desktop.Services;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.ViewModels;

public sealed class ScriptLibraryItemViewModel : ObservableObject
{
    private readonly WorldScriptLibrary _library;
    public WorldScriptEntry Entry { get; }
    public IAsyncRelayCommand<bool> EnableCommand { get; }
    public string Name => Entry.Name;
    public bool IsPack => Entry.IsPack;
    /// <summary>The "pack" marker and provenance shown beside a supplied script.</summary>
    public string PackLabel => Entry.Pack is { } pack ? L.ScriptPackMarker + " · " + Provenance(pack.Provenance) : "";
    public static string Provenance(string value)
        => value == Wandur.Core.Scripting.ScriptPackInfo.Reviewed ? L.ScriptPackReviewed : L.ScriptPackGenerated;
    public bool Enabled
    {
        get => Entry.Enabled;
        set { if (value != Enabled) EnableCommand.Execute(value); }
    }
    public string Status => Entry.Runtime.Error is not null ? L.ScriptFailed :
        Entry.Runtime.IsBusy ? L.ScriptBusy : Entry.Runtime.IsPaused ? L.ScriptPaused :
        Entry.Runtime.IsRunning ? L.ScriptRunning : Entry.Enabled ? L.ScriptWaiting : L.ScriptDisabled;

    public ScriptLibraryItemViewModel(WorldScriptLibrary library, WorldScriptEntry entry, bool persistEnabled = true)
    {
        _library = library; Entry = entry;
        EnableCommand = new AsyncRelayCommand<bool>(enabled => _library.SetEnabledAsync(Entry, enabled, persistEnabled));
    }
    internal void Refresh()
    {
        OnPropertyChanged(nameof(Name)); OnPropertyChanged(nameof(Enabled)); OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(IsPack)); OnPropertyChanged(nameof(PackLabel));
    }
}

/// <summary>Presentation state for one world's library; keeps selection and drafts across view updates.</summary>
public sealed partial class ScriptLibraryViewModel : ObservableObject, IDisposable
{
    private readonly WorldScriptLibrary _library;
    private ScriptLibraryItemViewModel? _selected;
    private bool _confirmDelete;
    private bool _disposed;
    public ObservableCollection<ScriptLibraryItemViewModel> Items { get; } = [];
    public ScriptLibraryItemViewModel? Selected
    {
        get => _selected;
        set { if (SetProperty(ref _selected, value)) { ConfirmDelete = false; RefreshSelection(); } }
    }
    public string Title => L.Format(L.ScriptEditorTitle, _library.WorldName);
    public bool HasSelected => Selected is not null;
    public bool IsEmpty => Items.Count == 0;
    public string Name { get => Selected?.Entry.Name ?? ""; set { if (Selected is { } item) item.Entry.Name = value; } }
    public string Source { get => Selected?.Entry.Source ?? ""; set { if (Selected is { } item) item.Entry.Source = value; } }
    public bool Enabled { get => Selected?.Enabled ?? false; set { if (Selected is { } item) item.Enabled = value; } }
    public string Log => Selected?.Entry.Runtime.Log ?? "";
    public string Status => Selected?.Status ?? "";
    public string? Error => _library.Error ?? Selected?.Entry.Runtime.Error;
    public bool HasUnsavedChanges => Selected?.Entry.HasUnsavedChanges == true;
    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool ConfirmDelete { get => _confirmDelete; set => SetProperty(ref _confirmDelete, value); }
    public bool CanSave => HasSelected && !IsPack && Selected?.Entry.Runtime.IsBusy != true;
    public bool IsPack => Selected?.IsPack == true;
    public bool IsReadOnly => IsPack;
    public string PackLabel => Selected?.PackLabel ?? "";
    public string PackDescription => Selected?.Entry.Pack?.Description ?? "";
    public bool HasPackDescription => IsPack && PackDescription.Length > 0;
    /// <summary>The per-script choice that lifts a supplied script's restricted send policy.</summary>
    public bool AllowSend
    {
        get => Selected?.Entry.AllowSend == true;
        set { if (Selected is not null && value != AllowSend) AllowSendCommand.Execute(value); }
    }

    public ScriptLibraryViewModel(WorldScriptLibrary library)
    {
        _library = library; _library.Changed += Refresh; Wandur.Core.Localization.UiLanguage.Changed += Refresh; Refresh();
    }
    [RelayCommand]
    private void New() { var entry = _library.Add(); Refresh(); Selected = Items.FirstOrDefault(i => i.Entry == entry); }
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync() { if (Selected is { } item) await _library.SaveAsync(item.Entry); Refresh(); }
    [RelayCommand]
    private async Task AllowSendAsync(bool allow)
    {
        if (Selected is { } item) await _library.SetAllowSendAsync(item.Entry, allow);
        Refresh();
    }
    [RelayCommand]
    private void Duplicate()
    {
        if (Selected is not { } item) return;
        var copy = _library.Duplicate(item.Entry);
        Refresh();
        Selected = Items.FirstOrDefault(candidate => candidate.Entry == copy);
    }
    [RelayCommand]
    private void RequestDelete() => ConfirmDelete = HasSelected;
    [RelayCommand]
    private void CancelDelete() => ConfirmDelete = false;
    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (Selected is { } item && ConfirmDelete) await _library.DeleteAsync(item.Entry);
        ConfirmDelete = false; Refresh();
    }
    private void Refresh()
    {
        if (_disposed) return;
        foreach (var removed in Items.Where(i => !_library.Items.Contains(i.Entry)).ToArray()) Items.Remove(removed);
        foreach (var entry in _library.Items.Where(entry => !entry.IsMacro))
            if (!Items.Any(i => i.Entry == entry)) Items.Add(new(_library, entry));
        foreach (var item in Items) item.Refresh();
        if (Selected is null || !Items.Contains(Selected)) Selected = Items.FirstOrDefault();
        OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(IsEmpty)); RefreshSelection();
    }
    private void RefreshSelection()
    {
        foreach (var property in new[] { nameof(HasSelected), nameof(Name), nameof(Source), nameof(Enabled), nameof(Log), nameof(Status), nameof(Error), nameof(HasError), nameof(HasUnsavedChanges), nameof(CanSave),
            nameof(IsPack), nameof(IsReadOnly), nameof(PackLabel), nameof(PackDescription), nameof(HasPackDescription), nameof(AllowSend) })
            OnPropertyChanged(property);
        SaveCommand.NotifyCanExecuteChanged();
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _library.Changed -= Refresh; Wandur.Core.Localization.UiLanguage.Changed -= Refresh; }
}
