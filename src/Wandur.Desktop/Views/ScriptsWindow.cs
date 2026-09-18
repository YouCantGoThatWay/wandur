using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Wandur.Desktop.ViewModels;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.Views;

public sealed class ScriptsWindow : Window
{
    public ScriptEditorViewModel Model { get; }

    public ScriptsWindow(ScriptEditorViewModel model)
    {
        Model = model;
        DataContext = model;
        Width = 880; Height = 780; MinWidth = 560; MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.Bind(TitleProperty, new Binding(nameof(model.Title)));
        Closed += (_, _) => model.Dispose();
        KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.S, OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control),
            Command = model.SaveCommand
        });

        Button Action(string label, string name, System.Windows.Input.ICommand command, bool primary = false)
        {
            var button = new Button { Name = name, [!ContentControl.ContentProperty] = LocalizedText.Binding(label), Command = command };
            button.Classes.Add("app-button");
            if (primary) button.Classes.Add("primary");
            return button;
        }
        var status = Ui.Text("", 13, "muted"); status.Name = "ScriptStatus";
        status.VerticalAlignment = VerticalAlignment.Center;
        status.Bind(TextBlock.TextProperty, new Binding(nameof(model.Status)));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children =
        {
            Action(nameof(L.ScriptRun), "RunScript", model.RunCommand, true),
            Action(nameof(L.ScriptStop), "StopScript", model.StopCommand),
            Action(nameof(L.ScriptSave), "SaveScript", model.SaveCommand), status
        } };
        var error = Ui.Text("", 13); error.Name = "ScriptError";
        error.Bind(TextBlock.TextProperty, new Binding(nameof(model.Error)));
        var errorPane = new ScrollViewer
        {
            Content = error, MaxHeight = 80,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        errorPane.Bind(IsVisibleProperty, new Binding(nameof(model.HasError)));
        var header = new StackPanel { Spacing = 9, Margin = new Thickness(0, 0, 0, 12), Children =
        {
            actions, Ui.TextKey(nameof(L.ScriptTrustHint), 12, "muted"), Ui.TextKey(nameof(L.ScriptPrivatePause), 12, "muted"), errorPane
        } };

        var mono = new FontFamily("Menlo, Consolas, DejaVu Sans Mono");
        var source = new ScriptCodeEditor
        {
            Name = "ScriptSource",
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch
        };
        source.Bind(ScriptCodeEditor.SourceTextProperty, new Binding(nameof(model.Source)) { Mode = BindingMode.TwoWay });
        var sourceLabel = Ui.TextKey(nameof(L.ScriptSourceLabel), 12, "muted"); sourceLabel.Margin = new Thickness(0, 0, 0, 6);
        var editsHint = Ui.TextKey(nameof(L.ScriptEditsHint), 12, "muted"); editsHint.Margin = new Thickness(0, 6, 0, 0);
        Grid.SetRow(source, 1); Grid.SetRow(editsHint, 2);
        var editor = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Children = { sourceLabel, source, editsHint } };

        var output = new TextBox
        {
            Name = "ScriptOutput", IsReadOnly = true, AcceptsReturn = true,
            FontFamily = mono, FontSize = 12, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(10)
        };
        output.Bind(TextBox.TextProperty, new Binding(nameof(model.Log)));
        ScrollViewer.SetVerticalScrollBarVisibility(output, ScrollBarVisibility.Auto);
        var outputLabel = Ui.TextKey(nameof(L.ScriptOutputLabel), 12, "muted"); outputLabel.Margin = new Thickness(0, 0, 0, 6);
        Grid.SetRow(output, 1);
        var outputPanel = new Grid
        {
            Height = 120, Margin = new Thickness(0, 12, 0, 10), RowDefinitions = new RowDefinitions("Auto,*"),
            Children = { outputLabel, output }
        };
        var examples = new SelectableTextBlock
        {
            FontFamily = mono, FontSize = 12, TextWrapping = TextWrapping.NoWrap,
            Text = L.ScriptApiExamples
        };
        var examplesScroll = new ScrollViewer
        {
            Content = examples, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var helpContent = new StackPanel { Spacing = 10, Children = { Ui.TextKey(nameof(L.ScriptApiHelp), 12, "muted"), examplesScroll } };
        var help = new Expander
        {
            Name = "ScriptHelp", [!Expander.HeaderProperty] = LocalizedText.Binding(nameof(L.ScriptHelpTitle)), HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = new ScrollViewer { Content = helpContent, MaxHeight = 120, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
        };
        Grid.SetRow(editor, 1); Grid.SetRow(outputPanel, 2); Grid.SetRow(help, 3);
        Content = new Grid { Margin = new Thickness(20), RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"), Children = { header, editor, outputPanel, help } };
    }
}
