using Wandur.Core.Mapping;

namespace Wandur.Desktop.Tests;

internal sealed class MemoryRoomMapStore : IRoomMapStore
{
    private readonly Dictionary<(string, int), MapSnapshot> _maps = [];
    public MapSnapshot? Load(string host, int port) => _maps.GetValueOrDefault((host.ToLowerInvariant(), port));
    public void Save(string host, int port, MapSnapshot snapshot) => _maps[(host.ToLowerInvariant(), port)] = snapshot;
}
