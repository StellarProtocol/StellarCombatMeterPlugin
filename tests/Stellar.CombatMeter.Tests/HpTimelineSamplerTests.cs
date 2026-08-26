using System.Collections.Generic;
using System.Linq;
using Stellar.CombatMeter.Replay;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public class HpTimelineSamplerTests
{
    [Fact]
    public void SamplesEveryFiveHundredMs_SharedCadence()
    {
        var s = new HpTimelineSampler(_ => (50, 100));
        s.Track(1, ms0: 0);
        for (var i = 0; i < 12; i++) s.Tick(250f); // 3000 ms => 6 samples
        var t = s.GetTrack(1);
        Assert.NotNull(t);
        Assert.Equal(6, t!.Pct.Count);
        Assert.All(t.Pct, p => Assert.Equal(50, p));
    }

    [Fact]
    public void ClampsPctAndAppendsSentinelWhenMaxHpUnknown()
    {
        // L2 fix (2026-08-26 raid-bosshp-capture-design): an unusable tick (maxHp<=0) APPENDS the
        // sentinel instead of skipping — Ms0 stays the FIRST tracked time and every later sample's
        // grid position (Ms0 + i*cadence) still names the real elapsed time.
        var reads = new Queue<(long, long)>(new[] { (150L, 100L), (0L, 0L), (-5L, 100L) });
        var s = new HpTimelineSampler(_ => reads.Dequeue());
        s.Track(7, ms0: 1000);
        s.Tick(500f); // 150/100 -> clamped 100
        s.Tick(500f); // maxHp 0 -> sentinel (-1), grid position preserved
        s.Tick(500f); // -5/100 -> clamped 0
        var t = s.GetTrack(7)!;
        Assert.Equal(new[] { 100, HpTimelineSampler.SentinelPct, 0 }, t.Pct);
        Assert.Equal(1000, t.Ms0);
    }

    [Fact]
    public void SentinelGrid_KeepsGridPositionHonest_AcrossASkipRun()
    {
        // A run of several unusable ticks in a row must each still occupy their own grid slot — the
        // whole point of the L2 fix is that a LATER real sample's Ms0+i*cadence position matches when
        // it actually happened, not an earlier position shifted by the skipped ticks.
        var reads = new Queue<(long, long)>(new[]
        {
            (50L, 100L),   // real: 50%
            (0L, 0L), (0L, 0L), (0L, 0L),   // three unusable ticks in a row
            (75L, 100L),   // real: 75%, at grid index 4 (Ms0 + 4*500 = 2000)
        });
        var s = new HpTimelineSampler(_ => reads.Dequeue());
        s.Track(1, ms0: 0);
        for (var i = 0; i < 5; i++) s.Tick(500f);
        var t = s.GetTrack(1)!;
        Assert.Equal(new[] { 50, -1, -1, -1, 75 }, t.Pct);
        // The final real sample's grid time is Ms0 + 4*500 = 2000 — exactly when it was captured,
        // not shifted early by the three skipped ticks (the pre-fix bug).
        Assert.Equal(2000, t.Ms0 + 4L * HpTimelineSampler.SampleIntervalMs);
    }

    [Fact]
    public void SentinelGrid_TrimBelow_CountsSentinelsAsOrdinaryGridSlots()
    {
        // TrimBelow's math is purely positional (Ms0 + i*cadence), so a sentinel occupies a grid slot
        // exactly like a real sample — this pins that a run of sentinels doesn't confuse the trim.
        var reads = new Queue<(long, long)>(new[]
        {
            (50L, 100L), (0L, 0L), (0L, 0L), (60L, 100L),
        });
        var s = new HpTimelineSampler(_ => reads.Dequeue());
        s.Track(1, ms0: 0);
        for (var i = 0; i < 4; i++) s.Tick(500f);   // grid 0,500,1000,1500 -> [50,-1,-1,60]
        s.TrimBelow(1000, cadenceMs: 500);          // drop grid <= 1000 (indices 0,1,2 incl. 2 sentinels)
        var t = s.GetTrack(1)!;
        Assert.Equal(1500, t.Ms0);
        Assert.Equal(new[] { 60 }, t.Pct);
    }

    // ── Catch-up loop (2026-08-26 grid-drift fix, owner-measured gridDriftMs=2970 on a raid segment)
    //    A dtMs spanning multiple 500 ms intervals (loading-screen hitches run at 1-10 Hz) must
    //    append ONE slot per owed interval, not a single slot with the remainder silently zeroed —
    //    the old single-step drain left every LATER sample's grid position labeled earlier than it
    //    actually happened. ──

    [Fact]
    public void CatchUp_dt1600ms_appendsThreeSlots_GridStaysTrue()
    {
        var s = new HpTimelineSampler(_ => (50, 100));
        s.Track(1, ms0: 0);
        s.Tick(1600f);   // 1600/500 = 3 whole intervals, 100ms remainder carries in the accumulator
        Assert.Equal(new[] { 50, 50, 50 }, s.GetTrack(1)!.Pct);

        // The 100 ms remainder is honored on the NEXT tick — 400 more ms completes the 4th slot at
        // its true grid time (2000 ms), proving the accumulator wasn't just reset to 0.
        s.Tick(400f);
        Assert.Equal(new[] { 50, 50, 50, 50 }, s.GetTrack(1)!.Pct);
    }

    [Fact]
    public void CatchUp_tenSecondHitch_appendsExactlyTwenty()
    {
        var s = new HpTimelineSampler(_ => (75, 100));
        s.Track(1, ms0: 0);
        s.Tick(10_000f);   // exactly MaxCatchUpSlotsPerTick (20) * 500ms — no sentinel drain needed
        var t = s.GetTrack(1)!;
        Assert.Equal(20, t.Pct.Count);
        Assert.All(t.Pct, p => Assert.Equal(75, p));
    }

    [Fact]
    public void CatchUp_valuesRepeatAcrossSlots_OneReadPerTickCall()
    {
        // The reader is called ONCE per Tick call regardless of how many slots the hitch spans — a
        // Queue that only has ONE entry proves this (a second read attempt would throw).
        var reads = new Queue<(long, long)>(new[] { (33L, 100L) });
        var s = new HpTimelineSampler(_ => reads.Dequeue());
        s.Track(1, ms0: 0);
        s.Tick(2000f);   // 4 catch-up slots from ONE read
        Assert.Equal(new[] { 33, 33, 33, 33 }, s.GetTrack(1)!.Pct);
        Assert.Empty(reads);
    }

    [Fact]
    public void CatchUp_unusableRead_appendsSentinelForEveryOwedSlot()
    {
        var s = new HpTimelineSampler(_ => (0, 0));   // maxHp unknown — unusable every slot
        s.Track(1, ms0: 0);
        s.Tick(1500f);   // 3 owed slots, all unusable
        Assert.Equal(new[] { -1, -1, -1 }, s.GetTrack(1)!.Pct);
    }

    [Fact]
    public void CatchUp_beyondTwentySlots_drainsTheRemainderAsSentinels()
    {
        // 15s hitch = 30 owed slots: the first MaxCatchUpSlotsPerTick (20) repeat the real read, the
        // remaining 10 (past the 10s real-read bound, within the 20s sentinel-drain bound) are
        // sentinels — never more real reads of an already-stale frozen value.
        var s = new HpTimelineSampler(_ => (60, 100));
        s.Track(1, ms0: 0);
        s.Tick(15_000f);
        var t = s.GetTrack(1)!;
        Assert.Equal(30, t.Pct.Count);
        Assert.All(t.Pct.Take(20), p => Assert.Equal(60, p));
        Assert.All(t.Pct.Skip(20), p => Assert.Equal(-1, p));
    }

    [Fact]
    public void CatchUp_pathologicalDt_neverSpins_AndDropsTheUnrepresentableRemainder()
    {
        // A truly extreme dtMs (100s = 200 owed slots) must not make Tick loop hundreds of times —
        // it appends at most MaxCatchUpSlotsPerTick + MaxSentinelDrainSlotsPerTick (40) slots and
        // resets the accumulator, dropping the unrepresentable remainder rather than spinning.
        var s = new HpTimelineSampler(_ => (10, 100));
        s.Track(1, ms0: 0);
        s.Tick(100_000f);
        Assert.Equal(
            HpTimelineSampler.MaxCatchUpSlotsPerTick + HpTimelineSampler.MaxSentinelDrainSlotsPerTick,
            s.GetTrack(1)!.Pct.Count);

        // The accumulator was reset (not left mid-hitch), so a normal-sized next tick samples cleanly
        // instead of immediately re-triggering another huge catch-up burst.
        s.Tick(500f);
        Assert.Equal(
            HpTimelineSampler.MaxCatchUpSlotsPerTick + HpTimelineSampler.MaxSentinelDrainSlotsPerTick + 1,
            s.GetTrack(1)!.Pct.Count);
    }

    [Fact]
    public void CatchUp_respectsMaxSamplesPerEntity_AcrossABurst()
    {
        var s = new HpTimelineSampler(_ => (5, 100));
        s.Track(1, ms0: 0);
        for (var i = 0; i < HpTimelineSampler.MaxSamplesPerEntity - 5; i++) s.Tick(500f);   // fill to 5 below cap
        s.Tick(10_000f);   // a 20-slot catch-up burst would overshoot the cap by 15 without the guard
        Assert.Equal(HpTimelineSampler.MaxSamplesPerEntity, s.GetTrack(1)!.Pct.Count);
    }

    [Fact]
    public void MarkDead_IgnoresTrailingSentinels_NoOpWhenRealLastSampleIsZero()
    {
        // A real 0% was already recorded, then a couple of unusable ticks (sentinels) landed after
        // it — MarkDead must look PAST the sentinels to see the real terminal 0 and stay idempotent
        // instead of appending a redundant duplicate.
        var reads = new Queue<(long, long)>(new[] { (0L, 100L), (0L, 0L), (0L, 0L) });
        var s = new HpTimelineSampler(_ => reads.Dequeue());
        s.Track(7, ms0: 0);
        s.Tick(500f);   // real 0%
        s.Tick(500f);   // sentinel
        s.Tick(500f);   // sentinel
        s.MarkDead(7, 1500);
        Assert.Equal(new[] { 0, -1, -1 }, s.GetTrack(7)!.Pct);   // no duplicate 0 appended
    }

    [Fact]
    public void MarkDead_AppendsZero_AfterTrailingSentinels_WhenNoRealZeroYet()
    {
        // No real 0% has landed — only sentinels trail the last real (non-zero) sample. A death
        // observed after a run of gaps must still append the terminal 0.
        var reads = new Queue<(long, long)>(new[] { (40L, 100L), (0L, 0L), (0L, 0L) });
        var s = new HpTimelineSampler(_ => reads.Dequeue());
        s.Track(7, ms0: 0);
        s.Tick(500f);   // real 40%
        s.Tick(500f);   // sentinel
        s.Tick(500f);   // sentinel
        s.MarkDead(7, 1500);
        Assert.Equal(new[] { 40, -1, -1, 0 }, s.GetTrack(7)!.Pct);
    }

    [Fact]
    public void TrackIsIdempotent_AndNegativeMs0ClampsToZero()
    {
        var s = new HpTimelineSampler(_ => (1, 2));
        s.Track(3, ms0: -50);
        s.Track(3, ms0: 9999); // ignored — already tracked
        s.Tick(500f);
        Assert.Equal(0, s.GetTrack(3)!.Ms0);
    }

    [Fact]
    public void SamplesAllTrackedEntitiesOnTheSameTick()
    {
        var s = new HpTimelineSampler(id => (id == 1 ? 80 : 20, 100));
        s.Track(1, 0);
        s.Track(2, 0);
        s.Tick(500f);
        Assert.Equal(new[] { 80 }, s.GetTrack(1)!.Pct);
        Assert.Equal(new[] { 20 }, s.GetTrack(2)!.Pct);
    }

    [Fact]
    public void GetTrackReturnsNullWithoutSamples_AndResetClears()
    {
        var s = new HpTimelineSampler(_ => (1, 1));
        s.Track(1, 0);
        Assert.Null(s.GetTrack(1));   // no Tick yet
        s.Tick(500f);
        Assert.NotNull(s.GetTrack(1));
        s.Reset();
        Assert.Null(s.GetTrack(1));
        Assert.Empty(s.TrackedIds);
    }

    [Fact]
    public void Reset_ClearsTracksAndSamples()
    {
        var s = new HpTimelineSampler(_ => (50, 100));
        s.Track(1, ms0: 0);
        s.Track(2, ms0: 0);
        s.Tick(500f);
        Assert.NotNull(s.GetTrack(1));
        Assert.NotNull(s.GetTrack(2));

        s.Reset();

        Assert.Null(s.GetTrack(1));
        Assert.Null(s.GetTrack(2));
        Assert.Empty(s.TrackedIds);

        // Post-reset the sampler is usable again — re-tracking + ticking produces fresh samples.
        s.Track(1, ms0: 0);
        s.Tick(500f);
        Assert.NotNull(s.GetTrack(1));
    }

    [Fact]
    public void Reset_OnFreshSampler_DoesNotThrow()
    {
        var s = new HpTimelineSampler(_ => (1, 1));
        var ex = Record.Exception(() => s.Reset());
        Assert.Null(ex);
        Assert.Empty(s.TrackedIds);
    }

    [Fact]
    public void StopsAtMaxSamplesPerEntity()
    {
        var s = new HpTimelineSampler(_ => (1, 1));
        s.Track(1, 0);
        for (var i = 0; i < HpTimelineSampler.MaxSamplesPerEntity + 25; i++) s.Tick(500f);
        Assert.Equal(HpTimelineSampler.MaxSamplesPerEntity, s.GetTrack(1)!.Pct.Count);
    }

    [Fact]
    public void MarkDead_IsExempt_FromTheMaxSamplesCap()
    {
        // I5 (2026-08-26 full-chain review): with the L2 sentinel-grid fix a long-gappy track can
        // legitimately sit AT the cap (every tick unusable -> sentinel). MarkDead appends at most one
        // terminating sample (idempotency guarantees that), so the cap that exists to bound unbounded
        // per-tick growth must never be the reason the terminal death 0% goes missing.
        var s = new HpTimelineSampler(_ => (0, 0));   // every tick unusable -> sentinel
        s.Track(1, 0);
        for (var i = 0; i < HpTimelineSampler.MaxSamplesPerEntity; i++) s.Tick(500f);
        Assert.Equal(HpTimelineSampler.MaxSamplesPerEntity, s.GetTrack(1)!.Pct.Count);   // at the cap

        s.MarkDead(1, 999_999);

        var track = s.GetTrack(1)!;
        Assert.Equal(HpTimelineSampler.MaxSamplesPerEntity + 1, track.Pct.Count);   // exceeds the cap by one
        Assert.Equal(0, track.Pct[^1]);
    }

    [Fact]
    public void MarkDead_appends_a_final_zero_sample()
    {
        long hp = 50, maxHp = 100;
        var s = new HpTimelineSampler(_ => (hp, maxHp));
        s.Track(7, 0);
        s.Tick(500f);                // one 50% sample
        s.MarkDead(7, 1000);         // boss dies
        var track = s.GetTrack(7)!;
        Assert.Equal(0, track.Pct[^1]);       // last sample is 0
        Assert.Equal(2, track.Pct.Count);     // 50 then 0
    }

    [Fact]
    public void MarkDead_is_idempotent_and_ignores_untracked()
    {
        var s = new HpTimelineSampler(_ => (0, 100));
        s.Track(7, 0);
        s.Tick(500f);                // one 0% sample already
        s.MarkDead(7, 1000);         // must NOT add a second 0
        s.MarkDead(999, 1000);       // untracked -> no-op
        Assert.Single(s.GetTrack(7)!.Pct);
        Assert.Null(s.GetTrack(999));
    }

    // Multi-boss plan Task 3: TickHpTimelines now Tracks/MarkDeads EVERY boss the stage set
    // knows (Plugin.Replay.cs), not just one lazily-resolved entity. This pins the sampler-level
    // capability that reworked loop depends on — two non-player (boss) entities sampled
    // independently on the SAME tick, each keeping its own Ms0/Pct series.
    [Fact]
    public void Two_boss_tracks_are_sampled_independently()
    {
        var hp = new Dictionary<long, (long Hp, long MaxHp)>
        {
            [10] = (500, 1000),
            [11] = (900, 1000),
        };
        var s = new HpTimelineSampler(id => hp[id]);
        s.Track(10, ms0: 0);
        s.Track(11, ms0: 0);
        s.Tick(500f);
        Assert.NotNull(s.GetTrack(10));
        Assert.NotNull(s.GetTrack(11));
        Assert.Equal(new[] { 50 }, s.GetTrack(10)!.Pct);
        Assert.Equal(new[] { 90 }, s.GetTrack(11)!.Pct);
    }

    // Amendment 3 (2026-08-12 review): a scripted-killed raid co-boss never reads Hp<=0 — it just
    // vanishes — so its HP track must terminate via an explicit MarkDead call driven by the stage
    // set's sticky `killed` flag, not a raw HP read. Pins that MarkDead's own contract (idempotent
    // final-zero append) composes correctly for that caller: calling it repeatedly while `killed`
    // stays true (every tick) must not grow the track past the one terminating zero sample.
    [Fact]
    public void MarkDead_called_every_tick_while_killed_appends_only_one_terminating_zero()
    {
        long hp = 40, maxHp = 100;
        var s = new HpTimelineSampler(_ => (hp, maxHp));
        s.Track(20, ms0: 0);
        s.Tick(500f);           // one 40% sample
        for (var i = 0; i < 5; i++) s.MarkDead(20, 1000);   // repeated, as a per-tick loop would do
        var track = s.GetTrack(20)!;
        Assert.Equal(new[] { 40, 0 }, track.Pct);
    }
}
