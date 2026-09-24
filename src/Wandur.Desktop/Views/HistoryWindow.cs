using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Wandur.Core.History;
using Wandur.Desktop.ViewModels;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.Views;

public sealed class HistoryWindow : Window
{
    public HistoryViewModel Model { get; }

    public HistoryWindow(IHistoryStore store)
    {
        Model = new HistoryViewModel(store);
        DataContext = Model;
        Width = 1100; Height = 800; MinWidth = 860; MinHeight = 660;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.Bind(TitleProperty, LocalizedText.Binding(nameof(L.SessionHistory)));
        this.Bind(BackgroundProperty, new DynamicResourceExtension("PanelBrush"));
        Opened += async (_, _) => await Model.RefreshAsync();
        Closed += (_, _) => Model.Dispose();

        var heading = Ui.TextKey(nameof(L.SessionHistory), 24);
        heading.FontWeight = FontWeight.SemiBold;
        var local = Ui.TextKey(nameof(L.HistoryLocalNotice), 12, "muted");
        local.Name = "HistoryLocalNotice";
        var privacy = Ui.TextKey(nameof(L.HistoryPrivacyHint), 12, "muted");
        var query = Input("HistoryQuery", nameof(Model.Query), nameof(L.HistoryQuery));
        query.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            await Model.RefreshAsync();
        };
        var search = Action("HistorySearch", nameof(L.HistorySearch), Model.RefreshAsync);
        search.Classes.Add("primary");
        var queryRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10, Children = { query, search } };
        Grid.SetColumn(search, 1);
        var filters = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,170,170"), ColumnSpacing = 10 };
        filters.Children.Add(Ui.FieldKey(nameof(L.HistoryWorld), Input("HistoryWorld", nameof(Model.World), nameof(L.HistoryWorld))));
        var character = Ui.FieldKey(nameof(L.HistoryCharacter), Input("HistoryCharacter", nameof(Model.Character), nameof(L.HistoryCharacter)));
        var from = DateField("HistoryFrom", nameof(L.HistoryFrom), value => Model.From = value);
        var until = DateField("HistoryUntil", nameof(L.HistoryUntil), value => Model.Until = value);
        filters.Children.Add(character); Grid.SetColumn(character, 1);
        filters.Children.Add(from); Grid.SetColumn(from, 2);
        filters.Children.Add(until); Grid.SetColumn(until, 3);

        var header = new StackPanel { Spacing = 10, Children = { heading, local, privacy, queryRow, filters } };
        header.Bind(IsEnabledProperty, new Binding("!" + nameof(Model.IsDeleting)));
        var sessions = new ListBox
        {
            Name = "HistorySessions", ItemsSource = Model.Sessions,
            ItemTemplate = new FuncDataTemplate<HistorySession>((session, _) => session is null ? null : SessionRow(session))
        };
        var results = new ListBox
        {
            Name = "HistoryResults", ItemsSource = Model.Results,
            ItemTemplate = new FuncDataTemplate<HistoryHit>((hit, _) =>
            {
                if (hit is null) return null;
                var snippet = Ui.Text(hit.Entry.Text.Replace('\n', ' ').Replace('\r', ' '), 13);
                // The full, unmodified entry remains available in the read-only transcript.
                if (snippet.Text!.Length > 180) snippet.Text = snippet.Text[..180] + "...";
                snippet.TextTrimming = TextTrimming.CharacterEllipsis; snippet.MaxLines = 2;
                var row = SessionRow(hit.Session);
                row.Children.Add(Ui.Text($"{hit.Entry.At.ToLocalTime():g}", 11, "muted"));
                row.Children.Add(snippet);
                return row;
            })
        };
        results.SelectionChanged += async (_, _) =>
        {
            if (results.SelectedItem is not HistoryHit hit) return;
            sessions.SelectedItem = null;
            await Model.OpenHitAsync(hit);
        };
        sessions.SelectionChanged += async (_, _) =>
        {
            if (sessions.SelectedItem is not HistorySession session) return;
            results.SelectedItem = null;
            await Model.OpenSessionAsync(session);
        };
        var tabs = new TabControl { Name = "HistoryBrowseTabs", Items =
        {
            new TabItem { Header = Ui.TextKey(nameof(L.HistorySessions)), Content = Page(sessions, nameof(L.HistoryEmptySessions), nameof(Model.SessionsEmpty),
                "HistorySessions", nameof(Model.SessionsLabel), Model.PreviousSessionsAsync, nameof(Model.HasPreviousSessions), Model.NextSessionsAsync, nameof(Model.HasNextSessions)) },
            new TabItem { Header = Ui.TextKey(nameof(L.HistoryResults)), Content = Page(results, nameof(L.HistoryEmptyResults), nameof(Model.ResultsEmpty),
                "HistoryResults", nameof(Model.ResultsLabel), Model.PreviousResultsAsync, nameof(Model.HasPreviousResults), Model.NextResultsAsync, nameof(Model.HasNextResults)) }
        } };
        Model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(Model.IsBusy) && !Model.IsBusy && !string.IsNullOrWhiteSpace(Model.Query)) tabs.SelectedIndex = 1;
        };
        tabs.Bind(IsEnabledProperty, new Binding("!" + nameof(Model.IsDeleting)));

        var contextLabel = Ui.Text("", 13);
        contextLabel.Name = "HistoryContextLabel";
        contextLabel.MaxLines = 2; contextLabel.TextTrimming = TextTrimming.CharacterEllipsis;
        contextLabel.Bind(TextBlock.TextProperty, new Binding(nameof(Model.ContextLabel)));
        contextLabel.Bind(ToolTip.TipProperty, new Binding(nameof(Model.ContextLabel)));
        var transcript = new TextBox
        {
            Name = "HistoryTranscript", IsReadOnly = true, AcceptsReturn = true,
            FontFamily = new FontFamily("Menlo, Consolas, DejaVu Sans Mono"), FontSize = 13,
            TextWrapping = TextWrapping.Wrap, Padding = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch
        };
        transcript.Bind(TextBox.TextProperty, new Binding(nameof(Model.TranscriptText)));
        Model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(Model.TranscriptSelectionEnd)) return;
            // Moving the caret clears selection, so apply both selection bounds after it.
            transcript.CaretIndex = Model.TranscriptSelectionEnd;
            transcript.SelectionStart = Model.TranscriptSelectionStart;
            transcript.SelectionEnd = Model.TranscriptSelectionEnd;
        };
        transcript.Bind(BackgroundProperty, new DynamicResourceExtension("EditorBackgroundBrush"));
        transcript.Bind(ForegroundProperty, new DynamicResourceExtension("EditorTextBrush"));
        transcript.Bind(AutomationProperties.NameProperty, LocalizedText.Binding(nameof(L.SessionHistory)));
        ScrollViewer.SetVerticalScrollBarVisibility(transcript, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(transcript, ScrollBarVisibility.Disabled);
        var previous = Action("HistoryPreviousContext", nameof(L.HistoryPrevious), Model.PreviousContextAsync, nameof(Model.HasPreviousContext));
        var next = Action("HistoryNextContext", nameof(L.HistoryNext), Model.NextContextAsync, nameof(Model.HasNextContext));
        var delete = Ui.ButtonKey(nameof(L.HistoryDelete), Model.RequestDelete);
        delete.Name = "HistoryDelete";
        delete.Bind(IsEnabledProperty, new Binding(nameof(Model.CanDelete)));
        var contextButtons = new WrapPanel { Orientation = Orientation.Horizontal, Children = { previous, next, delete } };
        foreach (var button in contextButtons.Children) button.Margin = new Thickness(0, 0, 8, 0);
        var loadingContext = Ui.TextKey(nameof(L.HistoryLoading), 12, "muted");
        loadingContext.Bind(IsVisibleProperty, new Binding(nameof(Model.IsContextBusy)));
        var contextHeader = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 8, 10), Children = { contextLabel, loadingContext } };
        var detail = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 10, Children = { contextHeader, transcript, contextButtons } };
        Grid.SetRow(transcript, 1); Grid.SetRow(contextButtons, 2);
        var splitter = new GridSplitter
        {
            ResizeDirection = GridResizeDirection.Columns, ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            Background = Brushes.Transparent
        };
        var panes = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,12,3*"), Children = { tabs, splitter, detail } };
        panes.ColumnDefinitions[0].MinWidth = 270; panes.ColumnDefinitions[2].MinWidth = 340;
        Grid.SetColumn(splitter, 1); Grid.SetColumn(detail, 2);

        var confirmText = Ui.Text("", 13);
        confirmText.Bind(TextBlock.TextProperty, new Binding(nameof(Model.DeletePrompt)));
        var confirm = Action("HistoryConfirmDelete", nameof(L.HistoryDelete), Model.DeleteAsync);
        var cancel = Ui.ButtonKey(nameof(L.Cancel), Model.CancelDelete); cancel.Name = "HistoryCancelDelete";
        var confirmButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { confirm, cancel } };
        confirmButtons.Bind(IsEnabledProperty, new Binding("!" + nameof(Model.IsDeleting)));
        var confirmation = Ui.Card(new StackPanel { Spacing = 10, Children = { confirmText, confirmButtons } }, 12);
        confirmation.Name = "HistoryDeleteConfirm";
        confirmation.Bind(IsVisibleProperty, new Binding(nameof(Model.ConfirmDelete)));
        var busy = Ui.TextKey(nameof(L.HistoryLoading), 12, "muted");
        busy.Bind(IsVisibleProperty, new Binding(nameof(Model.IsBusy)));
        var error = Ui.Text("", 13); error.Name = "HistoryError";
        error.Bind(TextBlock.TextProperty, new Binding(nameof(Model.Error)));
        var status = new StackPanel { Spacing = 6, Children = { busy, error, confirmation } };
        var root = new Grid { Margin = new Thickness(20), RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 16, Children = { header, panes, status } };
        Grid.SetRow(panes, 1); Grid.SetRow(status, 2);
        Content = root;
    }

    private static StackPanel SessionRow(HistorySession session)
    {
        var name = Ui.Text(session.WorldName, 14);
        name.FontWeight = FontWeight.Medium; name.MaxLines = 1; name.TextTrimming = TextTrimming.CharacterEllipsis;
        var metadata = Ui.Text($"{session.CharacterName} · {session.StartedAt.ToLocalTime():g}", 12, "muted");
        metadata.MaxLines = 1; metadata.TextTrimming = TextTrimming.CharacterEllipsis;
        var row = new StackPanel { Spacing = 5, Margin = new Thickness(4, 5), Children = { name, metadata } };
        ToolTip.SetTip(row, $"{HistoryViewModel.SessionLabel(session)}\n{session.WorldKey}");
        return row;
    }

    private TextBox Input(string name, string property, string label)
    {
        var input = new TextBox { Name = name, MinHeight = 36, HorizontalAlignment = HorizontalAlignment.Stretch };
        input.Bind(TextBox.TextProperty, new Binding(property) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        input.Bind(TextBox.PlaceholderTextProperty, LocalizedText.Binding(label));
        input.Bind(AutomationProperties.NameProperty, LocalizedText.Binding(label));
        return input;
    }
    private static Control DateField(string name, string label, Action<DateTimeOffset?> changed)
    {
        var picker = new CalendarDatePicker { Name = name, MinHeight = 36, HorizontalAlignment = HorizontalAlignment.Stretch };
        picker.Bind(AutomationProperties.NameProperty, LocalizedText.Binding(label));
        picker.SelectedDateChanged += (_, _) => changed(picker.SelectedDate is { } date ? new DateTimeOffset(date.Date) : null);
        return Ui.FieldKey(label, picker);
    }
    private Button Action(string name, string label, Func<Task> action, string? enabled = null)
    {
        var button = new Button { Name = name };
        button.Classes.Add("app-button");
        button.Bind(ContentControl.ContentProperty, LocalizedText.Binding(label));
        button.Bind(AutomationProperties.NameProperty, LocalizedText.Binding(label));
        button.Click += async (_, _) => await action();
        if (enabled is not null) button.Bind(IsEnabledProperty, new Binding(enabled));
        return button;
    }
    private Control Page(ListBox list, string emptyLabel, string emptyProperty, string name, string label,
        Func<Task> previousAction, string previousEnabled, Func<Task> nextAction, string nextEnabled)
    {
        var empty = Ui.TextKey(emptyLabel, 13, "muted");
        empty.Margin = new Thickness(16); empty.VerticalAlignment = VerticalAlignment.Center;
        empty.Bind(IsVisibleProperty, new Binding(emptyProperty));
        var body = new Grid { Children = { list, empty } };
        var count = Ui.Text("", 12, "muted");
        count.Bind(TextBlock.TextProperty, new Binding(label));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
        {
            Action(name + "Previous", nameof(L.HistoryPrevious), previousAction, previousEnabled),
            Action(name + "Next", nameof(L.HistoryNext), nextAction, nextEnabled)
        } };
        var footer = new StackPanel { Spacing = 6, Margin = new Thickness(0, 8, 0, 0), Children = { count, buttons } };
        var page = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Children = { body, footer } };
        Grid.SetRow(footer, 1);
        return page;
    }
}
