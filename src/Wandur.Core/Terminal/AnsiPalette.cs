namespace Wandur.Core.Terminal;

/// <summary>Defaults for the sixteen configurable ANSI terminal colors.</summary>
public static class AnsiPalette
{
    public static IReadOnlyList<string> Defaults { get; } = Array.AsReadOnly(new[]
    {
        "#16161C", "#CD3131", "#0DBC79", "#E5C07B", "#61AFEF", "#C678DD", "#56B6C2", "#D4D4D4",
        "#808080", "#F14C4C", "#23D18B", "#F5F543", "#82AAFF", "#D670D6", "#29B8DB", "#FFFFFF"
    });
}
