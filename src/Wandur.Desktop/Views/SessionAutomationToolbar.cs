using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Wandur.Desktop.ViewModels;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.Views;

/// <summary>Compact live controls; definitions remain in the world's configuration.</summary>
public sealed class SessionAutomationToolbar : UserControl
{
    public SessionAutomationToolbar(SessionAutomationViewModel model, Action<int>? editConfiguration, AgentSessionViewModel? agent)
    {
        Name = "SessionAutomation";
        var scripts = new SessionScriptsButton(model, editConfiguration);
        var macros = new ToggleButton { Name = "SessionMacros", Width = double.NaN, Height = 26, MinHeight = 0, Padding = new Thickness(6, 0), FontSize = 11 };
        macros.Classes.Add("command-bar-button");
        macros.Bind(ContentControl.ContentProperty, LocalizedText.Binding(nameof(L.MacrosTab)));
        macros.Bind(ToolTip.TipProperty, LocalizedText.Binding(nameof(L.MacrosLiveHelp)));
        macros.Bind(Avalonia.Automation.AutomationProperties.NameProperty, LocalizedText.Binding(nameof(L.MacrosTab)));
        macros.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(model.MacrosEnabled)) { Source = model, Mode = BindingMode.TwoWay });
        macros.Bind(IsEnabledProperty, new Binding(nameof(model.HasMacros)) { Source = model });
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Children = { scripts, macros } };
        if (agent is null) { Content = controls; return; }
        var agentButton = new Button { Name = "SessionAgent", Width = double.NaN, Height = 26, MinHeight = 0, Padding = new Thickness(6, 0), FontSize = 11 };
        agentButton.Classes.Add("command-bar-button");
        agentButton.Bind(ContentControl.ContentProperty, LocalizedText.Binding(nameof(L.AgentMenu)));
        agentButton.Bind(Avalonia.Automation.AutomationProperties.NameProperty, LocalizedText.Binding(nameof(L.AgentMenu)));
        agentButton.Flyout = new Flyout { Placement = PlacementMode.TopEdgeAlignedRight,
            Content = new ScrollViewer { Width = 340, MaxHeight = 580, Content = new AgentSessionView(agent,
                editConfiguration is null ? null : section => { agentButton.Flyout?.Hide(); editConfiguration(section); }) } };
        controls.Children.Add(agentButton);
        var play = Ui.ToolbarIconKey(new Button { Name = "PlaySessionAgent", Command = agent.RunCommand }, "M 4,2 L 13,8 L 4,14 Z", nameof(L.AgentPlay));
        play.Height = 26; play.MinHeight = 0; play.Padding = new Thickness(5, 0);
        play.Bind(IsVisibleProperty, new Binding("!" + nameof(agent.IsBusy)) { Source = agent });
        var stop = Ui.ToolbarIconKey(new Button { Name = "StopSessionAgent", Command = agent.StopCommand }, "M 3,3 H 13 V 13 H 3 Z", nameof(L.AgentStop));
        stop.Height = 26; stop.MinHeight = 0; stop.Padding = new Thickness(5, 0);
        stop.Bind(IsVisibleProperty, new Binding(nameof(agent.IsBusy)) { Source = agent });
        controls.Children.Add(play); controls.Children.Add(stop);
        var status = Ui.Text("", 11, "muted"); status.Name = "SessionAgentStatus";
        status.VerticalAlignment = VerticalAlignment.Center; status.Margin = new Thickness(8, 0, 0, 0);
        status.TextWrapping = TextWrapping.NoWrap; status.TextTrimming = TextTrimming.CharacterEllipsis;
        status.Bind(TextBlock.TextProperty, new Binding(nameof(agent.Status)) { Source = agent });
        status.Bind(ToolTip.TipProperty, new Binding(nameof(agent.Status)) { Source = agent });
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children = { controls, status } };
        Grid.SetColumn(status, 1); Content = layout;
    }
}
