namespace Wandur.Core.Agents;

public sealed record AgentProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Provider { get; init; } = "openai-compatible";
    public string Endpoint { get; init; } = "http://localhost:1234/v1";
    public string Model { get; init; } = "";
    public string SystemPrompt { get; init; } = "You are a practical MUD agent. Work toward the user's goal using only the available actions. Observe carefully, avoid needless risk, and stop when the goal is complete or cannot be safely continued.";
    // Retained to read profiles saved before the goal list was introduced.
    public string DefaultGoal { get; init; } = "";
    public IReadOnlyList<AgentGoal> Goals { get; init; } = [];
    public string Commands { get; init; } = "look | look | Observe the current room\nnorth | north | Move north\nsouth | south | Move south\neast | east | Move east\nwest | west | Move west";
    public int MaxDecisions { get; init; } = 30;
    public int MaxRunSeconds { get; init; } = 600;
    public int ActionIntervalSeconds { get; init; } = 2;
    public int ResponseTimeoutSeconds { get; init; } = 120;
    public int MaxInputCharacters { get; init; } = 12000;
    public int MaxOutputTokens { get; init; } = 512;
    public bool JsonMode { get; init; }
    public Guid? CredentialId { get; init; }
}

/// <summary>Text is the Markdown description; its serialized name preserves existing profiles.</summary>
public sealed record AgentGoal(Guid Id, string Text, bool Enabled = true)
{
    public string Name { get; init; } = "";
    public string Rules { get; init; } = "";
}

public static class AgentGoals
{
    public static IReadOnlyList<AgentGoal> FromProfile(AgentProfile profile) => SingleDefault(profile.Goals.Count > 0
        ? profile.Goals : string.IsNullOrWhiteSpace(profile.DefaultGoal) ? [] : [new(profile.Id, profile.DefaultGoal)]);

    // Older clients allowed several defaults. Retain every goal but select only the first.
    public static IReadOnlyList<AgentGoal> SingleDefault(IReadOnlyList<AgentGoal> goals)
    {
        var selected = false;
        return goals.Select(goal =>
        {
            if (goal is null) throw new ArgumentException("Invalid goal.");
            if (!goal.Enabled) return goal;
            if (selected) return goal with { Enabled = false };
            selected = true; return goal;
        }).ToArray();
    }
    public static string Compose(IEnumerable<AgentGoal> goals)
    {
        var selected = goals.Where(g => g.Enabled).Take(2).ToArray();
        if (selected.Length > 1) throw new ArgumentException("Only one goal can run at a time.");
        return selected.Length == 0 ? "" : Instructions(selected[0]);
    }
    public static string Instructions(AgentGoal goal)
    {
        if (string.IsNullOrWhiteSpace(goal.Name) && string.IsNullOrWhiteSpace(goal.Rules)) return goal.Text.Trim();
        return $"# Goal: {goal.Name.Trim()}\n\n## Description\n{goal.Text.Trim()}\n\n## Rules\n{goal.Rules.Trim()}";
    }
}

public sealed record AgentCommand(string Id, string Command, string Description);
public sealed record AgentRequest(AgentProfile Profile, string Goal, string Memory, string Observation);
public sealed record AgentDecision(string Action, string Reason, string Memory);

public static class AgentCatalog
{
    public static IReadOnlyList<AgentCommand> Parse(string text)
    {
        if (text is null || text.Length > 16000) throw new ArgumentException("Invalid command catalog.");
        var result = new List<AgentCommand>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.Any(char.IsControl)) throw new ArgumentException("Commands must not contain control characters.");
            var parts = line.Split('|');
            if (parts.Length != 3) throw new ArgumentException("Commands must use: id | command | description.");
            var id = parts[0].Trim(); var command = parts[1].Trim(); var description = parts[2].Trim();
            if (id.Length is < 1 or > 64 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-') ||
                id.Equals("wait", StringComparison.OrdinalIgnoreCase) || id.Equals("done", StringComparison.OrdinalIgnoreCase) || !ids.Add(id))
                throw new ArgumentException("Command IDs must be unique, use letters, digits, underscores or dashes, and not use wait or done.");
            if (command.Length is < 1 or > 256 || command.Any(c => c is ';' or '|' or '&' or '`' or '{' or '}' || char.IsControl(c)) ||
                command.StartsWith('#') || command.StartsWith('/') || description.Length > 512)
                throw new ArgumentException("Commands must be single, fixed MUD commands without chaining or placeholders.");
            result.Add(new(id, command, description));
            if (result.Count > 64) throw new ArgumentException("At most 64 agent commands are supported.");
        }
        if (result.Count == 0) throw new ArgumentException("At least one agent command is required.");
        return result;
    }
}

public static class AgentConfiguration
{
    public static void Validate(AgentProfile profile, bool requireModel = true)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Id == Guid.Empty || profile.CredentialId == Guid.Empty) throw new ArgumentException("Invalid agent profile identity.");
        if (string.IsNullOrWhiteSpace(profile.Provider) || profile.Provider.Length > 64 || profile.Provider.Any(char.IsControl)) throw new ArgumentException("Invalid agent provider.");
        if (profile.Endpoint is null || profile.Endpoint.Length > 2048 || profile.Endpoint.Any(char.IsControl) ||
            !Uri.TryCreate(profile.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https") ||
            string.IsNullOrEmpty(endpoint.Host) || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new ArgumentException("Endpoint must be an absolute HTTP or HTTPS URL without credentials, query or fragment.");
        if (profile.Model is null || profile.Model.Length > 256 || profile.Model.Any(char.IsControl) || requireModel && string.IsNullOrWhiteSpace(profile.Model)) throw new ArgumentException("Select a valid model.");
        if (profile.SystemPrompt is null || profile.SystemPrompt.Length > 8000 || profile.DefaultGoal is null || profile.DefaultGoal.Length > 4000) throw new ArgumentException("Agent instructions are too long.");
        if (profile.Goals is null || profile.Goals.Count > 32 || profile.Goals.Any(g => g is null || g.Id == Guid.Empty || string.IsNullOrWhiteSpace(g.Text) || g.Text.Length > 4000 || g.Name is null || g.Name.Length > 120 || g.Rules is null || g.Rules.Length > 4000 || AgentGoals.Instructions(g).Length > 4000) ||
            profile.Goals.Select(g => g.Id).Distinct().Count() != profile.Goals.Count ||
            profile.Goals.Count(g => g.Enabled) > 1)
            throw new ArgumentException("Invalid agent goals.");
        if (profile.MaxDecisions is < 1 or > 1000 || profile.MaxRunSeconds is < 1 or > 86400 ||
            profile.ActionIntervalSeconds is < 1 or > 3600 || profile.ResponseTimeoutSeconds is < 1 or > 600 ||
            profile.MaxInputCharacters is < 1024 or > 128000 || profile.MaxOutputTokens is < 64 or > 8192)
            throw new ArgumentException("Agent limits are outside supported ranges.");
        AgentCatalog.Parse(profile.Commands);
    }
}

public interface IAgentModelProvider
{
    string Key { get; }
    Task<AgentDecision> DecideAsync(AgentRequest request, string? apiKey, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> ListModelsAsync(AgentProfile profile, string? apiKey, CancellationToken cancellationToken);
}

public interface IAgentProviderResolver { IAgentModelProvider Resolve(string key); }

public sealed class AgentProviderRegistry(IEnumerable<IAgentModelProvider> providers) : IAgentProviderResolver
{
    private readonly IReadOnlyDictionary<string, IAgentModelProvider> _providers = providers.ToDictionary(p => p.Key, StringComparer.Ordinal);
    public IAgentModelProvider Resolve(string key) => _providers.TryGetValue(key, out var provider) ? provider : throw new ArgumentException("Unsupported agent integration type.");
}

public interface IAgentProfileStore
{
    AgentProfile Load(string worldKey);
    void Save(string worldKey, AgentProfile profile);
    event Action<Guid>? Saved;
}
