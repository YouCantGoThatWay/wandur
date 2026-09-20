using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Wandur.Desktop.Terminal;
using Wandur.Desktop.ViewModels;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.Views;

/// <summary>
/// The small window behind "Mark as channel": the example line, the guessed pieces and the rule they
/// compose, a channel picker, the reply command, and a live preview over the recent transcript.
/// </summary>
public sealed class MarkChannelDialog : Window
{
    public MarkChannelViewModel Model { get; }

    public MarkChannelDialog(MarkChannelViewModel model)
    {
        Model = model; DataContext = model;
        Name = "MarkChannelDialog";
        Title = L.TeachChannelTitle;
        Width = 680; Height = 640; MinWidth = 520; MinHeight = 480;
        CanResize = true; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var monospace = new FontFamily(TerminalPalette.Monospace);

        var example = new TextBlock { Name = "TeachExample", Text = model.Example, FontFamily = monospace, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var exampleCard = Ui.Card(example, 10);

        TextBox Field(string name, string property, bool mono = true)
        {
            var box = new TextBox { Name = name, FontSize = 12 };
            if (mono) box.FontFamily = monospace;
            box.Bind(TextBox.TextProperty, new Binding(property) { Mode = BindingMode.TwoWay });
            return box;
        }
        var pieces = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), ColumnSpacing = 10 };
        var head = Ui.FieldKey(nameof(L.TeachChannelHead), Field("TeachHead", nameof(model.HeadPattern)));
        var speaker = Ui.FieldKey(nameof(L.TeachChannelSpeaker), Field("TeachSpeaker", nameof(model.SpeakerPattern)));
        var separator = Ui.FieldKey(nameof(L.TeachChannelSeparator), Field("TeachSeparator", nameof(model.SeparatorPattern)));
        Grid.SetColumn(speaker, 1); Grid.SetColumn(separator, 2);
        pieces.Children.Add(head); pieces.Children.Add(speaker); pieces.Children.Add(separator);
        var read = Ui.Text(string.Join("  ·  ", new[] { model.Head, model.Speaker, model.Separator }.Where(s => s.Length > 0).Select(s => "“" + s + "”")), 11, "muted");
        read.Name = "TeachPieces"; read.FontFamily = monospace; read.TextWrapping = TextWrapping.Wrap;
        var pattern = Field("TeachPattern", nameof(model.Pattern));
        pattern.TextWrapping = TextWrapping.Wrap; pattern.AcceptsReturn = false;

        var channels = new ComboBox { Name = "TeachChannel", HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12,
            ItemTemplate = new FuncDataTemplate<string>((name, _) => Ui.Text(name ?? "", 12)) };
        channels.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(model.ChannelChoices)));
        channels.Bind(SelectingItemsControl.SelectedIndexProperty, new Binding(nameof(model.ChannelIndex)) { Mode = BindingMode.TwoWay });
        var newName = Field("TeachChannelName", nameof(model.NewChannelName), mono: false);
        newName.Bind(TextBox.PlaceholderTextProperty, LocalizedText.Binding(nameof(L.TeachChannelNewChannelName)));
        newName.Bind(IsVisibleProperty, new Binding(nameof(model.IsNewChannel)));
        var channelColumn = new StackPanel { Spacing = 6, Children = { channels, newName } };
        var reply = Field("TeachReply", nameof(model.ReplyCommand), mono: false);
        var isPrivate = new CheckBox { Name = "TeachPrivate", FontSize = 12 };
        isPrivate.Bind(ContentControl.ContentProperty, LocalizedText.Binding(nameof(L.TeachChannelPrivate)));
        isPrivate.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(model.IsPrivate)) { Mode = BindingMode.TwoWay });
        var replyColumn = new StackPanel { Spacing = 6, Children = { reply, isPrivate } };
        var choices = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 10 };
        var channelField = Ui.FieldKey(nameof(L.TeachChannelChannel), channelColumn);
        var replyField = Ui.FieldKey(nameof(L.TeachChannelReply), replyColumn);
        Grid.SetColumn(replyField, 1);
        choices.Children.Add(channelField); choices.Children.Add(replyField);

        var previewTitle = Ui.Text(model.PreviewTitle, 10, "eyebrow");
        var summary = new TextBlock { Name = "TeachPreviewSummary", FontSize = 12, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        summary.Bind(TextBlock.TextProperty, new Binding(nameof(model.PreviewSummary)));
        var matches = new ItemsControl { Name = "TeachPreviewMatches", ItemTemplate = new FuncDataTemplate<ChannelPreviewMatch>((match, _) =>
        {
            var row = new TextBlock { FontFamily = monospace, FontSize = 12, TextWrapping = TextWrapping.Wrap, Text = match?.Label ?? "" };
            return row;
        }) };
        matches.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(model.Matches)));
        var preview = Ui.Card(new StackPanel { Spacing = 6, Children = { previewTitle, summary, matches } }, 12);
        preview.Name = "TeachPreview";

        var error = new TextBlock { Name = "TeachError", Foreground = Brushes.IndianRed, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        error.Bind(TextBlock.TextProperty, new Binding(nameof(model.Error)));
        error.Bind(IsVisibleProperty, new Binding(nameof(model.HasError)));
        var excludeHelp = Ui.Text(model.ExcludeHelp, 11, "muted");
        excludeHelp.TextWrapping = TextWrapping.Wrap; excludeHelp.IsVisible = model.CanExclude;

        var exclude = new Button { Name = "TeachExclude", Command = model.ExcludeCommand, IsVisible = model.CanExclude };
        exclude.Classes.Add("app-button");
        exclude.Bind(ContentControl.ContentProperty, LocalizedText.Binding(nameof(L.TeachChannelNotAChannel)));
        var cancel = new Button { Name = "TeachCancel", IsCancel = true };
        cancel.Classes.Add("app-button");
        cancel.Bind(ContentControl.ContentProperty, LocalizedText.Binding(nameof(L.Cancel)));
        cancel.Click += (_, _) => Close();
        var save = new Button { Name = "TeachSave", Command = model.SaveCommand, IsDefault = true };
        save.Classes.Add("app-button"); save.Classes.Add("primary");
        save.Bind(ContentControl.ContentProperty, LocalizedText.Binding(nameof(L.TeachChannelSave)));
        var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 8 };
        Grid.SetColumn(cancel, 2); Grid.SetColumn(save, 3);
        buttons.Children.Add(exclude); buttons.Children.Add(cancel); buttons.Children.Add(save);

        var body = new StackPanel { Margin = new Thickness(24, 20), Spacing = 14, Children =
        {
            Ui.TextKey(nameof(L.TeachChannelExample), 10, "eyebrow"), exampleCard,
            Ui.TextKey(nameof(L.TeachChannelHelp), 12, "muted"),
            pieces, read,
            Ui.FieldKey(nameof(L.TeachChannelPattern), pattern),
            choices,
            preview
        } };
        foreach (var block in body.Children.OfType<TextBlock>()) block.TextWrapping = TextWrapping.Wrap;
        var footer = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(24, 12),
            Child = new StackPanel { Spacing = 8, Children = { error, excludeHelp, buttons } } };
        footer.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("LineBrush"));
        Grid.SetRow(footer, 1);
        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Children =
        {
            new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, footer
        } };
        Content = root;
        KeyDown += (_, args) => { if (args.Key == Key.Escape) { args.Handled = true; Close(); } };
        model.CloseRequested += Close;
        Closed += (_, _) => model.CloseRequested -= Close;
    }
}
