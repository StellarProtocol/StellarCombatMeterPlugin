using System.Collections.Generic;
using Xunit;

namespace Stellar.CombatMeter.Tests;

// Per-actor TALENT spec timeline (spec upload design 2026-09-26 item 1, owner: "we should send spec to server if
// we are able to probe it correctly"). The tracker records change points from CombatEvent.SpecChanged and folds
// them into [specId, startMs, endMs] spans — ONLY for the periods where the framework reported the spec as
// talent-derived (FromTalent). A cast-inferred period is a guess and never becomes a span. Pure accumulator, same
// style as ClassSpanTrackerTests; the live wiring (Plugin.SpecSpans.cs) is the thin seam.
public class SpecSpanTrackerTests
{
    // Spec ids: ProfessionId * 10000 + index (ICombatSpec doc). Marksman 11 / Falconry, Beat Performer 13 /
    // Concerto, Verdant Oracle 5 / Smite + Lifebind.
    const int Falconry = 110002, Concerto = 130002, Smite = 50001, Lifebind = 50002;
    const long Miyuki = 0x0012_3456_0000_0280;

    // The owner-confirmed live sequence (docs/recon/spec-from-public-wire-data.md § Live test, capture
    // stellar-wirecap-20260926-164712): Miyuki Marksman/Falconry → Beat Performer/Concerto (class swap) →
    // Verdant Oracle/Smite (class swap) → Lifebind (same-class swap: the framework holds Smite across the ~2 s
    // gap and raises ONE Smite → Lifebind event). Owner: "everything correct".
    [Fact]
    public void Miyuki_sequence_yields_one_contiguous_span_per_talent_spec()
    {
        var t = new SpecSpanTracker();
        t.OnSpecChanged(Miyuki, Falconry, fromTalent: true, 1_000);
        t.OnSpecChanged(Miyuki, Concerto, fromTalent: true, 61_000);
        t.OnSpecChanged(Miyuki, Smite, fromTalent: true, 121_000);
        t.OnSpecChanged(Miyuki, Lifebind, fromTalent: true, 181_000);

        var spans = t.Spans(Miyuki, 240_000);

        Assert.Equal(4, spans.Count);
        Assert.Equal(new long[] { Falconry, 1_000, 61_000 }, spans[0]);
        Assert.Equal(new long[] { Concerto, 61_000, 121_000 }, spans[1]);
        Assert.Equal(new long[] { Smite, 121_000, 181_000 }, spans[2]);
        Assert.Equal(new long[] { Lifebind, 181_000, 240_000 }, spans[3]);
    }

    // A single talent spec all run IS worth uploading (unlike classSpans, which omit a single-class actor): the
    // span is the only place the server learns the authoritative spec.
    [Fact]
    public void A_single_talent_spec_still_yields_a_span()
    {
        var t = new SpecSpanTracker();
        t.OnSpecChanged(Miyuki, Smite, fromTalent: true, 5_000);
        Assert.Equal(new[] { new long[] { Smite, 5_000, 9_000 } }, t.Spans(Miyuki, 9_000));
    }

    // A period the framework reports as CAST-derived (class changed with no root buff of the new class yet, or a
    // swap gap that outlived 10 s) closes the open span and opens nothing — only probed-correct spec is sent.
    [Fact]
    public void A_non_talent_period_yields_no_span_and_closes_the_open_one()
    {
        var t = new SpecSpanTracker();
        t.OnSpecChanged(Miyuki, Falconry, fromTalent: true, 1_000);
        t.OnSpecChanged(Miyuki, 130001, fromTalent: false, 10_000);   // class swap, spec now cast-guessed
        t.OnSpecChanged(Miyuki, 0, fromTalent: false, 20_000);        // then nothing at all
        t.OnSpecChanged(Miyuki, Concerto, fromTalent: true, 30_000);  // root buff arrives: authoritative again

        var spans = t.Spans(Miyuki, 50_000);

        Assert.Equal(2, spans.Count);
        Assert.Equal(new long[] { Falconry, 1_000, 10_000 }, spans[0]);
        Assert.Equal(new long[] { Concerto, 30_000, 50_000 }, spans[1]);
    }

    [Fact]
    public void An_entity_only_ever_cast_derived_has_no_spans()
    {
        var t = new SpecSpanTracker();
        t.OnSpecChanged(Miyuki, Smite, fromTalent: false, 1_000);
        t.OnSpecChanged(Miyuki, Lifebind, fromTalent: false, 2_000);
        Assert.Empty(t.Spans(Miyuki, 10_000));
        Assert.Empty(t.Spans(424242, 10_000));                         // never seen at all
    }

    // Cast → talent for the SAME spec value raises no SpecChanged in the framework (it reports value changes);
    // a talent re-announce of the spec already open (scene re-appear 0 → spec) must not split the span.
    [Fact]
    public void Re_announcing_the_open_spec_does_not_split_the_span()
    {
        var t = new SpecSpanTracker();
        t.OnSpecChanged(Miyuki, Smite, fromTalent: true, 1_000);
        t.OnSpecChanged(Miyuki, Smite, fromTalent: true, 5_000);
        Assert.Equal(new[] { new long[] { Smite, 1_000, 8_000 } }, t.Spans(Miyuki, 8_000));
    }

    // Run start (ResetForRun): the previous run's timeline is dropped and the CURRENT talent state is seeded
    // from ICombatSpec.TryGetTalentSpec, because a player already in view at run start raises no SpecChanged.
    [Fact]
    public void Run_start_resets_then_seeds_the_current_talent_spec()
    {
        var t = new SpecSpanTracker();
        t.OnSpecChanged(Miyuki, Falconry, fromTalent: true, 1_000);    // previous run / town
        t.ResetForRun();
        Assert.Empty(t.Spans(Miyuki, 100_000));
        Assert.Empty(t.Entities());

        t.Seed(Miyuki, Smite, 50_000);                                  // TryGetTalentSpec == Smite at run start
        t.Seed(777, 0, 50_000);                                         // no talent spec: nothing seeded
        t.OnSpecChanged(Miyuki, Lifebind, fromTalent: true, 70_000);

        var spans = t.Spans(Miyuki, 90_000);
        Assert.Equal(new long[] { Smite, 50_000, 70_000 }, spans[0]);
        Assert.Equal(new long[] { Lifebind, 70_000, 90_000 }, spans[1]);
        Assert.Empty(t.Spans(777, 90_000));
    }

    // A seed never overrides a timeline already open for the entity (the event can land before the seed pass).
    [Fact]
    public void Seed_does_not_override_an_already_open_span()
    {
        var t = new SpecSpanTracker();
        t.OnSpecChanged(Miyuki, Smite, fromTalent: true, 1_000);
        t.Seed(Miyuki, Smite, 2_000);
        Assert.Equal(new[] { new long[] { Smite, 1_000, 5_000 } }, t.Spans(Miyuki, 5_000));
    }

    // Archive freeze: the spans written into the frozen snapshot are capped at ArchivedAtMs and later events
    // never mutate that archive's copy (the same bake-in contract as ApplyClassSpans).
    [Fact]
    public void Archive_freeze_caps_at_archive_time_and_later_events_do_not_touch_the_frozen_copy()
    {
        var t = new SpecSpanTracker();
        t.OnSpecChanged(Miyuki, Smite, fromTalent: true, 1_000);
        var snap = new EntitySnapshot();
        Plugin.WriteSpecSpansToSnapshot(snap, t.Spans(Miyuki, 30_000));

        t.OnSpecChanged(Miyuki, Lifebind, fromTalent: true, 40_000);  // after the archive

        Assert.Equal(new long[] { Smite }, snap.SpecSpanId);
        Assert.Equal(new long[] { 1_000 }, snap.SpecSpanStart);
        Assert.Equal(new long[] { 30_000 }, snap.SpecSpanEnd);
        Assert.Equal(2, t.Spans(Miyuki, 50_000).Count);                // the tracker itself kept going
    }

    // A span that would start at/after the cap (event stamped after the archive instant) is not emitted.
    [Fact]
    public void Spans_starting_at_or_after_the_cap_are_not_emitted()
    {
        var t = new SpecSpanTracker();
        t.OnSpecChanged(Miyuki, Smite, fromTalent: true, 1_000);
        t.OnSpecChanged(Miyuki, Lifebind, fromTalent: true, 10_000);
        Assert.Equal(new[] { new long[] { Smite, 1_000, 10_000 } }, t.Spans(Miyuki, 10_000));
    }

    // Bounded: a town crowd raises SpecChanged for every player in view. Past MaxEntities the OLDEST-seen entity is
    // dropped (O(1)); the run-start seed pass re-derives current state from the framework anyway.
    [Fact]
    public void The_tracker_is_bounded_and_drops_the_oldest_seen_entity()
    {
        var t = new SpecSpanTracker();
        for (long i = 1; i <= SpecSpanTracker.MaxEntities + 5; i++) t.OnSpecChanged(i, Smite, fromTalent: true, i);
        Assert.Equal(SpecSpanTracker.MaxEntities, t.Entities().Count);
        Assert.Empty(t.Spans(1, long.MaxValue));
        Assert.Single(t.Spans(SpecSpanTracker.MaxEntities + 5, long.MaxValue));
    }

    // QA fix round (review of e86a595, finding 1 [major]): the bound must never cost the people who matter. Self, the
    // current party roster and the current combatants are PINNED (never evicted); a town crowd of strangers admitted
    // after them can only evict other strangers.
    [Fact]
    public void Over_the_bound_self_and_party_spans_survive_a_crowd_of_strangers()
    {
        const long self = 0x0001_0000_0280, mate = 0x0002_0000_0280;
        var t = new SpecSpanTracker(id => id == self || id == mate);
        t.OnSpecChanged(self, Smite, fromTalent: true, 1);
        t.OnSpecChanged(mate, Falconry, fromTalent: true, 2);
        for (long i = 0; i < SpecSpanTracker.MaxEntities + 50; i++) t.OnSpecChanged(1_000_000 + i, Concerto, fromTalent: true, 10 + i);

        Assert.Equal(new[] { new long[] { Smite, 1, 100_000 } }, t.Spans(self, 100_000));
        Assert.Equal(new[] { new long[] { Falconry, 2, 100_000 } }, t.Spans(mate, 100_000));
        Assert.Equal(SpecSpanTracker.MaxEntities, t.Entities().Count);
    }

    // Among the unpinned, the victim is the least-recently-CHANGED entity (not the first-seen): a stranger whose spec
    // changed recently outlives one that has been static since it was first seen.
    [Fact]
    public void Eviction_takes_the_least_recently_changed_unpinned_entity()
    {
        var t = new SpecSpanTracker();
        for (long i = 1; i <= SpecSpanTracker.MaxEntities; i++) t.OnSpecChanged(i, Smite, fromTalent: true, i);
        t.OnSpecChanged(1, Lifebind, fromTalent: true, 10_000);        // entity 1 changes → youngest
        t.OnSpecChanged(99_999, Smite, fromTalent: true, 10_001);      // full → evicts entity 2, not 1
        Assert.Equal(2, t.Spans(1, 20_000).Count);
        Assert.Empty(t.Spans(2, 20_000));
    }

    // QA finding 4 [nit]: two change points in the same ms (a whole-packet flip reported twice) yield no zero-length span.
    [Fact]
    public void Zero_length_spans_are_not_emitted()
    {
        var t = new SpecSpanTracker();
        t.OnSpecChanged(Miyuki, Smite, fromTalent: true, 5_000);
        t.OnSpecChanged(Miyuki, Lifebind, fromTalent: true, 5_000);
        Assert.Equal(new[] { new long[] { Lifebind, 5_000, 9_000 } }, t.Spans(Miyuki, 9_000));
    }

    // Perf finding 10: change points per entity are capped (oldest dropped) so a pathological flip-flopper cannot grow
    // without bound within one run.
    [Fact]
    public void Change_points_per_entity_are_capped_dropping_the_oldest()
    {
        var t = new SpecSpanTracker();
        for (var i = 0; i < SpecSpanTracker.MaxPointsPerEntity + 10; i++)
            t.OnSpecChanged(Miyuki, i % 2 == 0 ? Smite : Lifebind, fromTalent: true, 1_000 + i);
        var spans = t.Spans(Miyuki, 1_000_000);
        Assert.Equal(SpecSpanTracker.MaxPointsPerEntity, spans.Count);
        Assert.Equal(1_010, spans[0][1]);                              // the 10 oldest points are gone
    }

    [Fact]
    public void WriteSpecSpansToSnapshot_fills_parallel_arrays()
    {
        var snap = new EntitySnapshot();
        Plugin.WriteSpecSpansToSnapshot(snap, new List<long[]> { new long[] { Smite, 1, 2 }, new long[] { Lifebind, 2, 3 } });
        Assert.Equal(new long[] { Smite, Lifebind }, snap.SpecSpanId);
        Assert.Equal(new long[] { 1, 2 }, snap.SpecSpanStart);
        Assert.Equal(new long[] { 2, 3 }, snap.SpecSpanEnd);
    }
}
