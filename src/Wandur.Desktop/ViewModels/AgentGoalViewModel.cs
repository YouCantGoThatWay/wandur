using CommunityToolkit.Mvvm.ComponentModel;
using Wandur.Core.Agents;

namespace Wandur.Desktop.ViewModels;

public sealed partial class AgentGoalViewModel : ObservableObject
{
    public Guid Id { get; }
    [ObservableProperty] private string _name;
    [ObservableProperty] private string _rules;
    [ObservableProperty] private string _text;
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Text : Name;
    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(DisplayName));
    partial void OnTextChanged(string value) => OnPropertyChanged(nameof(DisplayName));
    [ObservableProperty] private bool _enabled;
    public AgentGoalViewModel(AgentGoal goal) { Id = goal.Id; _name = goal.Name; _rules = goal.Rules; _text = goal.Text; _enabled = goal.Enabled; }
    public AgentGoal ToDefinition() => new(Id, Text ?? "", Enabled) { Name = Name ?? "", Rules = Rules ?? "" };
}
