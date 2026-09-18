using System.Text.Json;
using Microsoft.Data.Sqlite;
using Wandur.Core.Storage;

namespace Wandur.Core.Agents;

public sealed class SqliteAgentProfileStore(ClientDatabase database) : IAgentProfileStore
{
    public event Action<Guid>? Saved;

    public AgentProfile Load(string worldKey)
    {
        try
        {
            return database.Write((connection, transaction) =>
            {
                var worldId = database.ResolveWorld(connection, transaction, worldKey);
                using var query = connection.CreateCommand(); query.Transaction = transaction;
                query.CommandText = "SELECT payload FROM world_agent_profiles WHERE world_id=$world";
                query.Parameters.AddWithValue("$world", worldId);
                if (query.ExecuteScalar() is string json)
                {
                    if (json.Length > 256000) throw new InvalidDataException("Stored agent profile is too large.");
                    var profile = JsonSerializer.Deserialize<AgentProfile>(json) ?? throw new InvalidDataException("Stored agent profile is invalid.");
                    if (profile.Goals is not null) profile = profile with { Goals = AgentGoals.SingleDefault(profile.Goals) };
                    AgentConfiguration.Validate(profile, requireModel: false);
                    return profile;
                }
                var defaults = new AgentProfile();
                Write(connection, transaction, worldId, defaults);
                return defaults;
            });
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        { throw new InvalidDataException("Stored agent profile is invalid."); }
    }

    public void Save(string worldKey, AgentProfile profile)
    {
        AgentConfiguration.Validate(profile, requireModel: false);
        database.Write((connection, transaction) =>
        {
            Write(connection, transaction, database.ResolveWorld(connection, transaction, worldKey), profile);
            return true;
        });
        Saved?.Invoke(profile.Id);
    }

    private static void Write(SqliteConnection connection, SqliteTransaction transaction, string world, AgentProfile profile)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO world_agent_profiles(world_id,payload) VALUES($world,$payload) ON CONFLICT(world_id) DO UPDATE SET payload=excluded.payload";
        command.Parameters.AddWithValue("$world", world);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(profile));
        command.ExecuteNonQuery();
    }
}
