using Microsoft.Extensions.DependencyInjection;
using Wandur.Core.Agents;
using Wandur.Core.Discovery;
using Wandur.Core.Mapping;
using Wandur.Core.Scripting;
using Wandur.Core.Settings;
using Wandur.Core.Storage;

namespace Wandur.Desktop.Services;

public static class ClientStorageServices
{
    public static IServiceCollection AddClientStorage(this IServiceCollection services, string directory)
    {
        services.AddSingleton(new ClientDatabase(Path.Combine(directory, "wandur.db")));
        services.AddSingleton<ISettingsStore>(provider => new SqliteSettingsStore(provider.GetRequiredService<ClientDatabase>(), Path.Combine(directory, "settings.json")));
        services.AddSingleton<IRoomMapStore>(provider => new SqliteRoomMapStore(provider.GetRequiredService<ClientDatabase>(), Path.Combine(directory, "maps")));
        services.AddSingleton<IWorldScriptLibraryStore>(provider => new SqliteWorldScriptLibraryStore(provider.GetRequiredService<ClientDatabase>(), Path.Combine(directory, "scripts")));
        services.AddSingleton<IAgentProfileStore, SqliteAgentProfileStore>();
        services.AddSingleton<IWorldKnowledgeStore, SqliteWorldKnowledgeStore>();
        services.AddSingleton<IWorldUsageStore>(provider => new SqliteWorldUsageStore(provider.GetRequiredService<ClientDatabase>()));
        services.AddSingleton<Wandur.Core.History.IHistoryStore, Wandur.Core.History.SqliteHistoryStore>();
        services.AddSingleton<IWorldCatalogCache>(provider => new SqliteWorldCatalogCache(provider.GetRequiredService<ClientDatabase>(), Path.Combine(directory, "directory.json")));
        services.AddSingleton<WorldCatalog>(provider => new WorldCatalog(provider.GetRequiredService<IWorldCatalogCache>()));
        return services;
    }
}
