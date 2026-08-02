using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Stellar.CombatMeter.Tests;

// Base+peak snapshot stats (owner 2026-08-02): the attr sheet is buff-varying, so a single
// end-of-fight read froze buffed values. The tracker folds many sampled sheets into a per-profession
// running min (base) / max (peak). Pure accumulator — no IPluginServices/IL2CPP mock, matching
// LoadoutCaptureTests' style. Live sampling (Plugin.AttrRange.cs TickAttrRangeSample) is the thin
// untested seam, exercised only in-game.
public class AttrRangeTrackerTests
{
    private static Dictionary<int, long> Sheet(params (int Id, long Val)[] pairs)
        => pairs.ToDictionary(p => p.Id, p => p.Val);

    [Fact]
    public void BaseIsTheRunningMinAcrossObservations()
    {
        var t = new AttrRangeTracker();
        t.Observe(2, Sheet((11710, 2330), (11110, 11200))); // buffed sample first
        t.Observe(2, Sheet((11710, 500),  (11110, 2400)));  // idle sample later
        var baseMap = t.Base(2).ToDictionary(p => (int)p[0], p => p[1]);
        Assert.Equal(500, baseMap[11710]);
        Assert.Equal(2400, baseMap[11110]);
    }

    [Fact]
    public void PeaksAreSparse_OnlyWhereMaxExceedsMin()
    {
        var t = new AttrRangeTracker();
        t.Observe(2, Sheet((11710, 500), (10030, 49131))); // crit varies, ability score constant
        t.Observe(2, Sheet((11710, 2330), (10030, 49131)));
        var peaks = t.Peaks(2).ToDictionary(p => (int)p[0], p => p[1]);
        Assert.Equal(2330, peaks[11710]);          // crit peaked
        Assert.False(peaks.ContainsKey(10030));    // constant → no peak emitted
    }

    [Fact]
    public void ProfessionsAreIsolated()
    {
        var t = new AttrRangeTracker();
        t.Observe(2, Sheet((11710, 500)));
        t.Observe(5, Sheet((11710, 9000)));
        Assert.Equal(500, t.Base(2).Single(p => p[0] == 11710)[1]);
        Assert.Equal(9000, t.Base(5).Single(p => p[0] == 11710)[1]);
    }

    [Fact]
    public void ResetForRunClearsEverything()
    {
        var t = new AttrRangeTracker();
        t.Observe(2, Sheet((11710, 500)));
        t.ResetForRun();
        Assert.False(t.Has(2));
        Assert.Empty(t.Base(2));
        Assert.Empty(t.Peaks(2));
    }

    [Fact]
    public void ZeroValuesAndZeroProfessionAreIgnored()
    {
        var t = new AttrRangeTracker();
        t.Observe(0, Sheet((11710, 500)));          // profession 0 → skipped
        t.Observe(2, Sheet((11710, 0), (11110, 2400))); // zero attr value → skipped
        Assert.False(t.Has(0));
        Assert.DoesNotContain(t.Base(2), p => p[0] == 11710);
        Assert.Contains(t.Base(2), p => p[0] == 11110);
    }
}
