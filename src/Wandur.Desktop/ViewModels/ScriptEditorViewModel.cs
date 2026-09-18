using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wandur.Desktop.Services;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.ViewModels;

/// <summary>Edits the script owned by one session, regardless of the selected tab.</summary>
public sealed partial class ScriptEditorViewModel : ObservableObject, IDisposable
{
    private readonly SessionScripts _scripts;
    private bool _disposed;

    public ScriptEditorViewModel(SessionScripts scripts)
    {
        _scripts = scripts;
        _scripts.Changed += Refresh;
    }

    public string Source
    {
        get => _scripts.Source;
        set { if (_scripts.Source != value) _scripts.Source = value; }
    }
    public string Title => L.Format(L.ScriptEditorTitle, _scripts.WorldName);
    public string Log => _scripts.Log;
    public string? Error => _scripts.Error;
    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool IsRunning => _scripts.IsRunning;
    public bool IsBusy => _scripts.IsBusy;
    public bool IsPaused => _scripts.IsPaused;
    public bool CanRun => !_disposed && _scripts.CanRun && !IsBusy;
    public bool CanStop => !_disposed && (IsRunning || IsBusy);
    public string Status => IsBusy ? L.ScriptBusy : IsPaused ? L.ScriptPaused : IsRunning ? L.ScriptRunning : L.ScriptStopped;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task RunAsync() => _scripts.RunAsync();
    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _scripts.Stop();
    [RelayCommand]
    private void Save() => _scripts.Save();

    private void Refresh()
    {
        OnPropertyChanged(nameof(Source));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Log));
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(Status));
        RunCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _scripts.Changed -= Refresh;
        RunCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }
}
