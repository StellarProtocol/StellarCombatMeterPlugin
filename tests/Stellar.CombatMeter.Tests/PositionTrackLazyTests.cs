using System;
using Stellar.CombatMeter.Replay;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public class PositionTrackLazyTests
{
    [Fact]
    public void New_track_retains_nothing_and_snapshots_empty()
    {
        var t = new PositionTrack(3600);
        Assert.Equal(0, t.Count);
        Assert.Empty(t.Snapshot());
    }

    [Fact]
    public void Construction_does_not_allocate_the_sample_buffer()
    {
        // A 3600-sample buffer is ~72 KB (20 B/sample). Lazy construction must
        // allocate far less than that; the 8 KB bound keeps the assertion robust
        // to object-header / test-infra noise while failing hard on an eager buffer.
        var before = GC.GetAllocatedBytesForCurrentThread();
        var t = new PositionTrack(3600);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 8_192, $"expected lazy construction, allocated {allocated} bytes");
        GC.KeepAlive(t);
    }

    [Fact]
    public void Add_then_snapshot_roundtrips()
    {
        var t = new PositionTrack(4);
        t.Add(new PositionSample(0, 1f, 2f, 3f, 90f));
        t.Add(new PositionSample(500, 2f, 2f, 3f, 90f));
        var s = t.Snapshot();
        Assert.Equal(2, s.Length);
        Assert.Equal(0, s[0].Ms);
        Assert.Equal(500, s[1].Ms);
    }
}
