using System.Xml;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;

namespace Wandur.Desktop.Views;

/// <summary>The same AvaloniaEdit engine used by scripts, with Markdown syntax and no JS completion.</summary>
public sealed class MarkdownCodeEditor : TextEditor
{
    public static readonly StyledProperty<string> SourceTextProperty =
        AvaloniaProperty.Register<MarkdownCodeEditor, string>(nameof(SourceText), "", defaultBindingMode: BindingMode.TwoWay);
    protected override Type StyleKeyOverride => typeof(TextEditor);
    public string SourceText { get => GetValue(SourceTextProperty); set => SetValue(SourceTextProperty, value); }
    public MarkdownCodeEditor()
    {
        ShowLineNumbers = true; WordWrap = true;
        LineNumbersMargin = new Thickness(0, 0, 10, 0);
        FontFamily = new FontFamily("Menlo, Consolas, DejaVu Sans Mono"); FontSize = 12; Padding = new Thickness(8);
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled; VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        TextChanged += (_, _) => SetCurrentValue(SourceTextProperty, Text);
        ActualThemeVariantChanged += (_, _) => ApplySyntax(); ApplySyntax();
        this.Bind(BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("EditorBackgroundBrush"));
        this.Bind(ForegroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("EditorTextBrush"));
        this.Bind(LineNumbersForegroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("EditorLineNumbersBrush"));
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceTextProperty && SourceText != Text) Text = SourceText ?? "";
    }
    private void ApplySyntax()
    {
        var light = ActualThemeVariant == ThemeVariant.Light;
        TextArea.SelectionBrush = Brush.Parse(light ? "#B6D7FF" : "#264F78");
        string Color(string dark, string paper) => light ? paper : dark;
        var grammar = $$"""
            <SyntaxDefinition name="Markdown" extensions=".md" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
              <Color name="Heading" foreground="{{Color("#79C0FF", "#0550AE")}}" fontWeight="bold" />
              <Color name="Marker" foreground="{{Color("#D2A8FF", "#8250DF")}}" />
              <Color name="Code" foreground="{{Color("#A5D6FF", "#0A3069")}}" />
              <Color name="Strong" fontWeight="bold" />
              <Color name="Emphasis" fontStyle="italic" />
              <Color name="Link" foreground="{{Color("#7EE787", "#116329")}}" />
              <RuleSet>
                <Span color="Code" begin="^\s*```" end="^\s*```\s*$" multiline="true" />
                <Span color="Code" begin="^\s*~~~" end="^\s*~~~\s*$" multiline="true" />
                <Rule color="Heading">^\s{0,3}\#{1,6}\s+.*$</Rule>
                <Rule color="Code">`[^`\r\n]+`</Rule>
                <Rule color="Strong">\*\*[^*\r\n]+\*\*|__[^_\r\n]+__</Rule>
                <Rule color="Emphasis">\*[^*\r\n]+\*|_[^_\r\n]+_</Rule>
                <Rule color="Link">!?\[[^\]\r\n]+\]\([^\)\r\n]+\)</Rule>
                <Rule color="Marker">^\s*(?:[-+*]|\d+[.)]|&gt;)\s+</Rule>
              </RuleSet>
            </SyntaxDefinition>
            """;
        using var reader = XmlReader.Create(new StringReader(grammar));
        SyntaxHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }
}
