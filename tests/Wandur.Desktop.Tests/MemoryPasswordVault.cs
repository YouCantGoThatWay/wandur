using Wandur.Desktop.Security;

namespace Wandur.Desktop.Tests;

internal sealed class MemoryPasswordVault : IPasswordVault
{
    private readonly Dictionary<string, string> _values = [];
    public int Count => _values.Count;
    public string Description => "test credential store";
    public Task<string?> ReadAsync(string key) => Task.FromResult(_values.GetValueOrDefault(key));
    public Task WriteAsync(string key, string password) { _values[key] = password; return Task.CompletedTask; }
    public Task DeleteAsync(string key) { _values.Remove(key); return Task.CompletedTask; }
}
