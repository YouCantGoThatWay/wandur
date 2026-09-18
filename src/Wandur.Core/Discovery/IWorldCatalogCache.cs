namespace Wandur.Core.Discovery;

public interface IWorldCatalogCache
{
    string? ReadSnapshot();
    void WriteSnapshot(string json);
    byte[]? ReadArtwork(string key);
    void WriteArtwork(string key, byte[] data);
}

/// <summary>Legacy file storage retained for interchange and existing embedders.</summary>
public sealed class FileWorldCatalogCache(string path) : IWorldCatalogCache
{
    public string? ReadSnapshot() => File.Exists(path) ? File.ReadAllText(path) : null;
    public void WriteSnapshot(string json) => Write(path, System.Text.Encoding.UTF8.GetBytes(json));
    private string ArtPath(string key) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "directory-art", key + ".png");
    public byte[]? ReadArtwork(string key) => File.Exists(ArtPath(key)) ? File.ReadAllBytes(ArtPath(key)) : null;
    public void WriteArtwork(string key, byte[] data) => Write(ArtPath(key), data);
    private static void Write(string destination, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, destination, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
