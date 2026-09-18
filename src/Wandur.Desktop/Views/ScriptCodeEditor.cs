using System.Xml;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using AvaloniaEdit.Indentation.CSharp;

namespace Wandur.Desktop.Views;

/// <summary>Presentation adapter: binds the document without replacing it on status/log updates.</summary>
public sealed partial class ScriptCodeEditor : TextEditor
{
    public static readonly StyledProperty<string> SourceTextProperty =
        AvaloniaProperty.Register<ScriptCodeEditor, string>(nameof(SourceText), "", defaultBindingMode: BindingMode.TwoWay);
    protected override Type StyleKeyOverride => typeof(TextEditor);

    public string SourceText { get => GetValue(SourceTextProperty); set => SetValue(SourceTextProperty, value); }

    public ScriptCodeEditor()
    {
        ShowLineNumbers = true;
        LineNumbersMargin = new Thickness(0, 0, 12, 0);
        FontFamily = new FontFamily("Menlo, Consolas, DejaVu Sans Mono");
        FontSize = 13;
        Padding = new Thickness(10);
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Options.IndentationSize = 4;
        Options.ConvertTabsToSpaces = true;
        TextArea.IndentationStrategy = new CSharpIndentationStrategy(Options);
        TextChanged += (_, _) => SetCurrentValue(SourceTextProperty, Text);
        ActualThemeVariantChanged += (_, _) => ApplySyntaxTheme();
        ApplySyntaxTheme();
        this.Bind(BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("EditorBackgroundBrush"));
        this.Bind(ForegroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("EditorTextBrush"));
        this.Bind(LineNumbersForegroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("EditorLineNumbersBrush"));
        InitializeCompletion();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceTextProperty && SourceText != Text)
        {
            Completion?.Hide();
            Text = SourceText;
        }
    }

    private void ApplySyntaxTheme()
    {
        var light = ActualThemeVariant == ThemeVariant.Light;
        TextArea.SelectionBrush = Brush.Parse(light ? "#B6D7FF" : "#264F78");
        SyntaxHighlighting = JavaScriptSyntax.Create(light);
    }
}

internal static class JavaScriptSyntax
{
    public static IHighlightingDefinition Create(bool light)
    {
        string Color(string dark, string paper) => light ? paper : dark;
        // This is a lexical grammar, not a JavaScript parser. Spans preserve multiline
        // comments and templates; interpolation re-enters expression highlighting.
        var grammar = $$"""
            <SyntaxDefinition name="JavaScript" extensions=".js" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
              <Color name="Keyword" foreground="{{Color("#D2A8FF", "#8250DF")}}" />
              <Color name="Comment" foreground="{{Color("#8B949E", "#57606A")}}" />
              <Color name="String" foreground="{{Color("#A5D6FF", "#0A3069")}}" />
              <Color name="Number" foreground="{{Color("#79C0FF", "#0550AE")}}" />
              <Color name="Regex" foreground="{{Color("#7EE787", "#116329")}}" />
              <Color name="Api" foreground="{{Color("#FFA657", "#953800")}}" />
              <Color name="Escape" foreground="{{Color("#FFA657", "#953800")}}" />
              <RuleSet>
                <Import ruleSet="Expression" />
              </RuleSet>
              <RuleSet name="Expression">
                <Span color="Comment" begin="//" />
                <Span color="Comment" begin="/\*" end="\*/" multiline="true" />
                <Span color="String" begin="&quot;" end="&quot;">
                  <RuleSet><Span color="Escape" begin="\\" end="." /></RuleSet>
                </Span>
                <Span color="String" begin="'" end="'">
                  <RuleSet><Span color="Escape" begin="\\" end="." /></RuleSet>
                </Span>
                <Span color="String" begin="`" end="`" multiline="true">
                  <RuleSet>
                    <Span color="Escape" begin="\\" end="." />
                    <Span begin="\$\{" end="\}" multiline="true" ruleSet="Expression" />
                  </RuleSet>
                </Span>
                <Span begin="\{" end="\}" multiline="true" ruleSet="Expression" />
                <Rule color="Regex">(?&lt;![\w\])])/(?![/*])(?:\\.|\[(?:\\.|[^\]\\\r\n])*\]|[^/\\[\r\n])+/[dgimsuvy]*</Rule>
                <Keywords color="Keyword">
                  <Word>async</Word><Word>await</Word><Word>break</Word><Word>case</Word><Word>catch</Word><Word>class</Word>
                  <Word>const</Word><Word>continue</Word><Word>debugger</Word><Word>default</Word><Word>delete</Word><Word>do</Word>
                  <Word>else</Word><Word>export</Word><Word>extends</Word><Word>false</Word><Word>finally</Word><Word>for</Word>
                  <Word>function</Word><Word>if</Word><Word>import</Word><Word>in</Word><Word>instanceof</Word><Word>let</Word>
                  <Word>new</Word><Word>null</Word><Word>of</Word><Word>return</Word><Word>static</Word><Word>super</Word>
                  <Word>switch</Word><Word>this</Word><Word>throw</Word><Word>true</Word><Word>try</Word><Word>typeof</Word>
                  <Word>undefined</Word><Word>var</Word><Word>void</Word><Word>while</Word><Word>with</Word><Word>yield</Word>
                </Keywords>
                <Rule color="Number">\b(?:0[xX][\da-fA-F_]+|0[bB][01_]+|0[oO][0-7_]+|\d[\d_]*(?:\.[\d_]+)?(?:[eE][+-]?[\d_]+)?n?)\b</Rule>
                <Rule color="Api">\b(?:mud|Events)\b|(?&lt;=\.)(?:send|echo|alias|trigger|every|on|Line|Gmcp)\b</Rule>
              </RuleSet>
            </SyntaxDefinition>
            """;
        using var reader = XmlReader.Create(new StringReader(grammar));
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }
}
