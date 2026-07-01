using Stellar.CombatMeter.Replay;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public sealed class PositionTrackTests
{
    private static PositionSample S(int ms) => new(ms, ms, 0f, 0f, 0f);

    [Fact]
    public void Add_BelowCap_KeepsAll()
    {
        var t = new PositionTrack(maxSamples: 4);
        t.Add(S(0)); t.Add(S(500)); t.Add(S(1000));
        Assert.Equal(3, t.Count);
        Assert.Equal(500, t.StrideMs);
    }

    [Fact]
    public void Add_OverCap_Coalesces_KeepsEveryOther_DoublesStride()
    {
        var t = new PositionTrack(maxSamples: 4);
        for (var i = 0; i < 5; i++) t.Add(S(i * 500));   // 0,500,1000,1500,2000 -> exceeds 4
        Assert.True(t.Count <= 4);
        Assert.Equal(1000, t.StrideMs);                  // coalesced once
        var snap = t.Snapshot();
        Assert.Equal(0, snap[0].Ms);
        Assert.Equal(1000, snap[1].Ms);                  // even indices retained
    }

    [Fact]
    public void Add_TwoCoalesces_DoubleStrideAgain_OriginSurvives()
    {
        // maxSamples=4, add 8 samples at 500ms intervals: 0,500,1000,...,3500
        // Coalesce 1 fires when adding ms=2000 (buffer was [0,500,1000,1500]): keeps [0,1000], stride→1000
        // Coalesce 2 fires when adding ms=3000 (buffer was [0,1000,2000,2500]): keeps [0,2000], stride→2000
        // Final state after all 8 adds: count=4 [0,2000,3000,3500], stride=2000
        var t = new PositionTrack(maxSamples: 4);
        for (var i = 0; i < 8; i++) t.Add(S(i * 500));   // 0,500,...,3500
        Assert.Equal(2000, t.StrideMs);                   // two coalesces: 500→1000→2000
        Assert.True(t.Count <= 4);
        Assert.Equal(0, t.Snapshot()[0].Ms);              // origin sample survives both passes
    }
}
