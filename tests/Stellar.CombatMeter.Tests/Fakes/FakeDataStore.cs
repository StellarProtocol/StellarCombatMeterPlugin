using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter.Tests.Fakes;

/// <summary>In-memory <see cref="IPluginDataStore"/> fake for spool tests. Not a full IO simulation —
/// just enough surface (Write/Read/Delete/List) plus a write counter to assert blob-write counts.</summary>
public sealed class FakeDataStore : IPluginDataStore
{
    private readonly ConcurrentDictionary<string, byte[]> _files = new();

    public int Writes;

    public void Write(string name, byte[] data)
    {
        _files[name] = data;
        System.Threading.Interlocked.Increment(ref Writes);
    }

    public byte[]? Read(string name) => _files.TryGetValue(name, out var data) ? data : null;

    public void Delete(string name) => _files.TryRemove(name, out _);

    public IReadOnlyList<string> List(string? prefix = null)
        => _files.Keys
            .Where(n => prefix == null || n.StartsWith(prefix, System.StringComparison.Ordinal))
            .OrderBy(n => n, System.StringComparer.Ordinal)
            .ToArray();
}
