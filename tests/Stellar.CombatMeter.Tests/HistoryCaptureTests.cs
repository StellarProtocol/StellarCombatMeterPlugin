using Stellar.Abstractions.Domain;
using Stellar.CombatMeter;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public sealed class HistoryCaptureTests
{
    [Fact]
    public void Frozen_series_are_per_source_per_channel()
    {
        var entry = new Plugin.EncounterHistoryEntry();
        var id = new EntityId(0x0000_0001_0000_0280L);
        entry.Series[id] = new SourceSeries
        {
            BucketMs = 1000,
            Dealt    = new long[] { 100, 200, 150 },
            Healing  = new long[] { 0, 0, 0 },
            Taken    = new long[] { 50, 0, 25 },
        };
        Assert.Equal(3, entry.Series[id].Dealt.Length);
        Assert.Equal(200, entry.Series[id].Dealt[1]);
        Assert.Equal(25,  entry.Series[id].Taken[2]);
    }

    [Fact]
    public void Freeze_round_trips_all_three_channels_with_sparse_trailing_gap()
    {
        var t = new SourceTimeline(bucketMs: 1000, maxBuckets: 600);
        // Dealt at 0s and 1s; a sparse trailing gap then a hit at 4s (buckets 2 & 3 stay empty).
        t.Add(TimelineChannel.Dealt, atMs: 0,    startMs: 0, amount: 100);
        t.Add(TimelineChannel.Dealt, atMs: 1000, startMs: 0, amount: 200);
        t.Add(TimelineChannel.Dealt, atMs: 4000, startMs: 0, amount: 50);
        t.Add(TimelineChannel.Healing, atMs: 1000, startMs: 0, amount: 30);
        t.Add(TimelineChannel.Taken,   atMs: 2000, startMs: 0, amount: 70);

        // Mirror the real archive path: FreezeTimelines() builds SourceSeries from these three Freeze calls,
        // incl. the Taken channel via the real SourceTimeline.Freeze(TimelineChannel.Taken).
        var frozen = new SourceSeries
        {
            BucketMs = t.BucketMs,
            Dealt    = t.Freeze(TimelineChannel.Dealt),
            Healing  = t.Freeze(TimelineChannel.Healing),
            Taken    = t.Freeze(TimelineChannel.Taken),
        };

        Assert.Equal(1000, frozen.BucketMs);
        // Length spans up to the highest occupied index (3 -> length 4), interior gaps are zero.
        Assert.Equal(5, frozen.Dealt.Length);
        Assert.Equal(new long[] { 100, 200, 0, 0, 50 }, frozen.Dealt);
        // Healing's highest index is 1 -> length 2.
        Assert.Equal(new long[] { 0, 30 }, frozen.Healing);
        // Taken's highest index is 2 -> length 3.
        Assert.Equal(new long[] { 0, 0, 70 }, frozen.Taken);
    }

    [Fact]
    public void Frozen_arrays_are_isolated_from_subsequent_live_mutation()
    {
        var t = new SourceTimeline(1000, 600);
        t.Add(TimelineChannel.Dealt, atMs: 0,    startMs: 0, amount: 100);
        t.Add(TimelineChannel.Taken, atMs: 0,    startMs: 0, amount: 40);

        var frozenDealt = t.Freeze(TimelineChannel.Dealt);
        var frozenTaken = t.Freeze(TimelineChannel.Taken);

        // After freezing, the live timeline keeps accruing (next encounter ticks before Clear()).
        t.Add(TimelineChannel.Dealt, atMs: 0,    startMs: 0, amount: 999);
        t.Add(TimelineChannel.Dealt, atMs: 5000, startMs: 0, amount: 999);
        t.Add(TimelineChannel.Taken, atMs: 0,    startMs: 0, amount: 999);

        // Freeze allocates fresh arrays (deep-copy semantics), so the archived snapshot is untouched.
        Assert.Equal(new long[] { 100 }, frozenDealt);
        Assert.Equal(new long[] { 40 },  frozenTaken);
    }

    [Fact]
    public void ComputeUptime_is_active_span_over_duration()
    {
        Assert.Equal(0.5f, Plugin.ComputeUptime(firstHitMs: 0, lastHitMs: 30000, durationMs: 60000));
    }

    [Fact]
    public void ComputeUptime_zero_duration_is_zero()
    {
        Assert.Equal(0f, Plugin.ComputeUptime(0, 30000, 0));
    }

    [Fact]
    public void ComputeUptime_clamps_to_one_when_span_exceeds_duration()
    {
        Assert.Equal(1f, Plugin.ComputeUptime(0, 90000, 60000));
    }

    [Fact]
    public void ComputeUptime_zero_when_no_active_span()
    {
        // lastHit <= firstHit (no progress) → 0, regardless of duration.
        Assert.Equal(0f, Plugin.ComputeUptime(5000, 5000, 60000));
    }
}
