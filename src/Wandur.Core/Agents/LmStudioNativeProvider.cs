using System.Text.Json;

namespace Wandur.Core.Agents;

/// <summary>LM Studio's native /api/v1 API. Decisions remain stateless and use the shared action contract.</summary>
public sealed class LmStudioNativeProvider(HttpClient client) : IAgentModelProvider
{
    private readonly AgentHttpTransport _transport = new(client);
    public string Key => "lmstudio-native";

    public async Task<IReadOnlyList<string>> ListModelsAsync(AgentProfile profile, string? apiKey, CancellationToken cancellationToken)
    {
        AgentConfiguration.Validate(profile, requireModel: false);
        using var response = await _transport.SendAsync(profile, "models", null, apiKey, cancellationToken).ConfigureAwait(false);
        try
        {
            var models = response.RootElement.GetProperty("models");
            if (models.ValueKind != JsonValueKind.Array || models.GetArrayLength() > 1024) throw AgentDecisionCodec.InvalidResponse();
            var result = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var model in models.EnumerateArray())
            {
                if (model.GetProperty("type").GetString() != "llm") continue;
                var key = model.GetProperty("key").GetString();
                if (string.IsNullOrWhiteSpace(key) || key.Length > 256 || key.Any(char.IsControl)) throw AgentDecisionCodec.InvalidResponse();
                result.Add(key);
            }
            return result.ToArray();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
        { throw AgentDecisionCodec.InvalidResponse(); }
    }

    public async Task<AgentDecision> DecideAsync(AgentRequest request, string? apiKey, CancellationToken cancellationToken)
    {
        var (system, input, catalog) = AgentDecisionCodec.Build(request);
        var payload = new
        {
            model = request.Profile.Model, system_prompt = system, input,
            max_output_tokens = request.Profile.MaxOutputTokens, temperature = 0,
            stream = false, store = false, integrations = Array.Empty<string>()
        };
        using var response = await _transport.SendAsync(request.Profile, "chat", payload, apiKey, cancellationToken).ConfigureAwait(false);
        try
        {
            var output = response.RootElement.GetProperty("output");
            if (output.ValueKind != JsonValueKind.Array || output.GetArrayLength() > 32) throw AgentDecisionCodec.InvalidResponse();
            string? content = null;
            foreach (var item in output.EnumerateArray())
            {
                switch (item.GetProperty("type").GetString())
                {
                    case "reasoning": break;
                    case "message" when content is null: content = item.GetProperty("content").GetString() ?? throw AgentDecisionCodec.InvalidResponse(); break;
                    default: throw AgentDecisionCodec.InvalidResponse();
                }
            }
            // The native API has no documented finish_reason. Require one complete validated decision.
            return AgentDecisionCodec.ParseDecision(content ?? throw AgentDecisionCodec.InvalidResponse(), catalog);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
        { throw AgentDecisionCodec.InvalidResponse(); }
    }
}
