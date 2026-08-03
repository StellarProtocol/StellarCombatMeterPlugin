using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Stellar.CombatMeter.Tests;

// Per-entity professionId timeline (owner design 2026-08-03, per-entity class detection plan Task 1):
// the plugin records each player entity's professionId over the run as contiguous
// [professionId, startMs, endMs] spans, so the site can show which classes were played (self AND
// party) from the actual professionId instead of guessing from cast skills. Pure accumulator — no
// IPluginServices/IL2CPP mock, matching AttrRangeTrackerTests' style. Live sampling
// (Plugin.ClassTimeline.cs TickClassTimeline) is the thin untested seam, exercised only in-game.
public class ClassSpanTrackerTests
{
    [Fact]
    public void SingleClass_SpansEmpty_NoTimelineNeeded()
    {
        var t = new ClassSpanTracker();
        t.Observe(1, 2, 0);
        Assert.Empty(t.Spans(1, 10_000));
    }

    [Fact]
    public void TwoClasses_ProduceContiguousSpansCappedAtEndMs()
    {
        var t = new ClassSpanTracker();
        t.Observe(1, 2, 0);
        t.Observe(1, 5, 5000);
        var spans = t.Spans(1, 12_000);
        Assert.Equal(2, spans.Count);
        Assert.Equal(new long[] { 2, 0, 5000 }, spans[0]);
        Assert.Equal(new long[] { 5, 5000, 12_000 }, spans[1]);
    }

    [Fact]
    public void RepeatedSameProfessionObservations_DoNotAddPoints()
    {
        var t = new ClassSpanTracker();
        t.Observe(1, 2, 0);
        t.Observe(1, 2, 1000);
        t.Observe(1, 2, 2000);
        Assert.Empty(t.Spans(1, 5000));   // still a single class -> no timeline
    }

    [Fact]
    public void ProfessionZero_Ignored()
    {
        var t = new ClassSpanTracker();
        t.Observe(1, 0, 0);     // unknown profession — never recorded
        t.Observe(1, 2, 1000);
        Assert.Empty(t.Spans(1, 5000));   // only one real class ever observed
    }

    [Fact]
    public void PerEntityIsolation()
    {
        var t = new ClassSpanTracker();
        t.Observe(1, 2, 0);
        t.Observe(1, 5, 1000);
        t.Observe(2, 9, 0);
        Assert.Equal(2, t.Spans(1, 5000).Count);
        Assert.Empty(t.Spans(2, 5000));
    }

    [Fact]
    public void ResetForRun_ClearsEverything()
    {
        var t = new ClassSpanTracker();
        t.Observe(1, 2, 0);
        t.Observe(1, 5, 1000);
        t.ResetForRun();
        Assert.Empty(t.Spans(1, 5000));
        Assert.Empty(t.Entities());
    }

    [Fact]
    public void Entities_ReturnsEntityIdsSeen()
    {
        var t = new ClassSpanTracker();
        t.Observe(1, 2, 0);
        t.Observe(2, 9, 0);
        Assert.Equal(new long[] { 1, 2 }, t.Entities().OrderBy(x => x).ToArray());
    }

    // -------------------------------------------------------------------------
    // WriteClassSpansToSnapshot (Task 2 wiring): bakes the tracker's folded spans into an
    // EntitySnapshot's parallel ClassSpanProf/Start/End arrays — pure, mirrors
    // AttrRangeTrackerTests' WriteRangeToSnapshot coverage.
    // -------------------------------------------------------------------------

    [Fact]
    public void WriteClassSpansToSnapshot_WritesParallelArrays()
    {
        var snap = new EntitySnapshot();
        var spans = new List<long[]> { new long[] { 2, 0, 5000 }, new long[] { 5, 5000, 12_000 } };

        Plugin.WriteClassSpansToSnapshot(snap, spans);

        Assert.Equal(new long[] { 2, 5 }, snap.ClassSpanProf);
        Assert.Equal(new long[] { 0, 5000 }, snap.ClassSpanStart);
        Assert.Equal(new long[] { 5000, 12_000 }, snap.ClassSpanEnd);
    }

    [Fact]
    public void WriteClassSpansToSnapshot_EmptySpans_WritesEmptyArrays()
    {
        var snap = new EntitySnapshot();

        Plugin.WriteClassSpansToSnapshot(snap, System.Array.Empty<long[]>());

        Assert.Empty(snap.ClassSpanProf);
        Assert.Empty(snap.ClassSpanStart);
        Assert.Empty(snap.ClassSpanEnd);
    }

    // -------------------------------------------------------------------------
    // CombatLogAssembler.BuildActorClassSpans (Task 3): maps the snapshot's parallel arrays into
    // upload-ready [professionId,startMs,endMs][] triples — pure, mirrors AttrRangeTrackerTests'
    // SnapToActor_MapsSparsePeaksAndNullWhenEmpty coverage of BuildActorAttrPeaks.
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildActorClassSpans_MapsTriplesAndNullWhenEmpty()
    {
        var withSpans = new EntitySnapshot
        {
            ClassSpanProf  = new long[] { 2, 5 },
            ClassSpanStart = new long[] { 0, 5000 },
            ClassSpanEnd   = new long[] { 5000, 12_000 },
        };
        var noSpans = new EntitySnapshot();

        var mapped = Stellar.CombatMeter.LogUpload.CombatLogAssembler.BuildActorClassSpans(withSpans);
        Assert.NotNull(mapped);
        Assert.Equal(new long[] { 2, 0, 5000 }, mapped![0]);
        Assert.Equal(new long[] { 5, 5000, 12_000 }, mapped[1]);

        Assert.Null(Stellar.CombatMeter.LogUpload.CombatLogAssembler.BuildActorClassSpans(noSpans));
    }

    // -------------------------------------------------------------------------
    // ClassesPlayedInWindow (per-archive class label): the archived row must show EVERY class the
    // entity actually played DURING that archive, clamped to the archive's own [EnteredAtMs,
    // ArchivedAtMs] window — NOT the single frozen professionId (which is whatever the entity had at
    // bank time, so a clear-phase archive banked after a swap mislabels the player as the boss class:
    // the LUz6opkvNX bug where the 34s clear phase read "Frost Mage" though the owner was Verdant
    // Oracle for 26 of its 34s). The classSpans are run-anchored and accumulate across archives, so
    // clamping to the archive window is what stops an earlier archive's span from bleeding in.
    // -------------------------------------------------------------------------

    // Real LUz6opkvNX self timeline: Oracle(5) 173538→199399, then Frost Mage(2) 199399→257649.
    private static EntitySnapshot RevetteSnap() => new()
    {
        ClassSpanProf  = new long[] { 5, 2 },
        ClassSpanStart = new long[] { 173538, 199399 },
        ClassSpanEnd   = new long[] { 199399, 257649 },
    };

    [Fact]
    public void ClassesPlayedInWindow_ClearPhaseArchive_ShowsBothInPlayOrder()
    {
        // Archive 1 (clear phase) window: 173386 → 207415 (34s). Oracle spans 25.9s of it, then FM 8s.
        var order = Plugin.ClassesPlayedInWindow(RevetteSnap(), 173386, 207415);
        Assert.Equal(new[] { 5, 2 }, order.ToArray());   // Oracle first (played first), then Frost Mage
    }

    [Fact]
    public void ClassesPlayedInWindow_BossArchive_DropsThePreArchiveOracleSpan()
    {
        // Archive 2 (boss) window: 207415 → 257930. The Oracle span ended at 199399 — entirely BEFORE
        // this archive — so it must NOT bleed in; only Frost Mage overlaps.
        var order = Plugin.ClassesPlayedInWindow(RevetteSnap(), 207415, 257930);
        Assert.Equal(new[] { 2 }, order.ToArray());
    }

    [Fact]
    public void ClassesPlayedInWindow_NoTimeline_ReturnsEmpty()
    {
        // Single-class archive (tracker baked no spans) → empty, so the caller falls back to the
        // frozen professionId (unchanged behavior for the overwhelmingly common single-class row).
        Assert.Empty(Plugin.ClassesPlayedInWindow(new EntitySnapshot(), 0, 10_000));
    }

    [Fact]
    public void ClassesPlayedInWindow_DegenerateWindow_ReturnsEmpty()
    {
        // endMs <= startMs (missing/garbage window) must not throw or invent a class.
        Assert.Empty(Plugin.ClassesPlayedInWindow(RevetteSnap(), 207415, 207415));
    }

    [Fact]
    public void ClassesPlayedInWindow_DedupesAClassPlayedInTwoSeparateSpans()
    {
        // FM → Oracle → FM within the window: two distinct classes, FM listed once (first appearance).
        var snap = new EntitySnapshot
        {
            ClassSpanProf  = new long[] { 2, 5, 2 },
            ClassSpanStart = new long[] { 0, 4000, 8000 },
            ClassSpanEnd   = new long[] { 4000, 8000, 12_000 },
        };
        Assert.Equal(new[] { 2, 5 }, Plugin.ClassesPlayedInWindow(snap, 0, 12_000).ToArray());
    }
}
