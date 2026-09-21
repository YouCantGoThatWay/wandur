namespace Wandur.Desktop.Tests;

/// <summary>
/// The UI language is process-global, and xUnit runs test classes in parallel, so a class that applies
/// a language races every class that asserts a localized string: Spanish from one test would surface as
/// "1 de 5" in another's English assertion. Every class that writes the language or reads a localized
/// string joins this collection, which xUnit runs one class at a time. The rest of the suite still runs
/// in parallel.
/// </summary>
[CollectionDefinition(Name)]
public sealed class UiLanguageCollection
{
    public const string Name = "UiLanguage";
}
