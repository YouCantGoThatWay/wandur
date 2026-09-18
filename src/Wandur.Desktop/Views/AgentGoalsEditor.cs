using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Wandur.Desktop.ViewModels;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.Views;

public sealed class AgentGoalsEditor : UserControl
{
    public AgentGoalsEditor(AgentProfileViewModel model)
    {
        DataContext = model;
        var add = Ui.ToolbarIconKey(new Button { Command = model.AddGoalCommand }, "M 8,2 V 14 M 2,8 H 14", nameof(L.AgentAddGoal));
        var remove = Ui.ToolbarIconKey(new Button { Command = model.DeleteGoalCommand }, "M 3,4 H 13 M 6,2 H 10 M 4,4 V 14 H 12 V 4", nameof(L.AgentDeleteGoal));
        remove.Bind(IsEnabledProperty, new Binding(nameof(model.HasSelectedGoal)));
        var templates = new Button { Name = "AddAgentGoalTemplate", Padding = new Thickness(10, 4), MinHeight = 30, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        templates.Classes.Add("app-button"); templates.Classes.Add("quiet");
        templates.Bind(Avalonia.Automation.AutomationProperties.NameProperty, LocalizedText.Binding(nameof(L.AgentAddTemplate)));
        var templateLabel = Ui.TextKey(nameof(L.AgentAddTemplate), 12); templateLabel.VerticalAlignment = VerticalAlignment.Center;
        templates.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
        {
            templateLabel,
            new Avalonia.Controls.Shapes.Path { Data = StreamGeometry.Parse("M 1,2 L 5,6 L 9,2"), Width = 10, Height = 8,
                StrokeThickness = 1.5, VerticalAlignment = VerticalAlignment.Center }
        } };
        ((Avalonia.Controls.Shapes.Path)((StackPanel)templates.Content).Children[1]).Bind(
            Avalonia.Controls.Shapes.Shape.StrokeProperty, new Binding(nameof(templates.Foreground)) { Source = templates });
        var menu = new MenuFlyout();
        foreach (var (key, label) in new[] { ("observe", nameof(L.AgentTemplateObserve)), ("explore", nameof(L.AgentTemplateExplore)), ("inventory", nameof(L.AgentTemplateInventory)) })
        {
            var item = new MenuItem { Command = model.AddGoalTemplateCommand, CommandParameter = key };
            item.Bind(HeaderedSelectingItemsControl.HeaderProperty, LocalizedText.Binding(label)); menu.Items.Add(item);
        }
        templates.Flyout = menu;
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { add, remove, templates } };
        var list = new ListBox { Name = "AgentGoalsList", ItemsSource = model.Goals, MinHeight = 65,
            ItemTemplate = new FuncDataTemplate<AgentGoalViewModel>((_, _) =>
            {
                var text = Ui.Text("", 12); text.TextWrapping = TextWrapping.NoWrap; text.TextTrimming = TextTrimming.CharacterEllipsis;
                text.Bind(TextBlock.TextProperty, new Binding(nameof(AgentGoalViewModel.DisplayName)));
                return text;
            }) };
        list.Bind(SelectingItemsControl.SelectedItemProperty, new Binding(nameof(model.SelectedGoal)) { Mode = BindingMode.TwoWay });
        var name = new TextBox { Name = "AgentGoalName", MaxLength = 120, MinHeight = 28, FontSize = 12 };
        name.Bind(TextBox.TextProperty, new Binding("SelectedGoal.Name") { Mode = BindingMode.TwoWay });
        name.Bind(TextBox.PlaceholderTextProperty, LocalizedText.Binding(nameof(L.AgentGoalName)));
        name.Bind(Avalonia.Automation.AutomationProperties.NameProperty, LocalizedText.Binding(nameof(L.AgentGoalName)));
        var editors = new TabControl { Name = "AgentGoalMarkdownTabs", MinHeight = 130 };
        foreach (var (property, label) in new[] { ("Text", nameof(L.AgentGoalDescription)), ("Rules", nameof(L.AgentGoalRules)) })
        {
            var markdown = new MarkdownCodeEditor { Name = "AgentGoal" + property, MinHeight = 80 };
            markdown.Bind(MarkdownCodeEditor.SourceTextProperty, new Binding("SelectedGoal." + property) { Mode = BindingMode.TwoWay });
            markdown.Bind(Avalonia.Automation.AutomationProperties.NameProperty, LocalizedText.Binding(label));
            editors.Items.Add(new TabItem { Header = Ui.TextKey(label, 12), Content = markdown });
        }
        var fields = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 4, Children = {
            name, editors, Ui.TextKey(nameof(L.AgentGoalRulesHelp), 10, "muted") } };
        Grid.SetRow(editors, 1); Grid.SetRow(fields.Children[2], 2);
        fields.Bind(IsEnabledProperty, new Binding(nameof(model.HasSelectedGoal)));
        var enabled = new CheckBox { FontSize = 12 };
        enabled.Bind(ContentControl.ContentProperty, LocalizedText.Binding(nameof(L.AgentGoalDefaultEnabled)));
        enabled.Bind(ToggleButton.IsCheckedProperty, new Binding("SelectedGoal.Enabled") { Mode = BindingMode.TwoWay });
        enabled.Bind(IsEnabledProperty, new Binding(nameof(model.HasSelectedGoal)));
        var split = new Grid { ColumnDefinitions = new ColumnDefinitions("*,3*"), ColumnSpacing = 8, Children = { list, fields } };
        Grid.SetColumn(fields, 1);
        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), RowSpacing = 4, Children = {
            Ui.TextKey(nameof(L.AgentGoalsEditHelp), 11, "muted"), toolbar, split, enabled } };
        Grid.SetRow(toolbar, 1); Grid.SetRow(split, 2); Grid.SetRow(enabled, 3); Content = body;
    }
}
