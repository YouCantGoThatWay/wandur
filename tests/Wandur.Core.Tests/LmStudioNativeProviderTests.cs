using System.Net;
using System.Text.Json;
using Wandur.Core.Agents;

namespace Wandur.Core.Tests;

public sealed class LmStudioNativeProviderTests
{
    private static AgentProfile Profile => new() { Provider = "lmstudio-native", Endpoint = "http://localhost:1234/api/v1", Model = "gemma" };
    private static IAgentModelProvider Create(HttpClient client) => new LmStudioNativeProvider(client);
    [Fact]
    public async Task DiscoveryUsesNativeKeysAndExcludesEmbeddingModels()
    {
        using var client = new HttpClient(new Handler((request, _) =>
        {
            Assert.Equal("/api/v1/models", request.RequestUri!.AbsolutePath);
            return Task.FromResult(Response("""{"models":[{"type":"llm","key":"gemma"},{"type":"embedding","key":"embed"},{"type":"llm","key":"gemma"}]}"""));
        }));
        Assert.Equal(new[] { "gemma" }, await Create(client).ListModelsAsync(Profile, null, default));
    }
    [Fact]
    public async Task NativeChatUsesStatelessBoundedInputAndValidatesFinalMessage()
    {
        using var client = new HttpClient(new Handler(async (request, token) =>
        {
            Assert.Equal("/api/v1/chat", request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var root = body.RootElement;
            Assert.False(root.GetProperty("store").GetBoolean());
            Assert.False(root.GetProperty("stream").GetBoolean());
            Assert.Empty(root.GetProperty("integrations").EnumerateArray());
            Assert.Equal(512, root.GetProperty("max_output_tokens").GetInt32());
            Assert.DoesNotContain("Untrusted room", root.GetProperty("system_prompt").GetString());
            Assert.Contains("Untrusted room", root.GetProperty("input").GetString());
            Assert.True(root.GetProperty("input").GetString()!.Length + root.GetProperty("system_prompt").GetString()!.Length <= 12000);
            Assert.False(root.TryGetProperty("messages", out _));
            return Response(JsonSerializer.Serialize(new { output = new[] { new { type = "reasoning", content = "internal" }, new { type = "message", content = "{\"action\":\"north\",\"reason\":\"Explore\",\"memory\":\"Vestibule\"}" } } }));
        }));
        var decision = await Create(client).DecideAsync(new(Profile, "Explore", "", "Untrusted room"), null, default);
        Assert.Equal("north", decision.Action); Assert.Equal("Vestibule", decision.Memory);
    }
    [Theory]
    [InlineData("{\"output\":[{\"type\":\"reasoning\",\"content\":\"only thoughts\"}]}")]
    [InlineData("{\"output\":[{\"type\":\"tool_call\",\"content\":\"bad\"}]}")]
    [InlineData("{\"output\":[{\"type\":\"message\",\"content\":\"{\\\"action\\\":\\\"quit\\\",\\\"reason\\\":\\\"\\\",\\\"memory\\\":\\\"\\\"}\"}]}")]
    [InlineData("{\"output\":[{\"type\":\"message\",\"content\":\"{\\\"action\\\":\"}]}")]
    public async Task InvalidNativeOutputFailsWithoutRetry(string json)
    {
        var calls = 0;
        using var client = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(Response(json)); }));
        await Assert.ThrowsAsync<InvalidDataException>(() => Create(client).DecideAsync(new(Profile, "Explore", "", "Room"), null, default));
        Assert.Equal(1, calls);
    }
    private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
}
