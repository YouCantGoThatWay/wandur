using Wandur.Core.Agents;
using Wandur.Desktop.Security;
using Wandur.Desktop.Services;

namespace Wandur.Desktop.Tests;

public sealed class AgentCredentialTests
{
    [Fact]
    public async Task TokenLivesInVaultAndCannotFollowAnEndpointOrIntegrationChange()
    {
        var store = new Store(); var vault = new Vault();
        var service = new AgentClientServices(store, new AgentProviderRegistry([]), vault);
        var saved = await service.SaveAsync("world", store.Profile, "token-secret", false);
        Assert.NotNull(saved.CredentialId); Assert.Equal("token-secret", await service.ReadCredentialAsync(saved));
        Assert.DoesNotContain("token-secret", System.Text.Json.JsonSerializer.Serialize(saved));
        var moved = await service.SaveAsync("world", saved with { Endpoint = "http://localhost:9999/v1" }, "", false);
        Assert.Null(moved.CredentialId); Assert.Empty(vault.Keys);
        saved = await service.SaveAsync("world", moved, "new-token", false);
        moved = await service.SaveAsync("world", saved with { Provider = "future" }, "", false);
        Assert.Null(moved.CredentialId); Assert.Empty(vault.Keys);
    }

    [Fact]
    public async Task FailedPersistenceRemovesNewTokenAndPreservesOldOne()
    {
        var store = new Store(); var vault = new Vault();
        var service = new AgentClientServices(store, new AgentProviderRegistry([]), vault);
        var saved = await service.SaveAsync("world", store.Profile, "old-token", false);
        store.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => service.SaveAsync("world", saved, "new-token", false));
        Assert.Single(vault.Keys); Assert.Equal("old-token", await service.ReadCredentialAsync(saved));
    }
    private sealed class Store : IAgentProfileStore
    {
        public AgentProfile Profile = new(); public bool Fail;
        public event Action<Guid>? Saved;
        public AgentProfile Load(string worldKey) => Profile;
        public void Save(string worldKey, AgentProfile profile)
        { if (Fail) throw new IOException(); Profile = profile; Saved?.Invoke(profile.Id); }
    }
    private sealed class Vault : IPasswordVault
    {
        public Dictionary<string, string> Keys { get; } = [];
        public string Description => "test";
        public Task<string?> ReadAsync(string key) => Task.FromResult(Keys.GetValueOrDefault(key));
        public Task WriteAsync(string key, string password) { Keys[key] = password; return Task.CompletedTask; }
        public Task DeleteAsync(string key) { Keys.Remove(key); return Task.CompletedTask; }
    }
}
