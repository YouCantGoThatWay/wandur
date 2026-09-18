using L = Wandur.Core.Localization.Strings;
using System.Text.RegularExpressions;
using Wandur.Core.Settings;

namespace Wandur.Core.Sessions;

public enum LoginStep { None, Username, Password }

/// <summary>A bounded, one-shot handshake. It never holds a password or retries a rejected login.</summary>
public sealed class AutoLoginSequence
{
    public const string DefaultUsernamePrompt = @"^\s*(?:(?:(?:please\s+)?(?:\(e\)|e)nter\s+(?:your\s+)?|your\s+)?(?:user\s*name|login|name|account(?:\s+name)?|character(?:\s+(?:or\s+account\s+)?name)?)[^:\r\n]{0,180}[:>]|by what name[^\r\n]{0,180}\?)\s*$";
    public const string DefaultPasswordPrompt = @"^\s*(?:(?:please\s+)?enter\s+(?:your\s+)?|your\s+)?(?:(?:p|\(p\))assword|passphrase|passcode)\s*[:>]\s*$";
    private readonly Regex _username;
    private readonly Regex _password;
    private readonly DateTimeOffset _started;
    private int _stage;
    public bool Started => _stage != 0;
    public bool Expired => DateTimeOffset.UtcNow - _started > TimeSpan.FromMinutes(2);
    public bool Finished => _stage == 2;

    public AutoLoginSequence(ConnectionProfile profile, DateTimeOffset? started = null)
    {
        _username = Compile(profile.UsernamePrompt);
        _password = Compile(profile.PasswordPrompt);
        _started = started ?? DateTimeOffset.UtcNow;
    }

    public static Regex Compile(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern.Length > 512) throw new ArgumentException(L.LoginPromptPatternsMustBe1512Characters);
        var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50));
        if (regex.IsMatch("")) throw new ArgumentException(L.LoginPromptPatternsMustNotMatchEmptyText);
        return regex;
    }

    public LoginStep Next(string prompt)
    {
        if (Expired) _stage = 2;
        if (_stage == 2 || prompt.Length > 512) return LoginStep.None;
        if (_stage == 0 && _username.IsMatch(prompt)) { _stage = 1; return LoginStep.Username; }
        if (_stage == 1 && _password.IsMatch(prompt)) { _stage = 2; return LoginStep.Password; }
        if (_stage == 1 && _username.IsMatch(prompt)) _stage = 2;
        return LoginStep.None;
    }
}
