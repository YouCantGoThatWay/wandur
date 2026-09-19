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

/// <summary>
/// Read-only body view for a diagnostics entry: GMCP/MSDP payloads are already pretty-printed as JSON by
/// <c>ProtocolDiagnosticFormatter</c>, so this reuses the AvaloniaEdit engine the script and markdown editors
/// use, with the JSON syntax highlighter configured. Malformed or non-JSON bodies still render as plain text.
/// </summary>
public sealed class DiagnosticsBodyEditor : TextEditor
{
    public static readonly StyledProperty<string> SourceTextProperty =
        AvaloniaProperty.Register<DiagnosticsBodyEditor, string>(nameof(SourceText), "", defaultBindingMode: BindingMode.OneWay);
    protected override Type StyleKeyOverride => typeof(TextEditor);
    public string SourceText { get => GetValue(SourceTextProperty); set => SetValue(SourceTextProperty, value); }

    public DiagnosticsBodyEditor()
    {
        IsReadOnly = true;
        ShowLineNumbers = false;
        WordWrap = true;
        FontFamily = new FontFamily("Menlo, Consolas, DejaVu Sans Mono");
        FontSize = 12;
        Padding = new Thickness(14);
        BorderThickness = new Thickness(0);
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        ActualThemeVariantChanged += (_, _) => ApplySyntax();
        ApplySyntax();
        this.Bind(BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("EditorBackgroundBrush"));
        this.Bind(ForegroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("EditorTextBrush"));
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
        SyntaxHighlighting = JsonSyntax.Create(light);
    }
}

/// <summary>
/// AvaloniaEdit ships a built-in "Json" definition (<see cref="HighlightingManager.GetDefinition"/>); this
/// recolors its named spans to the same dark/light palette the script editor's JavaScript grammar uses, since
/// the shipped definition hardcodes punctuation as black, which is unreadable on a dark editor background. If
/// the built-in definition is ever unavailable a small equivalent grammar is loaded in its place.
/// </summary>
internal static class JsonSyntax
{
    public static IHighlightingDefinition Create(bool light)
    {
        string Color(string dark, string paper) => light ? paper : dark;
        var definition = HighlightingManager.Instance.GetDefinition("Json") ?? Build();
        Recolor(definition, "FieldName", Color("#D2A8FF", "#8250DF"));
        Recolor(definition, "String", Color("#A5D6FF", "#0A3069"));
        Recolor(definition, "Number", Color("#79C0FF", "#0550AE"));
        Recolor(definition, "Bool", Color("#FFA657", "#953800"));
        Recolor(definition, "Null", Color("#FFA657", "#953800"));
        Recolor(definition, "Punctuation", Color("#C9D1D9", "#1F2328"));
        return definition;
    }

    private static void Recolor(IHighlightingDefinition definition, string name, string hex)
    {
        if (definition.GetNamedColor(name) is { } color) color.Foreground = new SimpleHighlightingBrush(Avalonia.Media.Color.Parse(hex));
    }

    private static IHighlightingDefinition Build()
    {
        // Mirrors the shape of AvaloniaEdit's built-in Json.xshd. Colors are placeholders: Create() above
        // always recolors every named span immediately after loading a definition, built-in or not.
        const string grammar = """
            <SyntaxDefinition name="Json" extensions=".json" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
              <Color name="Bool" foreground="Black" />
              <Color name="Number" foreground="Black" />
              <Color name="String" foreground="Black" />
              <Color name="Null" foreground="Black" />
              <Color name="FieldName" foreground="Black" />
              <Color name="Punctuation" foreground="Black" />
              <RuleSet name="String">
                <Span begin="\\" end="." />
              </RuleSet>
              <RuleSet name="Object">
                <Span color="FieldName" ruleSet="String"><Begin>"</Begin><End>"</End></Span>
                <Span color="Punctuation" ruleSet="Expression"><Begin>:</Begin></Span>
                <Span color="Punctuation"><Begin>,</Begin></Span>
              </RuleSet>
              <RuleSet name="Array">
                <Import ruleSet="Expression" />
                <Span color="Punctuation"><Begin>,</Begin></Span>
              </RuleSet>
              <RuleSet name="Expression">
                <Keywords color="Bool"><Word>true</Word><Word>false</Word></Keywords>
                <Keywords color="Null"><Word>null</Word></Keywords>
                <Span color="String" ruleSet="String"><Begin>"</Begin><End>"</End></Span>
                <Span color="Punctuation" ruleSet="Object" multiline="true"><Begin>\{</Begin><End>\}</End></Span>
                <Span color="Punctuation" ruleSet="Array" multiline="true"><Begin>\[</Begin><End>\]</End></Span>
                <Rule color="Number">\b0[xX][0-9a-fA-F]+|(\b\d+(\.[0-9]+)?|\.[0-9]+)([eE][+-]?[0-9]+)?</Rule>
              </RuleSet>
              <RuleSet>
                <Import ruleSet="Expression" />
              </RuleSet>
            </SyntaxDefinition>
            """;
        using var reader = XmlReader.Create(new StringReader(grammar));
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }
}
