using System.Security.Cryptography;
using System.Text;
using Wandur.Core.Agents;
using Wandur.Desktop.Security;

namespace Wandur.Desktop.Services;

public sealed class AgentClientServices(IAgentProfileStore profiles, IAgentProviderResolver providers,
    IPasswordVault vault) : IAgentClientServices
{
    public IAgentProfileStore Profiles { get; } = profiles;
    public IAgentProviderResolver Providers { get; } = providers;
    private static string CredentialKey(AgentProfile profile) => "agent-" + Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes($"{profile.Id}:{profile.CredentialId}:{profile.Provider}:{profile.Endpoint.TrimEnd('/')}")));
    public Task<string?> ReadCredentialAsync(AgentProfile profile) => profile.CredentialId is null
        ? Task.FromResult<string?>(null) : vault.ReadAsync(CredentialKey(profile));

    public async Task<AgentProfile> SaveAsync(string worldKey, AgentProfile profile, string newKey, bool forgetKey)
    {
        AgentConfiguration.Validate(profile, requireModel: false);
        var previous = Profiles.Load(worldKey);
        var sameEndpoint = previous.Provider == profile.Provider && previous.Endpoint.TrimEnd('/') == profile.Endpoint.TrimEnd('/');
        profile = profile with { Id = previous.Id, CredentialId = sameEndpoint && !forgetKey ? previous.CredentialId : null };
        if (!string.IsNullOrEmpty(newKey))
        {
            PasswordVault.ValidatePassword(newKey);
            profile = profile with { CredentialId = Guid.NewGuid() };
            await vault.WriteAsync(CredentialKey(profile), newKey);
        }
        try { Profiles.Save(worldKey, profile); }
        catch
        {
            if (profile.CredentialId is not null && profile.CredentialId != previous.CredentialId)
                await vault.DeleteAsync(CredentialKey(profile));
            throw;
        }
        if (previous.CredentialId is not null && previous.CredentialId != profile.CredentialId)
            await vault.DeleteAsync(CredentialKey(previous));
        return profile;
    }
}
