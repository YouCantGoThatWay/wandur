using Wandur.Core.Agents;

namespace Wandur.Desktop.Services;

public interface IAgentClientServices
{
    IAgentProfileStore Profiles { get; }
    IAgentProviderResolver Providers { get; }
    Task<string?> ReadCredentialAsync(AgentProfile profile);
    Task<AgentProfile> SaveAsync(string worldKey, AgentProfile profile, string newKey, bool forgetKey);
}
