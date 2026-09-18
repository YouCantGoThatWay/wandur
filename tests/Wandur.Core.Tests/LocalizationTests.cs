using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Wandur.Core.Localization;

namespace Wandur.Core.Tests;

public sealed class LocalizationTests
{
    [Theory]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("de")]
    [InlineData("pt-BR")]
    public void SatellitesProvideEveryKeyWithMatchingFormatArguments(string locale)
    {
        var fallback = Strings.ResourceManager.GetResourceSet(CultureInfo.InvariantCulture, true, false)!;
        var translated = Strings.ResourceManager.GetResourceSet(CultureInfo.GetCultureInfo(locale), true, false);
        Assert.NotNull(translated);
        var expected = fallback.Cast<DictionaryEntry>().ToDictionary(p => (string)p.Key, p => (string)p.Value!);
        var actual = translated.Cast<DictionaryEntry>().ToDictionary(p => (string)p.Key, p => (string)p.Value!);
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach (var (key, value) in expected)
        {
            Assert.False(string.IsNullOrWhiteSpace(actual[key]), key);
            // Source-code resources contain JavaScript braces, not composite-format placeholders.
            if (key is not (nameof(Strings.ScriptApiExamples) or nameof(Strings.ScriptStarterExample)))
                Assert.Equal(CompositeFormat.Parse(value).MinimumArgumentCount, CompositeFormat.Parse(actual[key]).MinimumArgumentCount);
            var formatTokens = Regex.Matches(value, @"\{\d+[^}]*\}").Select(m => m.Value).Order();
            Assert.Equal(formatTokens, Regex.Matches(actual[key], @"\{\d+[^}]*\}").Select(m => m.Value).Order());
        }
    }

    [Theory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("de")]
    [InlineData("pt-BR")]
    public void LocalizedScriptResourcesContainValidJavaScript(string locale)
    {
        foreach (var key in new[] { nameof(Strings.ScriptApiExamples), nameof(Strings.ScriptStarterExample) })
        {
            var source = Strings.ResourceManager.GetString(key, CultureInfo.GetCultureInfo(locale));
            Assert.NotNull(source);
            Assert.Null(new Wandur.Core.Scripting.JavaScriptEngine().Load(source).Error);
        }
    }

    [Theory]
    [InlineData("es-MX", "es")]
    [InlineData("fr-CA", "fr")]
    [InlineData("de-AT", "de")]
    [InlineData("pt-PT", "pt-BR")]
    [InlineData("ja-JP", "en")]
    public void SystemLocaleChoosesSupportedLanguageOrEnglish(string system, string expected)
        => Assert.Equal(expected, UiLanguage.Resolve("", CultureInfo.GetCultureInfo(system)).Name);

    [Fact]
    public void ExplicitLanguageWinsOverSystemPreference()
        => Assert.Equal("de", UiLanguage.Resolve("de", CultureInfo.GetCultureInfo("es-MX")).Name);
}
