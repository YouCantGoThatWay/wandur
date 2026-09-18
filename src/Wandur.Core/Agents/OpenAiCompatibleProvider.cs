using System.Text.Json;

namespace Wandur.Core.Agents;

/// <summary>Stateless Chat Completions adapter; model output selects a fixed action, never executable text.</summary>
public sealed class OpenAiCompatibleProvider(HttpClient client) : IAgentModelProvider
{
    private readonly AgentHttpTransport _transport = new(client);
    public string Key => "openai-compatible";

    public async Task<AgentDecision> DecideAsync(AgentRequest request, string? apiKey, CancellationToken cancellationToken)
    {
        var (system, user, catalog) = AgentDecisionCodec.Build(request);
        var payload = new Dictionary<string, object>
        {
            ["model"] = request.Profile.Model,
            ["messages"] = new[] { new { role = "system", content = system }, new { role = "user", content = user } },
            ["max_tokens"] = request.Profile.MaxOutputTokens,
            ["temperature"] = 0,
            ["stream"] = false
        };
        if (request.Profile.JsonMode) payload["response_format"] = new { type = "json_object" };
        using var response = await _transport.SendAsync(request.Profile, "chat/completions", payload, apiKey, cancellationToken).ConfigureAwait(false);
        try
        {
            var choices = response.RootElement.GetProperty("choices");
            if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() != 1) throw InvalidResponse();
            var choice = choices[0];
            if (choice.GetProperty("finish_reason").GetString() != "stop") throw InvalidResponse();
            var message = choice.GetProperty("message");
            if (message.TryGetProperty("tool_calls", out var tools) && tools.ValueKind != JsonValueKind.Null &&
                (tools.ValueKind != JsonValueKind.Array || tools.GetArrayLength() != 0) ||
                message.TryGetProperty("function_call", out var function) && function.ValueKind != JsonValueKind.Null)
                throw InvalidResponse();
            var content = message.GetProperty("content").GetString() ?? throw InvalidResponse();
            return AgentDecisionCodec.ParseDecision(content, catalog);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        { throw InvalidResponse(); }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(AgentProfile profile, string? apiKey, CancellationToken cancellationToken)
    {
        AgentConfiguration.Validate(profile, requireModel: false);
        using var response = await _transport.SendAsync(profile, "models", null, apiKey, cancellationToken).ConfigureAwait(false);
        try
        {
            var data = response.RootElement.GetProperty("data");
            if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() > 1024) throw InvalidResponse();
            var result = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var model in data.EnumerateArray())
            {
                var id = model.GetProperty("id").GetString();
                if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || id.Any(char.IsControl)) throw InvalidResponse();
                result.Add(id);
            }
            return result.ToArray();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        { throw InvalidResponse(); }
    }

    private static InvalidDataException InvalidResponse() => new("Agent provider returned an invalid or incomplete response.");
}
