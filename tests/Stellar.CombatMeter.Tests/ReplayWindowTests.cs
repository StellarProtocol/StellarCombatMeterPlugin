using System.Linq;
using Stellar.CombatMeter.Replay;
using Xunit;

namespace Stellar.CombatMeter.Tests;

// Delta-window upload (owner design 2026-07-19): the recorder never stops; each banked archive
// serializes only the samples in (watermark, thisArchive] and advances the watermark. ReplayWindow
// is the pure slicer both the position tracks and the HP timelines run through. The load-bearing
// invariant: two contiguous windows CONCATENATE to the single-doc baseline — no gap, no overlap.
public class ReplayWindowTests
{
    private static PositionSample S(int ms) => new PositionSample(ms, ms, 0f, 0f, 0f);

    // ── positions: half-open-below / closed-above window ────────────────────────────────────────

    [Fact]
    public void SlicePositions_keeps_lowerExclusive_to_upperInclusive()
    {
        var all = new[] { S(0), S(500), S(1000), S(1500), S(2000) };
        // (1000, 2000] → 1500, 2000 (1000 excluded as lower bound, 2000 included as upper).
        var w = ReplayWindow.SlicePositions(all, lowerExclusive: 1000, upperInclusive: 2000);
        Assert.Equal(new[] { 1500, 2000 }, w.Select(s => s.Ms));
    }

    [Fact]
    public void SlicePositions_first_window_includes_ms_zero()
    {
        var all = new[] { S(0), S(500), S(1000) };
        // Initial watermark is below zero so the walk-in lead (ms=0) rides window 1.
        var w = ReplayWindow.SlicePositions(all, lowerExclusive: -1, upperInclusive: 1000);
        Assert.Equal(new[] { 0, 500, 1000 }, w.Select(s => s.Ms));
    }

    [Fact]
    public void SlicePositions_empty_window_returns_no_samples()
    {
        var all = new[] { S(0), S(500), S(1000) };
        Assert.Empty(ReplayWindow.SlicePositions(all, lowerExclusive: 1000, upperInclusive: 5000));
    }

    // THE load-bearing test: window1 ++ window2 == single-doc baseline (positions), asserting FULL
    // sample equality (X/Y/Z/Yaw + Ms via struct value-equality), symmetric with the HP concat test.
    [Fact]
    public void SlicePositions_two_windows_concatenate_to_baseline()
    {
        // Distinct values per field so the equality check exercises the whole struct, not just Ms.
        var all = new[]
        {
            new PositionSample(0,    1f,  2f,  3f,  10f),
            new PositionSample(500,  4f,  5f,  6f,  20f),
            new PositionSample(1000, 7f,  8f,  9f,  30f),
            new PositionSample(1500, 10f, 11f, 12f, 40f),
            new PositionSample(2000, 13f, 14f, 15f, 50f),
        };
        var baseline = ReplayWindow.SlicePositions(all, -1, 2000);
        var win1     = ReplayWindow.SlicePositions(all, -1, 1000);       // (watermark0, cut]
        var win2     = ReplayWindow.SlicePositions(all, 1000, 2000);     // (cut, end]

        Assert.Equal(5, baseline.Length);
        Assert.Equal(baseline, win1.Concat(win2));   // full-struct sequence equality, no gap/overlap
    }

    // Owner default 2: a FAILED/skipped hand-off does NOT advance the watermark — the same watermark
    // re-slices and RE-INCLUDES the un-uploaded samples into the next window (at-least-once).
    [Fact]
    public void SlicePositions_unadvanced_watermark_reincludes_samples()
    {
        var all = new[] { S(0), S(500), S(1000), S(1500) };
        var win1 = ReplayWindow.SlicePositions(all, -1, 1000);          // ms 0,500,1000
        // hand-off failed → watermark stays at -1 → next slice covers the same span + the new tail.
        var retry = ReplayWindow.SlicePositions(all, -1, 1500);         // ms 0,500,1000,1500
        Assert.Equal(new[] { 0, 500, 1000 }, win1.Select(s => s.Ms));
        Assert.Equal(new[] { 0, 500, 1000, 1500 }, retry.Select(s => s.Ms));
    }

    // ── HP timelines: implicit 500ms cadence, ms0 recomputation ─────────────────────────────────

    [Fact]
    public void SliceHp_recomputes_ms0_for_the_slice_start()
    {
        // Ms0=0, cadence 500 → grid times 0,500,1000,1500,2000.
        var track = new HpTrack(0, new[] { 100, 90, 80, 70, 60 });
        // (1000, 2000] → indices 3,4 (grid 1500,2000). New Ms0 must be 1500.
        var w = ReplayWindow.SliceHp(track, 1000, 2000, cadenceMs: 500)!;
        Assert.Equal(1500, w.Ms0);
        Assert.Equal(new[] { 70, 60 }, w.Pct);
    }

    [Fact]
    public void SliceHp_respects_a_nonzero_base_ms0()
    {
        // Ms0=1000 → grid times 1000,1500,2000. (-1,1500] → indices 0,1 (grid 1000,1500).
        var track = new HpTrack(1000, new[] { 55, 44, 33 });
        var w = ReplayWindow.SliceHp(track, -1, 1500, 500)!;
        Assert.Equal(1000, w.Ms0);
        Assert.Equal(new[] { 55, 44 }, w.Pct);
    }

    [Fact]
    public void SliceHp_empty_window_returns_null()
    {
        var track = new HpTrack(0, new[] { 100, 90 });   // grid 0,500
        Assert.Null(ReplayWindow.SliceHp(track, 5000, 9000, 500));
    }

    // HP concatenation: window1 ++ window2 == baseline Pct, and window2.Ms0 is contiguous with window1.
    [Fact]
    public void SliceHp_two_windows_concatenate_to_baseline()
    {
        var track = new HpTrack(0, new[] { 100, 90, 80, 70, 60 });   // grid 0,500,1000,1500,2000
        var baseline = ReplayWindow.SliceHp(track, -1, 2000, 500)!;
        var win1 = ReplayWindow.SliceHp(track, -1, 1000, 500)!;      // idx 0,1,2
        var win2 = ReplayWindow.SliceHp(track, 1000, 2000, 500)!;    // idx 3,4

        Assert.Equal(baseline.Pct, win1.Pct.Concat(win2.Pct));
        Assert.Equal(win1.Ms0 + win1.Pct.Count * 500, win2.Ms0);    // contiguous, no gap
    }

    // MarkDead appends a final 0 at the next grid slot; windowing puts it in whichever window
    // contains its grid time (the ms0 windowing trap the brief calls out).
    [Fact]
    public void SliceHp_markdead_zero_lands_in_the_window_containing_it()
    {
        // Live samples 100,90,80 (grid 0,500,1000); MarkDead appended 0 at grid 1500 (index 3).
        var track = new HpTrack(0, new[] { 100, 90, 80, 0 });
        var early = ReplayWindow.SliceHp(track, -1, 1000, 500)!;     // grid 0,500,1000 — no death
        var late  = ReplayWindow.SliceHp(track, 1000, 2000, 500)!;   // grid 1500 — the death 0
        Assert.Equal(new[] { 100, 90, 80 }, early.Pct);
        Assert.DoesNotContain(0, early.Pct);
        Assert.Equal(new[] { 0 }, late.Pct);
        Assert.Equal(1500, late.Ms0);
    }
}
