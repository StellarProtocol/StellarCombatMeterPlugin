using System;

namespace Stellar.CombatMeter.Replay;

/// <summary>One captured sample: ms relative to combat start + raw world floats. Encoded off-thread later.</summary>
internal readonly struct PositionSample
{
    public readonly int Ms;
    public readonly float X, Y, Z, Yaw;

    public PositionSample(int ms, float x, float y, float z, float yaw)
    {
        Ms = ms; X = x; Y = y; Z = z; Yaw = yaw;
    }
}

/// <summary>
/// Bounded per-entity sample buffer. When it would exceed <c>maxSamples</c> it coalesces —
/// keeps every other sample and doubles the effective stride — so an arbitrarily long fight
/// stays memory-bounded while the whole fight remains represented (coarser). Mirrors SourceTimeline.
/// Main-thread only; Add is amortized O(1) (coalesce is a rare in-place compaction, no per-Add allocation).
/// </summary>
internal sealed class PositionTrack
{
    private readonly int _maxSamples;
    private PositionSample[]? _buf;   // allocated on first Add — a noted-but-never-sampled
                                       // entity (gate races, cap interplay) costs ~nothing.
    private int _count;

    /// <summary>Current effective interval between retained samples (ms). Doubles on each coalesce.</summary>
    public int StrideMs { get; private set; }

    public PositionTrack(int maxSamples)
    {
        if (maxSamples < 2) throw new System.ArgumentOutOfRangeException(nameof(maxSamples), "must be >= 2");
        _maxSamples = maxSamples;
        StrideMs = 500;
    }

    /// <summary>Number of samples currently retained.</summary>
    public int Count => _count;

    /// <summary>Append a sample, allocating the buffer lazily and coalescing first if it is full.</summary>
    public void Add(in PositionSample s)
    {
        _buf ??= new PositionSample[_maxSamples];
        if (_count >= _maxSamples) Coalesce();
        _buf[_count++] = s;
    }

    private void Coalesce()
    {
        var buf = _buf!;   // only called from Add after _buf is ensured non-null
        var w = 0;
        for (var r = 0; r < _count; r += 2)
            buf[w++] = buf[r];   // keep even indices in-place
        _count = w;
        StrideMs *= 2;
    }

    /// <summary>Frees the leading samples whose <see cref="PositionSample.Ms"/> is &lt;= <paramref name="ms"/>
    /// (they've been uploaded in a completed window) and compacts the buffer in place; returns how many
    /// were dropped. Samples are appended in ascending Ms and coalescing preserves that order, so the
    /// kept tail is always a contiguous suffix. Lifecycle op (runs at archive, not on the sample path);
    /// <see cref="StrideMs"/> is unchanged — the retained samples keep their spacing.</summary>
    public int TrimBelow(long ms)
    {
        if (_buf is null || _count == 0) return 0;
        var drop = 0;
        while (drop < _count && _buf[drop].Ms <= ms) drop++;
        if (drop == 0) return 0;
        var remaining = _count - drop;
        if (remaining > 0) Array.Copy(_buf, drop, _buf, 0, remaining);
        _count = remaining;
        return drop;
    }

    /// <summary>Returns a snapshot of all currently retained samples in order.</summary>
    public PositionSample[] Snapshot()
    {
        if (_buf is null || _count == 0) return Array.Empty<PositionSample>();
        var result = new PositionSample[_count];
        Array.Copy(_buf, result, _count);
        return result;
    }
}
