using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wandur.Core.Agents;
using Wandur.Core.Localization;
using Wandur.Desktop.Services;

namespace Wandur.Desktop.ViewModels;

public sealed partial class AgentSessionViewModel : ObservableObject, IDisposable
{
    private readonly AgentRunner _runner;
    private readonly IAgentClientServices _services;
    private string? _worldKey;
    private Guid? _profileId;
    private bool _selectingGoal;
    public ObservableCollection<AgentGoalViewModel> Goals { get; } = [];
    public string Goal => AgentGoals.Compose(Goals.Select(g => g.ToDefinition()));
    public bool HasNoGoals => Goals.Count == 0;
    private void LoadGoals(AgentProfile profile, bool preserveSelection)
    {
        var selectedId = Goals.FirstOrDefault(g => g.Enabled)?.Id;
        var hadGoals = Goals.Count > 0;
        foreach (var goal in Goals) goal.PropertyChanged -= GoalChanged;
        Goals.Clear();
        var definitions = AgentGoals.FromProfile(profile);
        var keepSelection = preserveSelection && hadGoals && (selectedId is null || definitions.Any(g => g.Id == selectedId));
        foreach (var definition in definitions)
        {
            var goal = new AgentGoalViewModel(definition);
            if (keepSelection) goal.Enabled = goal.Id == selectedId;
            goal.PropertyChanged += GoalChanged; Goals.Add(goal);
        }
        OnPropertyChanged(nameof(HasNoGoals)); OnPropertyChanged(nameof(Goal));
    }
    private void GoalChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_selectingGoal || args.PropertyName is not (nameof(AgentGoalViewModel.Enabled) or nameof(AgentGoalViewModel.Text) or nameof(AgentGoalViewModel.Name) or nameof(AgentGoalViewModel.Rules))) return;
        _selectingGoal = true;
        try
        {
            if (sender is AgentGoalViewModel { Enabled: true } selected && args.PropertyName == nameof(AgentGoalViewModel.Enabled))
                foreach (var goal in Goals.Where(g => g != selected)) goal.Enabled = false;
        }
        finally { _selectingGoal = false; }
        _runner.ClearMemory();
        OnPropertyChanged(nameof(Goal)); Refresh();
    }
    public string Status => Strings.ResourceManager.GetString(_runner.StatusKey, UiLanguage.Culture) ?? _runner.StatusKey;
    public string Activity => _runner.Activity;
    public string Memory => _runner.Memory;
    public bool IsBusy => _runner.IsBusy;
    public bool CanStart => !IsBusy && _worldKey is not null && !string.IsNullOrWhiteSpace(Goal);
    public AgentSessionViewModel(AgentRunner runner, IAgentClientServices services)
    {
        _runner = runner; _services = services; runner.Changed += Refresh;
        services.Profiles.Saved += Saved; UiLanguage.Changed += Refresh;
    }
    public void Configure(string worldKey)
    {
        _runner.ClearMemory(); _worldKey = worldKey;
        try { var profile = _services.Profiles.Load(worldKey); _profileId = profile.Id; LoadGoals(profile, false); }
        catch { _worldKey = null; _runner.Stop("AgentFailed"); }
        Refresh();
    }
    private void Saved(Guid id)
    {
        if (id != _profileId) return;
        _runner.ClearMemory();
        if (_worldKey is not null)
        {
            try { LoadGoals(_services.Profiles.Load(_worldKey), true); Refresh(); }
            catch { _runner.Stop("AgentFailed"); }
        }
    }
    private async Task StartAsync(AgentRunMode mode)
    {
        if (_worldKey is null || !CanStart) return;
        try { await _runner.StartAsync(_services.Profiles.Load(_worldKey), Goal, mode); }
        catch { _runner.Stop("AgentFailed"); }
    }
    [RelayCommand(CanExecute = nameof(CanStart))] private Task PreviewAsync() => StartAsync(AgentRunMode.Preview);
    [RelayCommand(CanExecute = nameof(CanStart))] private Task StepAsync() => StartAsync(AgentRunMode.Step);
    [RelayCommand(CanExecute = nameof(CanStart))] private Task RunAsync() => StartAsync(AgentRunMode.Run);
    [RelayCommand] private void Stop() => _runner.Stop();
    [RelayCommand] private void ResetMemory() => _runner.ClearMemory();
    private void Refresh()
    {
        OnPropertyChanged(nameof(Status)); OnPropertyChanged(nameof(Activity)); OnPropertyChanged(nameof(Memory));
        OnPropertyChanged(nameof(IsBusy)); OnPropertyChanged(nameof(CanStart));
        PreviewCommand.NotifyCanExecuteChanged(); StepCommand.NotifyCanExecuteChanged(); RunCommand.NotifyCanExecuteChanged();
    }
    public void Dispose()
    { foreach (var goal in Goals) goal.PropertyChanged -= GoalChanged; _runner.Stop(); _runner.Changed -= Refresh; _services.Profiles.Saved -= Saved; UiLanguage.Changed -= Refresh; }
}
