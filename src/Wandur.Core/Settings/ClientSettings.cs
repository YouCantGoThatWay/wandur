using L = Wandur.Core.Localization.Strings;
using System.Text.Json;
using Wandur.Core.Sessions;
using System.Text.RegularExpressions;

namespace Wandur.Core.Settings;

public sealed record ConnectionProfile
{
    [System.Text.Json.Serialization.JsonConverter(typeof(Wandur.Core.Discovery.WorldMappingConverter))]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Wandur.Models.WorldMapping? ProtocolMapping { get; init; }
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = L.NewWorld;
    public string Host { get; init; } = "";
    public int Port { get; init; } = 4000;
    public bool UseTls { get; init; }
    public string Encoding { get; init; } = "utf-8";
    public Wandur.Core.Discovery.WorldTheme? Theme { get; init; }
    /// <summary>The directory's free-text codebase, which chooses the channel family when this world has no rules of its own.</summary>
    public string Codebase { get; init; } = "";
    /// <summary>Channel shapes for this world. Empty means the family defaults; a channel pack fills the same list.</summary>
    public Wandur.Core.Channels.ChannelRuleList ChannelRules { get; init; } = [];
    public string Username { get; init; } = "";
    public Guid? PasswordId { get; init; }
    public bool AutoLogin { get; init; }
    public string UsernamePrompt { get; init; } = AutoLoginSequence.DefaultUsernamePrompt;
    public string PasswordPrompt { get; init; } = AutoLoginSequence.DefaultPasswordPrompt;

    public Wandur.Models.WorldMapping? GetProtocolMapping()
    {
        var endpoint = new Wandur.Models.WorldEndpoint(Host, Port, UseTls);
        return Wandur.Models.MappingValidation.Endpoint(endpoint) && Wandur.Models.MappingValidation.IsValid(ProtocolMapping)
            && ProtocolMapping!.Endpoint.Matches(endpoint) ? ProtocolMapping : null;
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 100) throw new ArgumentException(L.GiveThisWorldANameOf1100Characters);
        if (string.IsNullOrWhiteSpace(Host) || Host.Length > 253 || Uri.CheckHostName(Host) == UriHostNameType.Unknown)
            throw new ArgumentException(L.EnterAHostnameOrIPAddressWithoutAURL);
        if (Port is < 1 or > 65535) throw new ArgumentException(L.PortMustBeBetween1And65535);
        if (Encoding is not ("utf-8" or "latin1")) throw new ArgumentException(L.ChooseUTF8OrLatin1Encoding);
        if (Username is null || Username.Length > 256 || Username.Any(char.IsControl)) throw new ArgumentException(L.UsernameMustBeASingleLineOfAtMost);
        if (AutoLogin && (string.IsNullOrWhiteSpace(Username) || PasswordId is null)) throw new ArgumentException(L.AutoLoginNeedsAUsernameAndASavedPassword);
        if (Codebase is null || Codebase.Length > 100 || Codebase.Any(char.IsControl)) throw new ArgumentException(L.InvalidWorldProfile);
        if (ChannelRules is null || ChannelRules.Count > 200) throw new ArgumentException(L.ChannelRulesInvalid);
        foreach (var rule in ChannelRules)
        {
            if (rule is null || string.IsNullOrWhiteSpace(rule.Channel) || rule.Channel.Length > 40 || string.IsNullOrEmpty(rule.Pattern)
                || rule.Pattern.Length > 400 || rule.ReplyCommand is { Length: > 80 }) throw new ArgumentException(L.ChannelRulesInvalid);
            try { _ = new Regex(rule.Pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)); }
            catch (ArgumentException ex) { throw new ArgumentException(L.ChannelRulesInvalid, ex); }
        }
        try { AutoLoginSequence.Compile(UsernamePrompt); AutoLoginSequence.Compile(PasswordPrompt); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { throw new ArgumentException(L.InvalidLoginPromptPatternUseASimpleRegularExpression, ex); }

    }
    public override string ToString() => Name;
}

public sealed record ClientSettings
{
    public string Theme { get; init; } = "Ember";
    public string Language { get; init; } = "";
    public double FontSize { get; init; } = 15;
    public string? Foreground { get; init; }
    public string? Background { get; init; }
    public bool LocalEcho { get; init; }
    public bool AllowBlinkingText { get; init; }
    /// <summary>The share of the output area the live view takes while the transcript is scrolled back; 0 turns the split off.</summary>
    public double ScrollTailShare { get; init; } = 0.25;
    public bool UseWorldThemes { get; init; } = true;
    public bool ClassifyRoomsLocally { get; init; } = true;
    public double RoomClassificationThreshold { get; init; } = 0.8;
    public bool MapAutoCenter { get; init; } = true;
    /// <summary>Whether the docked Channels panel mirrors recognized channel traffic beside the transcript.</summary>
    public bool ShowChannelsPanel { get; init; } = true;
    /// <summary>Whether the composer offers grayed completions from command history and words seen in the session.</summary>
    public bool ComposerSuggestions { get; init; } = true;
    public List<UserTheme> CustomThemes { get; init; } = [];
    public List<ConnectionProfile> Profiles { get; init; } = [];
    public void Validate()
    {
        if (!Wandur.Core.Localization.UiLanguage.SupportedCodes.Contains(Language)) throw new ArgumentException(L.UnsupportedLanguage);
        if (CustomThemes is null || CustomThemes.Count > 100) throw new ArgumentException(L.InvalidCustomTheme);
        foreach (var custom in CustomThemes) { if (custom is null) throw new ArgumentException(L.InvalidCustomTheme); custom.Validate(); }
        if (CustomThemes.Select(t => t.Id).Distinct().Count() != CustomThemes.Count ||
            CustomThemes.Select(t => t.Name).Concat(UserTheme.PresetNames).Distinct(StringComparer.OrdinalIgnoreCase).Count() != CustomThemes.Count + UserTheme.PresetNames.Count)
            throw new ArgumentException(L.ThemeNameUnique);
        if (!UserTheme.PresetNames.Contains(Theme) && !CustomThemes.Any(t => t.Id == Theme)) throw new ArgumentException(L.UnknownColorScheme);
        if (!double.IsFinite(FontSize) || FontSize is < 11 or > 28) throw new ArgumentException(L.TextSizeMustBeBetween11And28);
        if (ScrollTailShare != 0 && (!double.IsFinite(ScrollTailShare) || ScrollTailShare is < 0.1 or > 0.6)) throw new ArgumentException(L.LiveViewShareMustBeBetween10And60);
        if (!double.IsFinite(RoomClassificationThreshold) || RoomClassificationThreshold is < 0.5 or > 0.99) throw new ArgumentException(L.RoomClassificationThresholdRange);
        foreach (var color in new[] { Foreground, Background })
            if (color is not null && !Regex.IsMatch(color, "^#[0-9a-fA-F]{6}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                throw new ArgumentException(L.CustomColorsMustUseRRGGBBOrBeLeftBlank);
        if (Profiles is null || Profiles.Count > 200) throw new ArgumentException(L.AtMost200WorldProfilesAreSupported);
        foreach (var profile in Profiles)
        {
            if (profile is null) throw new ArgumentException(L.InvalidWorldProfile);
            profile.Validate();
        }
        if (Profiles.Select(p => p.Id).Distinct().Count() != Profiles.Count) throw new ArgumentException(L.WorldProfileIDsMustBeUnique);
    }
}

public sealed record SettingsLoadResult(ClientSettings Settings, string? Warning = null);

public sealed class SettingsStore(string path) : ISettingsStore
{
    public string FilePath { get; } = path;
    // Upgrade only the exact default shipped before LOTJ shortcut prompts were supported.
    private const string PreviousDefaultPasswordPrompt = @"^\s*(?:(?:please\s+)?enter\s+(?:your\s+)?|your\s+)?(?:password|passphrase|passcode)\s*[:>]\s*$";
    private bool _preserveOriginal;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public SettingsLoadResult Load()
    {
        if (!File.Exists(FilePath)) return new(new());
        try
        {
            if (new FileInfo(FilePath).Length > 1_048_576) throw new ArgumentException(L.SettingsFileIsTooLarge);
            var settings = JsonSerializer.Deserialize<ClientSettings>(File.ReadAllText(FilePath)) ?? throw new JsonException(L.EmptySettings);
            settings.Validate();
            settings = settings with
            {
                Profiles = settings.Profiles.Select(profile => profile.PasswordPrompt == PreviousDefaultPasswordPrompt
                    ? profile with { PasswordPrompt = AutoLoginSequence.DefaultPasswordPrompt }
                    : profile).ToList()
            };
            return new(settings);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            _preserveOriginal = true;
            return new(new(), L.Format(L.SettingsCouldNotBeReadDefaultsAreInUse, ex.Message));
        }
    }

    public void Save(ClientSettings settings)
    {
        settings.Validate();
        var directory = Path.GetDirectoryName(Path.GetFullPath(FilePath))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".settings-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
            if (_preserveOriginal && File.Exists(FilePath))
                File.Copy(FilePath, FilePath + ".corrupt-" + Guid.NewGuid().ToString("N"));
            File.Move(temporary, FilePath, overwrite: true);
            _preserveOriginal = false;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
