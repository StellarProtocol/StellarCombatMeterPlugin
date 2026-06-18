using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Stellar.CombatMeter;

internal enum TimelineChannel { Dealt, Healing, Taken }

/// <summary>
/// Sparse per-second time-series for one source across three channels. Buckets are keyed by
/// (eventMs - startMs) / BucketMs. When the highest bucket index would exceed <c>maxBuckets</c>,
/// the bucket width doubles and existing buckets merge pairwise (ZDPS-style coalescing), keeping
/// memory flat for arbitrarily long fights while preserving totals.
/// </summary>
internal sealed class SourceTimeline
{
    private readonly int _maxBuckets;
    private readonly Dictionary<int, long>[] _ch;   // index by (int)TimelineChannel

    public int BucketMs { get; private set; }

    public SourceTimeline(int bucketMs, int maxBuckets)
    {
        BucketMs = bucketMs;
        _maxBuckets = maxBuckets;
        _ch = new[] { new Dictionary<int, long>(), new Dictionary<int, long>(), new Dictionary<int, long>() };
    }

    public void Add(TimelineChannel channel, long atMs, long startMs, long amount)
    {
        var idx = (int)((atMs - startMs) / BucketMs);
        if (idx < 0) idx = 0;
        while (idx >= _maxBuckets) { Coalesce(); idx = (int)((atMs - startMs) / BucketMs); }
        // Single hash lookup on the steady-state path: ref to the existing slot (or a freshly-added 0).
        CollectionsMarshal.GetValueRefOrAddDefault(_ch[(int)channel], idx, out _) += amount;
    }

    private void Coalesce()
    {
        BucketMs *= 2;
        for (int c = 0; c < _ch.Length; c++)
        {
            var merged = new Dictionary<int, long>();
            foreach (var (k, v) in _ch[c])
            {
                var nk = k / 2;
                merged[nk] = merged.TryGetValue(nk, out var cur) ? cur + v : v;
            }
            _ch[c] = merged;
        }
    }

    public long[] Freeze(TimelineChannel channel)
    {
        var map = _ch[(int)channel];
        int len = 0;
        foreach (var k in map.Keys) if (k + 1 > len) len = k + 1;
        var arr = new long[len];
        foreach (var (k, v) in map) arr[k] = v;
        return arr;
    }
}
