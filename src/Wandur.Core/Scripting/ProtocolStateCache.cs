using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Wandur.Core.Scripting;

/// <summary>The latest value of every MSDP variable and GMCP package one session has received, kept on the
/// host so a script runtime that starts later, or restarts, is seeded with what the world already said.
/// A world reports a variable once when the REPORT is accepted and afterwards only when it changes, so a
/// worker that starts empty would wait forever for skill levels, money or ship telemetry.
/// The bounds mirror the worker's own cache, so a seed never carries more than the worker would keep.</summary>
public sealed class ProtocolStateCache
{
    public const int MaximumEntries = 512;
    public const int MaximumValueCharacters = 32768;
    public const int MaximumBucketCharacters = 262144;
    private static readonly Regex Package = new("^[A-Za-z][A-Za-z0-9_.]*$", RegexOptions.CultureInvariant);
    private static readonly JsonDocumentOptions Parsing = new() { MaxDepth = 16 };
    private readonly object _sync = new();
    private readonly Bucket _gmcp = new();
    private readonly Bucket _msdp = new();

    private sealed class Bucket
    {
        public readonly Dictionary<string, string> Values = new(StringComparer.Ordinal);
        public int Total;

        // An update that would break a bound is dropped and the previous value stays, as in the worker.
        public bool Set(string key, string json)
        {
            if (json.Length > MaximumValueCharacters) return false;
            var exists = Values.TryGetValue(key, out var existing);
            var previous = exists ? existing!.Length : 0;
            if (!exists && Values.Count >= MaximumEntries) return false;
            if (Total - previous + json.Length > MaximumBucketCharacters) return false;
            Total += json.Length - previous;
            Values[key] = json;
            return true;
        }

        public void Clear() { Values.Clear(); Total = 0; }
    }

    public bool IsEmpty { get { lock (_sync) return _gmcp.Values.Count == 0 && _msdp.Values.Count == 0; } }
    public int MsdpCount { get { lock (_sync) return _msdp.Values.Count; } }
    public int GmcpCount { get { lock (_sync) return _gmcp.Values.Count; } }

    /// <summary>The cached JSON of one MSDP variable, or null when the world has not sent it.</summary>
    public string? TryGetMsdp(string variable) { lock (_sync) return _msdp.Values.GetValueOrDefault(variable); }

    /// <summary>The cached JSON of one GMCP package, or null when the world has not sent it.</summary>
    public string? TryGetGmcp(string package) { lock (_sync) return _gmcp.Values.GetValueOrDefault(package); }

    /// <summary>Records one decoded MSDP variable. The JSON must already be the serialized value.</summary>
    public bool RecordMsdp(string variable, string json)
    {
        // The same names the event decoder lets through; the worker's own cache accepts exactly these.
        if (variable.Length is 0 or > 128 || variable.Any(char.IsControl) || json.Length == 0) return false;
        lock (_sync) return _msdp.Set(variable, json);
    }

    /// <summary>Records every variable of one MSDP payload and returns how many were kept.
    /// The caller must already have cleared the payload for privacy.</summary>
    public int RecordMsdp(byte[] payload)
    {
        var kept = 0;
        foreach (var (variable, json) in MsdpScriptEvents.DecodeValues(payload))
            if (RecordMsdp(variable, json)) kept++;
        return kept;
    }

    /// <summary>Records one GMCP message of the form "Package.Name {json}". A message with only a package
    /// name is stored as null, exactly as the worker records it. Malformed messages are ignored.</summary>
    public bool RecordGmcp(string message) => RecordGmcp(message, out _);

    /// <summary>As <see cref="RecordGmcp(string)"/>, also naming the package the message was stored under.</summary>
    public bool RecordGmcp(string message, out string package)
    {
        var separator = message.AsSpan().IndexOfAny(" \t\r\n");
        package = separator < 0 ? message : message[..separator];
        if (package.Length == 0 || package.Length > 512 || !Package.IsMatch(package)) return false;
        var body = separator < 0 ? "" : message[(separator + 1)..].Trim();
        string json;
        if (body.Length == 0) json = "null";
        else
        {
            if (body.Length > MaximumValueCharacters) return false;
            try
            {
                using var document = JsonDocument.Parse(body, Parsing);
                json = JsonSerializer.Serialize(document.RootElement);
            }
            catch (JsonException) { return false; }
        }
        lock (_sync) return _gmcp.Set(package, json);
    }

    public void Clear() { lock (_sync) { _gmcp.Clear(); _msdp.Clear(); } }

    /// <summary>The seed a script runtime receives before its source runs: { "gmcp": {...}, "msdp": {...} },
    /// or null when nothing has been received. The engine applies it through the same paths as live events.</summary>
    public string? SeedJson()
    {
        lock (_sync)
        {
            if (_gmcp.Values.Count == 0 && _msdp.Values.Count == 0) return null;
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                foreach (var (name, bucket) in new[] { ("gmcp", _gmcp), ("msdp", _msdp) })
                {
                    writer.WritePropertyName(name);
                    writer.WriteStartObject();
                    foreach (var (key, json) in bucket.Values)
                    {
                        writer.WritePropertyName(key);
                        writer.WriteRawValue(json, skipInputValidation: false);
                    }
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
    }
}
