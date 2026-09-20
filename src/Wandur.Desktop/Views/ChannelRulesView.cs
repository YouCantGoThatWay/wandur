using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Wandur.Desktop.Terminal;
using Wandur.Desktop.ViewModels;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.Views;

/// <summary>
/// The Channels section of the world editor: the rules this world was taught, one card each, with the
/// channel, the pattern and the reply command editable, an enabled toggle and a delete button. Plain on
/// purpose: the teaching dialog is where a rule is built, this is where a broken one is fixed or dropped.
/// </summary>
public sealed class ChannelRulesView : UserControl
{
    public ChannelRulesView(ProfileEditorViewModel model)
    {
        DataContext = model;
        Name = "ChannelRulesView";
        var monospace = new FontFamily(TerminalPalette.Monospace);
        var empty = Ui.TextKey(nameof(L.ProfileChannelsEmpty), 12, "muted");
        empty.Name = "ChannelRulesEmpty"; empty.TextWrapping = TextWrapping.Wrap;
        empty.Bind(IsVisibleProperty, new Binding(nameof(model.HasChannelRules)) { Converter = Avalonia.Data.Converters.BoolConverters.Not });
        var help = Ui.TextKey(nameof(L.ProfileChannelsHelp), 12, "muted");
        help.TextWrapping = TextWrapping.Wrap;
        var list = new ItemsControl { Name = "ChannelRules", ItemTemplate = new FuncDataTemplate<ChannelRuleEditor>((editor, _) => editor is null ? null : Card(editor, model, monospace)) };
        list.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(model.ChannelRules)));
        list.ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Spacing = 10 });
        var title = Ui.TextKey(nameof(L.ProfileChannelsSection), 20);
        title.FontWeight = FontWeight.SemiBold; title.Margin = new Thickness(0, 0, 0, 6);
        Content = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content =
            new StackPanel { Margin = new Thickness(28, 24), Spacing = 14, MaxWidth = 700, HorizontalAlignment = HorizontalAlignment.Stretch, Children = { title, help, empty, list } } };
    }

    private static Control Card(ChannelRuleEditor editor, ProfileEditorViewModel model, FontFamily monospace)
    {
        TextBox Field(string name, string property, bool mono)
        {
            var box = new TextBox { Name = name, FontSize = 12, DataContext = editor };
            if (mono) box.FontFamily = monospace;
            box.Bind(TextBox.TextProperty, new Binding(property) { Mode = BindingMode.TwoWay });
            return box;
        }
        var enabled = new CheckBox { Name = "ChannelRuleEnabled", DataContext = editor, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        enabled.Bind(ContentControl.ContentProperty, LocalizedText.Binding(nameof(L.ProfileChannelEnabled)));
        enabled.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(editor.Enabled)) { Mode = BindingMode.TwoWay });
        var kind = Ui.Text(editor.Kind, 11, "muted");
        kind.Name = "ChannelRuleKind"; kind.IsVisible = editor.HasKind; kind.VerticalAlignment = VerticalAlignment.Center;
        var delete = new Button { Name = "ChannelRuleDelete", Command = model.RemoveRuleCommand, CommandParameter = editor, FontSize = 11, Padding = new Thickness(10, 4) };
        delete.Classes.Add("app-button"); delete.Classes.Add("quiet");
        delete.Bind(ContentControl.ContentProperty, LocalizedText.Binding(nameof(L.ProfileChannelDelete)));
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), ColumnSpacing = 12 };
        Grid.SetColumn(kind, 1); Grid.SetColumn(delete, 3);
        top.Children.Add(enabled); top.Children.Add(kind); top.Children.Add(delete);
        var names = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 10 };
        var channel = Ui.FieldKey(nameof(L.ChannelRuleChannel), Field("ChannelRuleChannel", nameof(editor.Channel), false));
        var reply = Ui.FieldKey(nameof(L.ChannelRuleReply), Field("ChannelRuleReply", nameof(editor.ReplyCommand), false));
        Grid.SetColumn(reply, 1);
        names.Children.Add(channel); names.Children.Add(reply);
        var pattern = Field("ChannelRulePattern", nameof(editor.Pattern), true);
        pattern.TextWrapping = TextWrapping.Wrap;
        return Ui.Card(new StackPanel { Spacing = 8, Children = { top, names, Ui.FieldKey(nameof(L.ChannelRulePattern), pattern) } }, 12);
    }
}
