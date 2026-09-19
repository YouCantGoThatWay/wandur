namespace Wandur.Core.Terminal;

/// <summary>Defaults for the sixteen configurable ANSI terminal colors, and the fixed xterm 256 color table above them.</summary>
public static class AnsiPalette
{
    public static IReadOnlyList<string> Defaults { get; } = Array.AsReadOnly(new[]
    {
        "#16161C", "#CD3131", "#0DBC79", "#E5C07B", "#61AFEF", "#C678DD", "#56B6C2", "#D4D4D4",
        "#808080", "#F14C4C", "#23D18B", "#F5F543", "#82AAFF", "#D670D6", "#29B8DB", "#FFFFFF"
    });

    /// <summary>The hex color for an xterm index 0 to 255: the sixteen defaults, the 6x6x6 cube, then the grey ramp.
    /// The transcript and the panel color code parser both resolve indexes here, so they agree.</summary>
    public static string Indexed(int n)
    {
        n = Math.Clamp(n, 0, 255);
        if (n < 16) return Defaults[n];
        if (n >= 232) { var c = 8 + (n - 232) * 10; return $"#{c:X2}{c:X2}{c:X2}"; }
        n -= 16;
        static int Channel(int v) => v == 0 ? 0 : 55 + v * 40;
        return $"#{Channel(n / 36):X2}{Channel(n / 6 % 6):X2}{Channel(n % 6):X2}";
    }
}
