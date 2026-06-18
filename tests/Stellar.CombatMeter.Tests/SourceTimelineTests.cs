using Stellar.CombatMeter;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public sealed class SourceTimelineTests
{
    [Fact]
    public void Add_buckets_by_second_relative_to_start()
    {
        var t = new SourceTimeline(bucketMs: 1000, maxBuckets: 600);
        t.Add(channel: TimelineChannel.Dealt, atMs: 0, startMs: 0, amount: 100);
        t.Add(TimelineChannel.Dealt, atMs: 500,  startMs: 0, amount: 50);   // same bucket 0
        t.Add(TimelineChannel.Dealt, atMs: 1500, startMs: 0, amount: 40);   // bucket 1
        var arr = t.Freeze(TimelineChannel.Dealt);
        Assert.Equal(150, arr[0]);
        Assert.Equal(40,  arr[1]);
    }

    [Fact]
    public void Channels_are_independent()
    {
        var t = new SourceTimeline(1000, 600);
        t.Add(TimelineChannel.Dealt,   0, 0, 100);
        t.Add(TimelineChannel.Healing, 0, 0, 30);
        t.Add(TimelineChannel.Taken,   0, 0, 70);
        Assert.Equal(100, t.Freeze(TimelineChannel.Dealt)[0]);
        Assert.Equal(30,  t.Freeze(TimelineChannel.Healing)[0]);
        Assert.Equal(70,  t.Freeze(TimelineChannel.Taken)[0]);
    }

    [Fact]
    public void Coalesces_when_exceeding_maxBuckets()
    {
        var t = new SourceTimeline(bucketMs: 1000, maxBuckets: 4);
        // 8 seconds of 10/s -> 8 buckets > max 4, must coalesce to 2000ms buckets (4 buckets of 20).
        for (int sec = 0; sec < 8; sec++) t.Add(TimelineChannel.Dealt, sec * 1000, 0, 10);
        var arr = t.Freeze(TimelineChannel.Dealt);
        Assert.True(arr.Length <= 4);
        Assert.Equal(2000, t.BucketMs);           // widened once
        Assert.Equal(80, Sum(arr));               // total preserved
    }

    [Fact]
    public void Coalesces_repeatedly_when_data_keeps_growing()
    {
        var t = new SourceTimeline(bucketMs: 1000, maxBuckets: 4);
        // 16 seconds of 10/s. Index 15 forces two doublings: 1000 -> 2000 (idx 7, still >=4) -> 4000 (idx 3).
        for (int sec = 0; sec < 16; sec++) t.Add(TimelineChannel.Dealt, sec * 1000, 0, 10);
        var arr = t.Freeze(TimelineChannel.Dealt);
        Assert.Equal(4000, t.BucketMs);           // widened twice (1000 -> 2000 -> 4000)
        Assert.True(arr.Length <= 4);
        Assert.Equal(160, Sum(arr));              // total preserved across both coalesces
    }

    [Fact]
    public void Negative_index_event_before_start_clamps_to_bucket_zero()
    {
        var t = new SourceTimeline(bucketMs: 1000, maxBuckets: 600);
        // atMs (0) < startMs (500): raw idx would be negative; Add clamps to bucket 0 (no throw, no negative).
        t.Add(TimelineChannel.Dealt, atMs: 0, startMs: 500, amount: 42);
        var arr = t.Freeze(TimelineChannel.Dealt);
        Assert.Equal(42, arr[0]);
        Assert.Single(arr);
    }

    // Latch-fix regression (Item 3). The Plugin.OnCombatEvent gating (`if (_combatActive)` in
    // CaptureHeal/CaptureTaken) is NOT reachable in this Unity-free host — constructing Plugin
    // requires a live IPluginServices (Log/Theme/Windows/Hotkeys + Unity GameObjects). The fix
    // hoists EnsureCombatStarted so the FIRST event of any channel establishes _combatStartMs,
    // making those guards satisfied for the opening heal/taken event. The reachable seam is the
    // timeline write the fix unblocks: a heal/taken event whose timestamp EQUALS the just-latched
    // combat start lands in bucket 0 (captured), exactly as the dealt channel always did.
    [Fact]
    public void Opening_heal_or_taken_event_at_combat_start_lands_in_bucket_zero()
    {
        const long start = 10_000;   // the instant the latch establishes (first event timestamp)
        var heal = new SourceTimeline(1000, 600);
        heal.Add(TimelineChannel.Healing, atMs: start, startMs: start, amount: 300);
        Assert.Equal(300, heal.Freeze(TimelineChannel.Healing)[0]);

        var taken = new SourceTimeline(1000, 600);
        taken.Add(TimelineChannel.Taken, atMs: start, startMs: start, amount: 175);
        Assert.Equal(175, taken.Freeze(TimelineChannel.Taken)[0]);
    }

    private static long Sum(long[] a) { long s = 0; foreach (var x in a) s += x; return s; }
}
