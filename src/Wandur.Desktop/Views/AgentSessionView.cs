using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Wandur.Desktop.ViewModels;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.Views;

public sealed class AgentSessionView : UserControl
{
    public AgentSessionView(AgentSessionViewModel model, Action<int>? configure)
    {
        DataContext = model;
        var goalGroup = "agent-goals-" + Guid.NewGuid();
        var goals = new ItemsControl { Name = "SessionAgentGoals", ItemsSource = model.Goals,
            ItemTemplate = new FuncDataTemplate<AgentGoalViewModel>((_, _) =>
            {
                var text = Ui.Text("", 12); text.MaxWidth = 290;
                text.Bind(TextBlock.TextProperty, new Binding(nameof(AgentGoalViewModel.DisplayName)));
                var check = new RadioButton { GroupName = goalGroup, Content = text, HorizontalAlignment = HorizontalAlignment.Stretch };
                check.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(AgentGoalViewModel.Enabled)) { Mode = BindingMode.TwoWay });
                return check;
            }) };
        var empty = Ui.TextKey(nameof(L.AgentGoalsEmpty), 12, "muted");
        empty.Bind(IsVisibleProperty, new Binding(nameof(model.HasNoGoals)));
        var status = Ui.Text("", 12, "muted"); status.Bind(TextBlock.TextProperty, new Binding(nameof(model.Status)));
        var activity = Ui.Text("", 11); activity.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        activity.Bind(TextBlock.TextProperty, new Binding(nameof(model.Activity)));
        Button ActionButton(string key, System.Windows.Input.ICommand command)
        {
            var button = new Button { Command = command, Padding = new Thickness(8, 4), MinHeight = 28, FontSize = 11 };
            button.Bind(ContentControl.ContentProperty, LocalizedText.Binding(key)); return button;
        }
        var configureButton = Ui.ButtonKey(nameof(L.AgentConfigure), () => configure?.Invoke(4)); configureButton.IsEnabled = configure is not null;
        Content = new StackPanel { Spacing = 7, Children =
        {
            Ui.TextKey(nameof(L.AgentGoals), 13), Ui.TextKey(nameof(L.AgentGoalsLiveHelp), 11, "muted"),
            new ScrollViewer { MaxHeight = 240, Content = goals }, empty,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = {
                ActionButton(nameof(L.AgentPlay), model.RunCommand), ActionButton(nameof(L.AgentStop), model.StopCommand) } },
            status, Ui.TextKey(nameof(L.AgentScriptsPaused), 11, "muted"),
            new Expander { Header = Ui.TextKey(nameof(L.AgentActivity), 12), HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = new ScrollViewer { MaxHeight = 160, Content = activity } },
            new Expander { Header = Ui.TextKey(nameof(L.AgentAdvanced), 12), HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = new StackPanel { Spacing = 5, Children = {
                    ActionButton(nameof(L.AgentPreview), model.PreviewCommand), ActionButton(nameof(L.AgentStep), model.StepCommand),
                    ActionButton(nameof(L.AgentResetMemory), model.ResetMemoryCommand) } } }, configureButton
        } };
    }
}
