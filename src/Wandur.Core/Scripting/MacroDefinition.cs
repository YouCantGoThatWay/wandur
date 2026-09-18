using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Core.Scripting;

public enum MacroKind { Trigger, Alias, Timer, Shortcut }
public enum MacroMatch { Contains, StartsWith, Exact }

/// <summary>Editable, language-independent rule data. Executable source is derived from this definition.</summary>
public sealed record MacroDefinition(MacroKind Kind, string Pattern, string Commands,
    MacroMatch Match = MacroMatch.Contains, bool IgnoreCase = false, int IntervalSeconds = 30);

public static class MacroCompiler
{
    public static string Compile(MacroDefinition macro)
    {
        if (!Enum.IsDefined(macro.Kind) || !Enum.IsDefined(macro.Match) || macro.Pattern is null || macro.Commands is null)
            throw new ArgumentException(L.MacroInvalid);
        if (macro.Pattern.Length > 1024 || macro.Pattern.Any(char.IsControl)) throw new ArgumentException(L.MacroInvalidPattern);
        var commands = macro.Commands.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (commands.Length is < 1 or > 20 || commands.Any(command => string.IsNullOrWhiteSpace(command) || command.Length > 1024 || command.Any(c => char.IsControl(c) || c is '\u2028' or '\u2029')) || macro.Commands.Length > 8192)
            throw new ArgumentException(L.MacroInvalidCommands);
        var actions = string.Join("\n", commands.Select(command => "mud.send(" + JsonSerializer.Serialize(command) + ");"));
        if (macro.Kind == MacroKind.Timer)
        {
            if (macro.IntervalSeconds is < 1 or > 86400) throw new ArgumentException(L.MacroInvalidInterval);
            return "mud.every(" + macro.IntervalSeconds.ToString(CultureInfo.InvariantCulture) + ", () => {\n" + actions + "\n});";
        }
        if (macro.Kind == MacroKind.Shortcut)
        {
            if (!Enumerable.Range(1, 12).Any(i => macro.Pattern == "F" + i.ToString(CultureInfo.InvariantCulture))) throw new ArgumentException(L.MacroInvalidShortcut);
            return "mud.on(Events.Key, event => { if (event.text === " + JsonSerializer.Serialize(macro.Pattern) + ") {\n" + actions + "\n} });";
        }
        if (string.IsNullOrWhiteSpace(macro.Pattern)) throw new ArgumentException(L.MacroInvalidPattern);
        // Escape only ECMAScript metacharacters, rather than .NET-specific escapes (e.g. spaces).
        var pattern = Regex.Replace(macro.Pattern, @"[.*+?^${}()|\[\]\\]", @"\$0");
        if (macro.Kind == MacroKind.Alias || macro.Match == MacroMatch.Exact) pattern = "^" + pattern + "$";
        else if (macro.Match == MacroMatch.StartsWith) pattern = "^" + pattern;
        var method = macro.Kind == MacroKind.Alias ? "alias" : "trigger";
        return "mud." + method + "(new RegExp(" + JsonSerializer.Serialize(pattern) + ", " + JsonSerializer.Serialize(macro.IgnoreCase ? "i" : "") + "), () => {\n" + actions + "\n});";
    }

    public static void Validate(WorldScriptDefinition script)
    {
        if (script.Macro is { } macro && script.Source != Compile(macro)) throw new ArgumentException(L.MacroInvalid);
    }
}
