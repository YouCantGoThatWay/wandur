using L = Wandur.Core.Localization.Strings;
using System.Security.Cryptography;
using System.Text;
using Wandur.Core.Settings;

namespace Wandur.Desktop.Security;

public interface IPasswordVault
{
    string Description { get; }
    Task<string?> ReadAsync(string key);
    Task WriteAsync(string key, string password);
    Task DeleteAsync(string key);
}

public static class PasswordVault
{
    // A credential cannot silently move to another host, port, transport, or account when a profile is edited.
    public static string Key(ConnectionProfile profile)
    {
        if (profile.PasswordId is null) throw new ArgumentException(L.ThisWorldHasNoSavedPassword);
        var identity = System.Text.Json.JsonSerializer.Serialize(new { profile.Id, profile.PasswordId, Host = profile.Host.ToLowerInvariant(), profile.Port, profile.UseTls, profile.Username });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    public static void ValidatePassword(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length > 1024 || password.Any(char.IsControl))
            throw new ArgumentException(L.PasswordMustBeASingleLineOf11024);
    }
}
