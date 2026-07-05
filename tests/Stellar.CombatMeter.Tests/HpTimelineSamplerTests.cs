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
}
