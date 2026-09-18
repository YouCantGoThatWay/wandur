using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Wandur.Desktop.ViewModels;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.Views;

/// <summary>Reusable agent configuration editor. It never starts an agent or sends game commands.</summary>
public sealed class AgentSettingsView : UserControl
{
    public AgentSettingsView(AgentProfileViewModel model, bool saveWithDialog = false)
    {
        DataContext = model;
        Binding TwoWay(string property) => new(property) { Mode = BindingMode.TwoWay };
        Control Field(string key, Control input)
        {
            input.Bind(Avalonia.Automation.AutomationProperties.NameProperty, LocalizedText.Binding(key));
            return new StackPanel { Spacing = 3, Children = { Ui.TextKey(key, 11, "muted"), input } };
        }
        TextBox Text(string property, string key, bool multiline = false)
        {
            var box = new TextBox { Name = "Agent" + property, FontSize = 12, MinHeight = 28, Padding = new Thickness(6, 3),
                AcceptsReturn = multiline, TextWrapping = multiline ? Avalonia.Media.TextWrapping.Wrap : Avalonia.Media.TextWrapping.NoWrap };
            box.Bind(TextBox.TextProperty, TwoWay(property));
            box.Bind(Avalonia.Automation.AutomationProperties.NameProperty, LocalizedText.Binding(key));
            ScrollViewer.SetVerticalScrollBarVisibility(box, multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled);
            return box;
        }
        CheckBox Check(string property, string key)
        {
            var check = new CheckBox { Name = "Agent" + property, FontSize = 12, MinHeight = 28 };
            check.Bind(ContentControl.ContentProperty, LocalizedText.Binding(key));
            check.Bind(ToggleButton.IsCheckedProperty, TwoWay(property)); return check;
        }
        var save = Ui.ToolbarIconKey(new Button { Name = "SaveAgentSettings", Command = model.SaveCommand },
            "M 2,2 H 11 L 14,5 V 14 H 2 Z M 5,2 V 6 H 10 V 2 M 5,14 V 9 H 11 V 14", nameof(L.AgentSaveSettings));
        save.IsVisible = !saveWithDialog;
        var discover = Ui.ToolbarIconKey(new Button { Name = "DiscoverAgentModels", Command = model.DiscoverModelsCommand },
            "M 13,5 A 6,6 0 1 0 14,10 M 13,1 V 5 H 9", nameof(L.AgentDiscoverModels));
        var title = Ui.TextKey(nameof(L.AgentSettings), 12); title.VerticalAlignment = VerticalAlignment.Center;
        var toolbar = Ui.Toolbar(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { save, discover, title } }, "AgentToolbar");
        if (!saveWithDialog) KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.S, OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control), Command = model.SaveCommand });

        var provider = new ComboBox { Name = "AgentProvider", ItemsSource = model.Providers, MinHeight = 28, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemTemplate = new FuncDataTemplate<LocalizedChoiceViewModel>((_, _) => { var text = Ui.Text("", 12); text.Bind(TextBlock.TextProperty, new Binding(nameof(LocalizedChoiceViewModel.Label))); return text; }) };
        provider.Bind(SelectingItemsControl.SelectedIndexProperty, TwoWay(nameof(model.ProviderIndex)));
        var models = new ComboBox { Name = "AgentDiscoveredModels", MinHeight = 28, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
        models.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(model.Models)));
        models.Bind(SelectingItemsControl.SelectedItemProperty, TwoWay(nameof(model.SelectedModel)));
        models.Bind(ComboBox.PlaceholderTextProperty, LocalizedText.Binding(nameof(L.AgentChooseModel)));
        var modelRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 8,
            Children = { Field(nameof(L.AgentModelId), Text(nameof(model.Model), nameof(L.AgentModelId))), Field(nameof(L.AgentDiscoveredModels), models) } };
        Grid.SetColumn(modelRow.Children[1], 1);
        var key = Text(nameof(model.ApiKey), nameof(L.AgentApiKey)); key.PasswordChar = '●';
        var credentials = new Expander { Name = "AgentCredentials", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel { Spacing = 4, Children = { Field(nameof(L.AgentApiKey), key), Ui.TextKey(nameof(L.AgentKeyHelp), 11, "muted"), Check(nameof(model.ForgetKey), nameof(L.AgentForgetKey)) } } };
        credentials.Bind(HeaderedContentControl.HeaderProperty, LocalizedText.Binding(nameof(L.AgentCredentials)));
        var endpointHelp = Ui.Text("", 11, "muted");
        endpointHelp.Bind(TextBlock.TextProperty, new Binding(nameof(model.EndpointHelp)));
        var connection = new StackPanel { Spacing = 7, Children =
        {
            Field(nameof(L.AgentServerAddress), Text(nameof(model.ServerAddress), nameof(L.AgentServerAddress))),
            Ui.TextKey(nameof(L.AgentServerAddressHelp), 11, "muted"),
            Field(nameof(L.AgentHostingProvider), provider), endpointHelp, modelRow, credentials
        } };

        TabItem Editor(string property, string key, string helpKey)
        {
            var help = Ui.TextKey(helpKey, 11, "muted"); help.Margin = new Thickness(0, 4, 0, 6);
            var editor = Text(property, key, true);
            var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Children = { help, editor } };
            Grid.SetRow(editor, 1);
            return new TabItem { Header = Ui.TextKey(key, 12), Content = body, FontSize = 12 };
        }
        var editors = new TabControl { Name = "AgentEditors", MinHeight = 140, ItemsSource = new[]
        {
            Editor(nameof(model.SystemPrompt), nameof(L.AgentSystemPrompt), nameof(L.AgentSystemPromptHelp)),
            new TabItem { Header = Ui.TextKey(nameof(L.AgentGoals), 12), Content = new AgentGoalsEditor(model), FontSize = 12 },
            Editor(nameof(model.Commands), nameof(L.AgentCommands), nameof(L.AgentCommandsHelp))
        } };
        var limitsGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), RowDefinitions = new RowDefinitions("Auto,Auto"), ColumnSpacing = 8, RowSpacing = 6 };
        var index = 0;
        void Limit(string property, string key, decimal maximum, decimal minimum = 1, decimal increment = 1)
        {
            var input = new NumericUpDown { Name = "Agent" + property, Minimum = minimum, Maximum = maximum, Increment = increment,
                FontSize = 12, MinHeight = 28, FormatString = increment < 1 ? "0.##" : "0", HorizontalAlignment = HorizontalAlignment.Stretch };
            input.Bind(NumericUpDown.ValueProperty, TwoWay(property));
            var field = Field(key, input); Grid.SetColumn(field, index % 3); Grid.SetRow(field, index / 3); limitsGrid.Children.Add(field); index++;
        }
        Limit(nameof(model.MaxDecisions), nameof(L.AgentMaxDecisions), 1000);
        Limit(nameof(model.MaxRunSeconds), nameof(L.AgentMaxRunSeconds), 86400);
        Limit(nameof(model.ActionIntervalSeconds), nameof(L.AgentActionInterval), 3600);
        Limit(nameof(model.ResponseTimeoutSeconds), nameof(L.AgentResponseTimeout), 600);
        Limit(nameof(model.MaxInputCharacters), nameof(L.AgentMaxInputCharacters), 128000, 1024);
        Limit(nameof(model.MaxOutputTokens), nameof(L.AgentMaxOutputTokens), 8192, 64);
        var jsonMode = Check(nameof(model.JsonMode), nameof(L.AgentJsonMode));
        jsonMode.Bind(IsVisibleProperty, new Binding(nameof(model.SupportsJsonMode)));
        var limits = new Expander { Name = "AgentLimits", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new ScrollViewer { MaxHeight = 200, Content = new StackPanel { Spacing = 6, Children = { limitsGrid, jsonMode } } } };
        limits.Bind(HeaderedContentControl.HeaderProperty, LocalizedText.Binding(nameof(L.AgentLimits)));
        var form = new Grid { Margin = new Thickness(12, 8), RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 8,
            Children = { connection, editors, limits } };
        Grid.SetRow(editors, 1); Grid.SetRow(limits, 2);
        var formHost = new ScrollViewer { Content = form, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch };
        var error = Ui.Text("", 12); error.Bind(TextBlock.TextProperty, new Binding(nameof(model.Error))); error.Bind(IsVisibleProperty, new Binding(nameof(model.HasError)));
        var status = Ui.Text("", 11, "muted"); status.Bind(TextBlock.TextProperty, new Binding(nameof(model.Status)));
        var unsaved = Ui.TextKey(nameof(L.ScriptUnsaved), 11, "muted"); unsaved.Bind(IsVisibleProperty, new Binding(nameof(model.HasUnsavedChanges)));
        var progress = new ProgressBar { IsIndeterminate = true, Height = 3 };
        progress.Bind(IsVisibleProperty, new Binding(nameof(model.IsDiscovering)));
        var footer = new StackPanel { Margin = new Thickness(12, 4), Spacing = 3, Children = { progress, error, status, unsaved } };
        Content = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Children = { toolbar, formHost, footer } };
        Grid.SetRow(formHost, 1); Grid.SetRow(footer, 2);
    }
}
