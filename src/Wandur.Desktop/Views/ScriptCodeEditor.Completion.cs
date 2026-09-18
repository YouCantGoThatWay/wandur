using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Highlighting;

namespace Wandur.Desktop.Views;

public sealed partial class ScriptCodeEditor
{
    internal CompletionWindow? Completion { get; private set; }

    private void InitializeCompletion()
    {
        TextArea.TextEntered += (_, args) => { if (args.Text == ".") ShowCompletion(); };
        TextArea.TextEntering += (_, args) =>
        {
            if (Completion is { } completion && args.Text is { Length: > 0 } text && !char.IsLetterOrDigit(text[0]) && text[0] is not ('_' or '$'))
                completion.CompletionList.RequestInsertion(args);
        };
        TextArea.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Space && args.KeyModifiers == KeyModifiers.Control)
            { ShowCompletion(); args.Handled = true; }
        };
        DetachedFromVisualTree += (_, _) => Completion?.Hide();
    }

    internal void ShowCompletion()
    {
        Completion?.Hide();
        if (Document is null || CaretOffset == 0 && !TextArea.IsFocused) return;
        // Use the existing lexical grammar to avoid suggestions in comments and strings.
        if (CaretOffset > 0 && SyntaxHighlighting is { } definition)
        {
            using var highlighter = new DocumentHighlighter(Document, definition);
            var line = Document.GetLineByOffset(CaretOffset);
            var color = highlighter.HighlightLine(line.LineNumber).Sections
                .LastOrDefault(s => s.Offset <= CaretOffset - 1 && s.Offset + s.Length >= CaretOffset)?.Color.Name;
            if (color is "Comment" or "String" or "Regex" or "Escape") return;
        }
        var start = Math.Max(0, CaretOffset - 8192);
        var context = ScriptCompletionCatalog.Get(Document.GetText(start, CaretOffset - start));
        if (context.Suggestions.Count == 0) return;
        var popup = new CompletionWindow(TextArea)
        {
            StartOffset = CaretOffset - context.PrefixLength,
            EndOffset = CaretOffset,
            CloseWhenCaretAtBeginning = false,
            MinWidth = 260, MaxWidth = 420
        };
        foreach (var suggestion in context.Suggestions) popup.CompletionList.CompletionData.Add(new ApiCompletion(suggestion));
        popup.Closed += (_, _) => { if (ReferenceEquals(Completion, popup)) Completion = null; };
        Completion = popup;
        popup.Show();
        if (context.PrefixLength > 0) popup.CompletionList.SelectItem(Document.GetText(popup.StartOffset, context.PrefixLength));
    }

    private sealed class ApiCompletion(ScriptSuggestion suggestion) : ICompletionData
    {
        public IImage? Image => null;
        public string Text => suggestion.Name;
        public object Content => suggestion.Signature;
        public object Description => new TextBlock { Text = suggestion.Description, MaxWidth = 340, TextWrapping = TextWrapping.Wrap, Margin = new Avalonia.Thickness(8) };
        public double Priority => 0;
        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        {
            textArea.Document.Replace(completionSegment.Offset, completionSegment.Length, Text);
            textArea.Caret.Offset = completionSegment.Offset + Text.Length;
        }
    }
}
