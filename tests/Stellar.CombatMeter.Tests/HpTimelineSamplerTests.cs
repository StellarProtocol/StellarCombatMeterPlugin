using System.Collections.Generic;
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
    public void ClampsPctAndSkipsWhenMaxHpUnknown()
    {
        var reads = new Queue<(long, long)>(new[] { (150L, 100L), (0L, 0L), (-5L, 100L) });
        var s = new HpTimelineSampler(_ => reads.Dequeue());
        s.Track(7, ms0: 1000);
        s.Tick(500f); // 150/100 -> clamped 100
        s.Tick(500f); // maxHp 0 -> skipped
        s.Tick(500f); // -5/100 -> clamped 0
        var t = s.GetTrack(7)!;
        Assert.Equal(new[] { 100, 0 }, t.Pct);
        Assert.Equal(1000, t.Ms0);
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
