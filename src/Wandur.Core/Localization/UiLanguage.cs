using System.Globalization;

namespace Wandur.Core.Localization;

public sealed record LanguageChoice(string Code, string Name)
{
    public override string ToString() => Name;
}

public static class UiLanguage
{
    private static CultureInfo? _culture;
    public static CultureInfo Culture => _culture ?? CultureInfo.CurrentUICulture;
    public static string[] SupportedCodes { get; } = ["", "en", "es", "fr", "de", "pt-BR"];
    public static IReadOnlyList<LanguageChoice> Choices =>
    [
        new("", Strings.SystemLanguage), new("en", "English"), new("es", "Español"),
        new("fr", "Français"), new("de", "Deutsch"), new("pt-BR", "Português (Brasil)")
    ];

    public static CultureInfo Resolve(string code, CultureInfo? system = null)
    {
        var requested = code.Length > 0 ? CultureInfo.GetCultureInfo(code) : system ?? CultureInfo.InstalledUICulture;
        var match = SupportedCodes.FirstOrDefault(c => c.Length > 0 && c.Equals(requested.Name, StringComparison.OrdinalIgnoreCase))
            ?? SupportedCodes.FirstOrDefault(c => c.Length > 0 && c.Equals(requested.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase))
            ?? (requested.TwoLetterISOLanguageName == "pt" ? "pt-BR" : "en");
        return CultureInfo.GetCultureInfo(match);
    }

    public static event Action? Changed;

    public static void Apply(string code)
    {
        var culture = Resolve(code);
        _culture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.CurrentCulture = culture;
        Changed?.Invoke();
    }
}
