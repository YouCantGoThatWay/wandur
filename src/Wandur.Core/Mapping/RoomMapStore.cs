using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Wandur.Core.Mapping;

public interface IRoomMapStore
{
    MapSnapshot? Load(string host, int port);
    void Save(string host, int port, MapSnapshot snapshot);
    /// <summary>Rooms of this world whose observed name or description matches every search term. See <see cref="RoomSearch"/>.</summary>
    IReadOnlyList<MapRoom> SearchRooms(string host, int port, string query) =>
        Load(host, port) is { } snapshot ? RoomSearch.Search(snapshot.Rooms, query) : [];
}

public sealed class RoomMapStore(string directory) : IRoomMapStore
{
    private static readonly object Gate = new();
    private string PathFor(string host, int port) => Path.Combine(directory,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(host.Trim().ToLowerInvariant() + ":" + port))) + ".json");

    public MapSnapshot? Load(string host, int port)
    {
        var path = PathFor(host, port);
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 16_777_216) return null;
            return MapFileFormat.Deserialize(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or FormatException) { return null; }
    }

    public void Save(string host, int port, MapSnapshot snapshot)
    {
        lock (Gate)
        {
            // Entity revisions and durable deletion records stop an older tab from reviving removed data.
            if (Load(host, port) is { } previous)
            {
                snapshot = MapSnapshotMerge.Combine(previous, snapshot);
            }
            SaveCore(host, port, snapshot);
        }
    }

    private void SaveCore(string host, int port, MapSnapshot snapshot)
    {
        Directory.CreateDirectory(directory);
        var path = PathFor(host, port);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, MapFileFormat.Serialize(snapshot with { CurrentRoomId = null, CandidateRoomIds = [], State = MapTrackingState.Unknown }));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
