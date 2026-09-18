using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Wandur.Core.Agents;

internal sealed class AgentHttpTransport(HttpClient client)
{
    private const int MaximumResponseBytes = 128 * 1024;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> OriginGates = new(StringComparer.OrdinalIgnoreCase);
    public async Task<JsonDocument> SendAsync(AgentProfile profile, string route, object? payload, string? apiKey, CancellationToken cancellationToken)
    {
        if (apiKey?.Any(char.IsControl) == true) throw new ArgumentException("Invalid API credential.");
        var endpoint = new Uri(profile.Endpoint.TrimEnd('/') + "/" + route);
        var gate = OriginGates.GetOrAdd(endpoint.GetLeftPart(UriPartial.Authority), _ => new SemaphoreSlim(1, 1));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(profile.ResponseTimeoutSeconds));
        var entered = false;
        try
        {
            await gate.WaitAsync(timeout.Token).ConfigureAwait(false);
            entered = true;
            using var request = new HttpRequestMessage(payload is null ? HttpMethod.Get : HttpMethod.Post, endpoint);
            if (!string.IsNullOrWhiteSpace(apiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            if (payload is not null) request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException("Agent provider returned HTTP " + (int)response.StatusCode + ".", null, response.StatusCode);
            if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw new InvalidDataException("Agent provider response exceeds the size limit.");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            while (true)
            {
                var read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false);
                if (read == 0) break;
                if (buffer.Length + read > MaximumResponseBytes) throw new InvalidDataException("Agent provider response exceeds the size limit.");
                buffer.Write(chunk, 0, read);
            }
            return JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("Agent provider request timed out."); }
        catch (HttpRequestException exception)
        { throw new HttpRequestException(exception.StatusCode is { } status ? "Agent provider returned HTTP " + (int)status + "." : "Cannot connect to the agent provider.", null, exception.StatusCode); }
        catch (JsonException) { throw AgentDecisionCodec.InvalidResponse(); }
        finally { if (entered) gate.Release(); }
    }

}
