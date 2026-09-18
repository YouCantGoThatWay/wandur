using L = Wandur.Core.Localization.Strings;

namespace Wandur.Core.Agents;

/// <summary>Local starter content, copied into the user's world profile when requested.</summary>
public static class BuiltInAgentGoals
{
    public static AgentGoal Create(string key) => key switch
    {
        "observe" => Goal(L.AgentTemplateObserve, L.AgentTemplateObserveDescription, L.AgentTemplateObserveRules),
        "explore" => Goal(L.AgentTemplateExplore, L.AgentTemplateExploreDescription, L.AgentTemplateExploreRules),
        "inventory" => Goal(L.AgentTemplateInventory, L.AgentTemplateInventoryDescription, L.AgentTemplateInventoryRules),
        _ => throw new ArgumentException("Unknown goal template.", nameof(key))
    };
    private static AgentGoal Goal(string name, string description, string rules) => new(Guid.NewGuid(), description, false) { Name = name, Rules = rules };
}
