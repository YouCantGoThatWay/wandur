using System.Text;
using System.Text.Json;

namespace Wandur.Core.Scripting;

public static class ScriptWorker
{
    internal const int MaximumMessageCharacters = 2 * 1024 * 1024;

    public static async Task RunAsync(TextReader input, TextWriter output)
    {
        var engine = new JavaScriptEngine();
        while (true)
        {
            ScriptResult result;
            try
            {
                var line = await ReadMessageAsync(input, CancellationToken.None).ConfigureAwait(false);
                if (line is null) return;
                var request = JsonSerializer.Deserialize<ScriptEvent>(line) ?? throw new JsonException("Missing request.");
                result = request.Kind == "load" ? engine.Load(request.Text) : engine.Dispatch(request);
            }
            catch (Exception error)
            {
                result = new(false, [], error.Message[..Math.Min(error.Message.Length, 2048)]);
            }
            await output.WriteLineAsync(JsonSerializer.Serialize(result)).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
            if (result.Error is not null) return;
        }
    }

    // ReadLineAsync allocates without a bound. Limit the framing before deserializing.
    internal static async Task<string?> ReadMessageAsync(TextReader reader, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var character = new char[1];
        while (await reader.ReadAsync(character.AsMemory(), cancellationToken).ConfigureAwait(false) != 0)
        {
            if (character[0] == '\n') return text.ToString();
            if (text.Length >= MaximumMessageCharacters) throw new InvalidDataException("Worker message exceeds size limit.");
            text.Append(character[0]);
        }
        return text.Length == 0 ? null : throw new EndOfStreamException("Incomplete worker message.");
    }
}
