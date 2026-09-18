using System.Security.Cryptography;
using System.Text;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Core.Scripting;

/// <summary>Source files only; loading never executes code.</summary>
public sealed class WorldScriptStore(string directory) : IWorldScriptStore
{
    public const int MaximumBytes = 262_144;
    private readonly object _gate = new();
    private string PathFor(string key) => Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".js");

    public string? Load(string worldKey)
    {
        lock (_gate)
        {
            var path = PathFor(worldKey);
            if (!File.Exists(path)) return null;
            if (new FileInfo(path).Length > MaximumBytes) throw new IOException(L.ScriptSourceTooLarge);
            return File.ReadAllText(path);
        }
    }

    public void Save(string worldKey, string source)
    {
        if (Encoding.UTF8.GetByteCount(source) > MaximumBytes) throw new ArgumentException(L.ScriptSourceTooLarge, nameof(source));
        lock (_gate)
        {
            Directory.CreateDirectory(directory);
            var path = PathFor(worldKey);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, source, new UTF8Encoding(false));
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
