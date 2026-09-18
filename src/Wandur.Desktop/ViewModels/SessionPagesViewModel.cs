using CommunityToolkit.Mvvm.ComponentModel;
using Wandur.Desktop.Services;

namespace Wandur.Desktop.ViewModels;

public enum SessionPage { Play, Diagnostics }

/// <summary>Presentation state for a connection; definition editors belong to world configuration.</summary>
public sealed partial class SessionPagesViewModel(WorldScriptLibrary library) : ObservableObject, IDisposable
{
    private SessionAutomationViewModel? _automation;
    public SessionAutomationViewModel Automation => _automation ??= new(library);
    [ObservableProperty] private SessionPage _selectedPage;
    public int SelectedIndex { get => (int)SelectedPage; set { if (Enum.IsDefined(typeof(SessionPage), value)) SelectedPage = (SessionPage)value; } }
    public bool IsPlay => SelectedPage == SessionPage.Play;
    public bool IsDiagnostics => SelectedPage == SessionPage.Diagnostics;
    partial void OnSelectedPageChanged(SessionPage value)
    {
        OnPropertyChanged(nameof(SelectedIndex)); OnPropertyChanged(nameof(IsPlay)); OnPropertyChanged(nameof(IsDiagnostics));
    }
    public void Dispose() => _automation?.Dispose();
}
