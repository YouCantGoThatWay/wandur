using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Wandur.Core.Terminal;

namespace Wandur.Desktop.Terminal;

/// <summary>
/// Puts a string that may carry MUD color codes into a text block. Plain text stays a plain <see cref="TextBlock.Text"/>,
/// so an ordinary label renders exactly as before; coded text becomes one <see cref="Run"/> per styled span, colored with
/// the themed palette brushes the transcript uses, read from the control that binds them (see <see cref="TerminalPalette.Bind"/>).
/// </summary>
internal static class MudText
{
    /// <summary>Renders <paramref name="text"/> into <paramref name="block"/>, resolving palette entries through <paramref name="palette"/>.</summary>
    public static void Apply(TextBlock block, string text, Control palette)
    {
        if (!MudColorCodes.HasCodes(text))
        {
            if (block.Inlines is { Count: > 0 } existing) existing.Clear();
            block.Text = text;
            return;
        }
        var inlines = block.Inlines ??= [];
        inlines.Clear();
        block.Text = null;
        foreach (var run in MudColorCodes.Parse(text)) inlines.Add(Styled(run, palette));
    }

    /// <summary>Button and check box content: the string itself when plain, a styled block otherwise.</summary>
    public static object Content(string text, Control palette)
    {
        if (!MudColorCodes.HasCodes(text)) return text;
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap };
        Apply(block, text, palette);
        return block;
    }

    private static Run Styled(TextRun run, Control palette)
    {
        var span = new Run(run.Text);
        if (TerminalPalette.Resolve(palette, run.Style.ForegroundIndex, run.Style.Foreground) is { } foreground) span.Foreground = foreground;
        if (TerminalPalette.Resolve(palette, run.Style.BackgroundIndex, run.Style.Background) is { } background) span.Background = background;
        if (run.Style.Bold) span.FontWeight = FontWeight.Bold;
        if (run.Style.Italic) span.FontStyle = FontStyle.Italic;
        if (run.Style.Underline) span.TextDecorations = TextDecorations.Underline;
        return span;
    }
}
