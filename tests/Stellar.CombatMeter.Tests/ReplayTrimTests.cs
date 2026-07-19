using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.CombatMeter.Replay;
using Xunit;

namespace Stellar.CombatMeter.Tests;

// Delta-window memory bounding: when a window's hand-off succeeds and the watermark advances, the
// samples at or below the watermark are freed (they've been uploaded). The remaining buffer holds
// only the un-uploaded tail. Sampling (Add / Tick / MarkDead) is untouched — trimming is a
// lifecycle op that runs at archive time, replacing the old whole-buffer Reset().
public class ReplayTrimTests
{
    private static PositionSample S(int ms) => new PositionSample(ms, ms, 0f, 0f, 0f);

    // ── PositionTrack.TrimBelow ─────────────────────────────────────────────────────────────────

    [Fact]
    public void PositionTrack_TrimBelow_drops_samples_at_or_below_and_keeps_the_tail()
    {
        var t = new PositionTrack(maxSamples: 3600);
        foreach (var ms in new[] { 0, 500, 1000, 1500, 2000 }) t.Add(S(ms));
        var removed = t.TrimBelow(1000);
        Assert.Equal(3, removed);
        Assert.Equal(new[] { 1500, 2000 }, t.Snapshot().Select(s => s.Ms));
    }

    [Fact]
    public void PositionTrack_TrimBelow_below_all_removes_nothing()
    {
        var t = new PositionTrack(maxSamples: 3600);
        t.Add(S(1000)); t.Add(S(1500));
        Assert.Equal(0, t.TrimBelow(-1));
        Assert.Equal(2, t.Count);
    }

    [Fact]
    public void PositionTrack_TrimBelow_above_all_empties()
    {
        var t = new PositionTrack(maxSamples: 3600);
        t.Add(S(0)); t.Add(S(500));
        Assert.Equal(2, t.TrimBelow(5000));
        Assert.Empty(t.Snapshot());
        // Still usable after a full trim (sampling continues into the same track object).
        t.Add(S(6000));
        Assert.Equal(new[] { 6000 }, t.Snapshot().Select(s => s.Ms));
    }

    // ── ReplayCapture.TrimBelow (keeps TotalSamples accurate for the maxTotalSamples cap) ─────────

    [Fact]
    public void ReplayCapture_TrimBelow_frees_uploaded_samples_and_updates_total()
    {
        var cap = new ReplayCapture(FakeTransform, maxSamplesPerTrack: 3600, maxTotalSamples: 200_000, sampleIntervalMs: 500);
        cap.NoteEntity(new EntityId(1));
        cap.Active = true;
        for (var i = 0; i < 5; i++) cap.Tick(nowMs: i * 500, dtMs: 500f);   // samples at rel 0,500,1000,1500,2000
        Assert.Equal(5, cap.TotalSamples);

        cap.TrimBelow(1000);   // free rel <= 1000 (3 samples)
        Assert.Equal(2, cap.TotalSamples);
        Assert.Equal(new[] { 1500, 2000 }, cap.Tracks[new EntityId(1)].Snapshot().Select(s => s.Ms));
    }

    private static bool FakeTransform(EntityId id, out Position3D pos, out float yaw)
    {
        pos = new Position3D(id.Value, 0f, 0f); yaw = 0f; return true;
    }

    // ── HpTimelineSampler.TrimBelow (front-truncates + advances Ms0, preserves the grid) ──────────

    [Fact]
    public void HpSampler_TrimBelow_drops_front_samples_and_advances_ms0()
    {
        var s = new HpTimelineSampler(_ => (50, 100));
        s.Track(1, ms0: 0);
        for (var i = 0; i < 5; i++) s.Tick(500f);   // grid 0,500,1000,1500,2000
        s.TrimBelow(1000, cadenceMs: 500);          // drop grid <= 1000 (indices 0,1,2)
        var t = s.GetTrack(1)!;
        Assert.Equal(1500, t.Ms0);
        Assert.Equal(2, t.Pct.Count);
    }

    [Fact]
    public void HpSampler_TrimBelow_keeps_the_grid_consistent_for_later_slicing()
    {
        // After a trim, sample i is still at Ms0 + i*cadence — so a later window slices correctly.
        var s = new HpTimelineSampler(id => (id, 100));   // pct = id (constant) — value irrelevant here
        s.Track(1, ms0: 0);
        for (var i = 0; i < 6; i++) s.Tick(500f);         // grid 0..2500
        s.TrimBelow(500, 500);                            // drop grid 0,500 → new Ms0=1000
        var t = s.GetTrack(1)!;
        Assert.Equal(1000, t.Ms0);
        // Window (1500, 2500] over the trimmed track → grid 2000,2500 (indices 2,3 of the trimmed array).
        var w = ReplayWindow.SliceHp(t, 1500, 2500, 500)!;
        Assert.Equal(2000, w.Ms0);
        Assert.Equal(2, w.Pct.Count);
    }
}
