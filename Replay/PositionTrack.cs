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
    private readonly PositionSample[] _buf;
    private int _count;

    /// <summary>Current effective interval between retained samples (ms). Doubles on each coalesce.</summary>
    public int StrideMs { get; private set; }

    public PositionTrack(int maxSamples)
    {
        if (maxSamples < 2) throw new System.ArgumentOutOfRangeException(nameof(maxSamples), "must be >= 2");
        _maxSamples = maxSamples;
        _buf = new PositionSample[maxSamples];
        StrideMs = 500;
    }

    /// <summary>Number of samples currently retained.</summary>
    public int Count => _count;

    /// <summary>Append a sample, coalescing first if the buffer is full.</summary>
    public void Add(in PositionSample s)
    {
        if (_count >= _maxSamples) Coalesce();
        _buf[_count++] = s;
    }

    private void Coalesce()
    {
        var w = 0;
        for (var r = 0; r < _count; r += 2)
            _buf[w++] = _buf[r];   // keep even indices in-place
        _count = w;
        StrideMs *= 2;
    }

    /// <summary>Returns a snapshot of all currently retained samples in order.</summary>
    public PositionSample[] Snapshot()
    {
        var result = new PositionSample[_count];
        Array.Copy(_buf, result, _count);
        return result;
    }
}
