using Wandur.Core.Settings;

namespace Wandur.Desktop.Services;

public interface IWorldProfileStore
{
    IReadOnlyList<ConnectionProfile> Profiles { get; }
    string CredentialStoreName { get; }
    Task SaveWorldAsync(ConnectionProfile profile, string password, bool rememberPassword);
    Task RemoveWorldAsync(ConnectionProfile profile);
}
