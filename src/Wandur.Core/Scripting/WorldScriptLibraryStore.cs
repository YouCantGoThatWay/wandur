using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Core.Scripting;

/// <summary>Identifies a script supplied by the world directory rather than written by the user.</summary>
public sealed record ScriptPackInfo(string PackId, string Provenance, int Version, string Description = "")
{
    public const string Generated = "generated";
    public const string Reviewed = "reviewed";
    public static bool IsSupported(string provenance) => provenance is Generated or Reviewed;

    /// <summary>A stable library id, so a refreshed listing updates the same entry instead of adding one.</summary>
    public static Guid IdFor(string worldKey, string packId)
        => new(SHA256.HashData(Encoding.UTF8.GetBytes("wandur-script-pack\n" + worldKey + "\n" + packId)).AsSpan(0, 16));

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(PackId) || PackId.Length > 120 || PackId.Any(char.IsControl)) throw new ArgumentException(L.ScriptPackInvalid);
        if (!IsSupported(Provenance) || Version < 0) throw new ArgumentException(L.ScriptPackInvalid);
        if (Description.Length > 1024 || Description.Any(c => char.IsControl(c) && c is not ('\n' or '\t'))) throw new ArgumentException(L.ScriptPackInvalid);
    }
}

public sealed record WorldScriptDefinition(Guid Id, string Name, string Source, bool Enabled = false, MacroDefinition? Macro = null)
{
    /// <summary>Set when the world directory supplied this script. Hand-written scripts leave it null.</summary>
    public ScriptPackInfo? Pack { get; init; }
    /// <summary>The user's per-pack-script choice to lift the restricted send policy.</summary>
    public bool AllowSend { get; init; }
}

public interface IWorldScriptLibraryStore
{
    IReadOnlyList<WorldScriptDefinition> Load(string worldKey);
    void Upsert(string worldKey, WorldScriptDefinition script);
    void Delete(string worldKey, Guid id);
}

/// <summary>Atomically persists an endpoint's library. Mutations merge one script into the latest file.</summary>
public sealed class WorldScriptLibraryStore(string directory, IWorldScriptStore legacy) : IWorldScriptLibraryStore
{
    public const int MaximumScripts = 64;
    public const int MaximumBytes = 4_194_304;
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.Ordinal);
    private string PathFor(string key) => Path.GetFullPath(Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".scripts.json"));

    public IReadOnlyList<WorldScriptDefinition> Load(string worldKey) => Access(worldKey, path => Read(path, worldKey));

    public void Upsert(string worldKey, WorldScriptDefinition script)
    {
        Validate(script);
        Access(worldKey, path =>
        {
            var scripts = Read(path, worldKey);
            var index = scripts.FindIndex(s => s.Id == script.Id);
            if (index < 0) scripts.Add(script); else scripts[index] = script;
            Write(path, scripts);
            return true;
        });
    }

    public void Delete(string worldKey, Guid id) => Access(worldKey, path =>
    {
        var scripts = Read(path, worldKey);
        scripts.RemoveAll(s => s.Id == id);
        Write(path, scripts);
        return true;
    });

    private T Access<T>(string key, Func<string, T> action)
    {
        var path = PathFor(key);
        lock (Gates.GetOrAdd(path, _ => new()))
        {
            Directory.CreateDirectory(directory);
            // Also serialize separate application instances without holding a stale snapshot.
            using var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return action(path);
        }
    }

    private List<WorldScriptDefinition> Read(string path, string worldKey)
    {
        if (!File.Exists(path))
        {
            var scripts = new List<WorldScriptDefinition> { new(Guid.NewGuid(), L.ScriptDefaultName, legacy.Load(worldKey) ?? ScriptExamples.Starter) };
            Write(path, scripts);
            return scripts;
        }
        if (new FileInfo(path).Length > MaximumBytes) throw new IOException(L.ScriptLibraryTooLarge);
        try
        {
            using var input = File.OpenRead(path);
            var scripts = JsonSerializer.Deserialize<List<WorldScriptDefinition>>(input) ?? throw new IOException(L.ScriptLibraryInvalid);
            if (scripts.Count > MaximumScripts) throw new IOException(L.ScriptLibraryTooLarge);
            if (scripts.Any(s => s is null) || scripts.Select(s => s.Id).Distinct().Count() != scripts.Count) throw new IOException(L.ScriptLibraryInvalid);
            foreach (var script in scripts) Validate(script);
            return scripts;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        { throw new IOException(L.ScriptLibraryInvalid, ex); }
    }

    private static void Validate(WorldScriptDefinition script)
    {
        if (script.Id == Guid.Empty) throw new ArgumentException(L.ScriptLibraryInvalid);
        if (string.IsNullOrWhiteSpace(script.Name) || script.Name.Length > 120 || script.Name.Any(char.IsControl)) throw new ArgumentException(L.ScriptInvalidName);
        if (script.Source is null || Encoding.UTF8.GetByteCount(script.Source) > WorldScriptStore.MaximumBytes) throw new ArgumentException(L.ScriptSourceTooLarge);
        script.Pack?.Validate();
        MacroCompiler.Validate(script);
    }

    private static void Write(string path, List<WorldScriptDefinition> scripts)
    {
        if (scripts.Count > MaximumScripts) throw new ArgumentException(L.ScriptLibraryTooLarge);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(scripts);
        if (bytes.Length > MaximumBytes) throw new ArgumentException(L.ScriptLibraryTooLarge);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
