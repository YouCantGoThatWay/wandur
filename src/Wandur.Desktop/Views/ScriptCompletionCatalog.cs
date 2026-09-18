using System.Text.RegularExpressions;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.Views;

internal sealed record ScriptSuggestion(string Name, string Signature, string Description);
internal sealed record ScriptCompletionContext(int PrefixLength, IReadOnlyList<ScriptSuggestion> Suggestions);

/// <summary>Local API hints, intentionally independent of a JavaScript language server.</summary>
internal static class ScriptCompletionCatalog
{
    private static readonly Regex Member = new(@"(?:(?<receiver>[$A-Za-z_][$\w]*)\s*\.\s*)?(?<prefix>[$A-Za-z_][$\w]*)?$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50));
    private static readonly Regex Subscription = new("mud\\s*\\.\\s*on\\s*\\(\\s*(?:Events\\s*\\.\\s*(?<kind>Line|Gmcp)|[\"'](?<legacy>line|gmcp)[\"'])\\s*,\\s*(?:function\\s*)?\\(?\\s*(?<parameter>[$A-Za-z_][$\\w]*)\\s*\\)?\\s*(?:=>|\\{)", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50));

    public static ScriptCompletionContext Get(string beforeCaret)
    {
        try { return GetCore(beforeCaret); }
        catch (RegexMatchTimeoutException) { return new(0, []); }
    }

    private static ScriptCompletionContext GetCore(string beforeCaret)
    {
        // Completion must stay cheap even in the largest allowed script.
        var context = beforeCaret.Length > 8192 ? beforeCaret[^8192..] : beforeCaret;
        var match = Member.Match(context);
        if (!match.Success) return new(0, []);
        var receiver = match.Groups["receiver"].Value;
        var prefix = match.Groups["prefix"].Value;
        IReadOnlyList<ScriptSuggestion> choices;
        if (receiver == "mud") choices =
        [
            new("on", "on(event, callback)", L.ScriptCompleteOn),
            new("send", "send(command)", L.ScriptCompleteSend),
            new("echo", "echo(text)", L.ScriptCompleteEcho),
            new("alias", "alias(pattern, callback)", L.ScriptCompleteAlias),
            new("trigger", "trigger(pattern, callback)", L.ScriptCompleteTrigger),
            new("every", "every(seconds, callback)", L.ScriptCompleteEvery)
        ];
        else if (receiver == "Events") choices =
        [
            new("Line", "Line", L.ScriptCompleteLine),
            new("Gmcp", "Gmcp", L.ScriptCompleteGmcp)
        ];
        else if (receiver.Length == 0) choices =
        [
            new("mud", "mud", L.ScriptCompleteMud), new("Events", "Events", L.ScriptCompleteEvents)
        ];
        else
        {
            var subscription = Subscription.Matches(context).LastOrDefault();
            if (subscription?.Groups["parameter"].Value != receiver) return new(prefix.Length, []);
            var kind = subscription.Groups["kind"].Value + subscription.Groups["legacy"].Value;
            choices = kind.Equals("Line", StringComparison.OrdinalIgnoreCase)
                ? [new("text", "text: string", L.ScriptCompleteText)]
                : [new("package", "package: string", L.ScriptCompletePackage), new("data", "data: JSON | null", L.ScriptCompleteData)];
        }
        return new(prefix.Length, choices.Where(c => c.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray());
    }
}
