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

    /// <summary>Makes every <see cref="Write"/> throw — the real store never does (it swallows + logs),
    /// so this stands in for a serialization/gzip fault on the spool's background write task.</summary>
    public bool ThrowOnWrite;

    public void Write(string name, byte[] data)
    {
        System.Threading.Interlocked.Increment(ref Writes);
        if (ThrowOnWrite) throw new System.IO.IOException("fake write fault");
        _files[name] = data;
    }

    public byte[]? Read(string name) => _files.TryGetValue(name, out var data) ? data : null;

    public void Delete(string name) => _files.TryRemove(name, out _);

    public IReadOnlyList<string> List(string? prefix = null)
        => _files.Keys
            .Where(n => prefix == null || n.StartsWith(prefix, System.StringComparison.Ordinal))
            .OrderBy(n => n, System.StringComparer.Ordinal)
            .ToArray();
}
