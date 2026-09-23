namespace Wandur.Core.Diagnostics;

/// <summary>
/// Opt-in, append-only trace of the world theme path, from the directory fetch through to the bitmaps the
/// window paints. Off unless <c>WANDUR_THEME_TRACE</c> names a writable file, so a normal run pays one
/// static read per call site. A theme that arrives but does not paint is otherwise invisible: the client
/// falls back silently by design, which is right for players and useless for whoever is authoring the skin.
/// </summary>
public static class ThemeTrace
{
    private static readonly Lock Gate = new();
    private static readonly string? Path = Resolve();
    public static bool Enabled => Path is not null;

    private static string? Resolve()
    {
        var path = Environment.GetEnvironmentVariable("WANDUR_THEME_TRACE");
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    public static void Write(string stage, string detail)
    {
        if (Path is null) return;
        var line = $"{DateTime.Now:HH:mm:ss.fff}  {stage,-22}  {detail}{Environment.NewLine}";
        try { lock (Gate) File.AppendAllText(Path, line); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException) { }
    }
}
