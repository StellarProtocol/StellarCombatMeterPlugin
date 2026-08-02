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
}
