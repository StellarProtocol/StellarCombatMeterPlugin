using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public sealed class CdRatioTrackerTests
{
    private static SkillCooldown Row(int skillId, long beginMs, int accel) =>
        Row(skillId, beginMs, 5000, 5000, accel);

    private static SkillCooldown Row(int skillId, long beginMs, int durationMs, int validCdMs, int accel) =>
        new(skillId, beginMs, durationMs, SkillCooldownKind.Normal, 0, validCdMs, 0, 0, accel);

    [Fact]
    public void First_observation_emits_the_fresh_rows_ratio()
    {
        var tracker = new CdRatioTracker();
        var result = tracker.Observe(new List<SkillCooldown> { Row(1, 100, 1000), Row(2, 100, 1000) });
        Assert.Equal(1000, result);
        Assert.Equal(1000, tracker.Last);
    }

    [Fact]
    public void Unchanged_rows_emit_nothing()
    {
        var tracker = new CdRatioTracker();
        var rows = new List<SkillCooldown> { Row(1, 100, 1000), Row(2, 100, 1000) };
        tracker.Observe(rows);
        var result = tracker.Observe(new List<SkillCooldown> { Row(1, 100, 1000), Row(2, 100, 1000) });
        Assert.Null(result);
    }

    [Fact]
    public void A_changed_row_states_the_new_ratio()
    {
        var tracker = new CdRatioTracker();
        tracker.Observe(new List<SkillCooldown> { Row(1, 100, 1000), Row(2, 100, 1000) });
        var result = tracker.Observe(new List<SkillCooldown> { Row(1, 200, 3000), Row(2, 100, 1000) });
        Assert.Equal(3000, result);
    }

    [Fact]
    public void A_stale_row_never_drags_the_scalar_back()
    {
        var tracker = new CdRatioTracker();
        tracker.Observe(new List<SkillCooldown> { Row(1, 100, 1000), Row(2, 100, 1000) });
        var afterAFresh = tracker.Observe(new List<SkillCooldown> { Row(1, 200, 3000), Row(2, 100, 1000) });
        Assert.Equal(3000, afterAFresh);

        var bothStale = tracker.Observe(new List<SkillCooldown> { Row(1, 200, 3000), Row(2, 100, 1000) });
        Assert.Null(bothStale);

        var bRecastSameAsLast = tracker.Observe(new List<SkillCooldown> { Row(1, 200, 3000), Row(2, 300, 3000) });
        Assert.Null(bRecastSameAsLast);
    }

    [Fact]
    public void Disagreeing_fresh_rows_take_the_max_and_count()
    {
        var tracker = new CdRatioTracker();
        var result = tracker.Observe(new List<SkillCooldown> { Row(1, 100, 3000), Row(2, 100, 1500) });
        Assert.Equal(3000, result);
        Assert.Equal(1, tracker.Disagreements);
    }

    [Fact]
    public void Empty_snapshot_emits_nothing_and_keeps_Last()
    {
        var tracker = new CdRatioTracker();
        tracker.Observe(new List<SkillCooldown> { Row(1, 100, 1000), Row(2, 100, 1000) });
        var result = tracker.Observe(Array.Empty<SkillCooldown>());
        Assert.Null(result);
        Assert.Equal(1000, tracker.Last);
    }

    // Task 4 review gap: the freshness key is the FOUR-field tuple (BeginTimeMs, DurationMs, ValidCdTimeMs,
    // AccelerateCdRatio). Nothing else pinned that DurationMs/ValidCdTimeMs are part of it, so a tracker keyed on
    // (begin, accel) alone would have passed the whole suite — and would then miss a cooldown-buff application
    // that shortens the remaining cooldown of an ALREADY-running row (begin unchanged, accel new).
    [Fact]
    public void Duration_and_valid_cd_time_are_each_part_of_the_freshness_tuple()
    {
        var tracker = new CdRatioTracker();
        var fresh = new List<SkillCooldown>();

        tracker.Observe(new List<SkillCooldown> { Row(1, 100, 5000, 5000, 1000) }, fresh);
        Assert.Single(fresh);                                                                    // first sight

        Assert.Null(tracker.Observe(new List<SkillCooldown> { Row(1, 100, 5000, 5000, 1000) }, fresh));
        Assert.Empty(fresh);                                                                     // same tuple → stale

        // DurationMs alone changed (begin AND accel identical) → the row is fresh …
        Assert.Null(tracker.Observe(new List<SkillCooldown> { Row(1, 100, 6000, 5000, 1000) }, fresh));
        Assert.Single(fresh);
        Assert.Equal(6000, fresh[0].DurationMs);
        // … but its ratio equals Last, so no new scalar is emitted.

        // ValidCdTimeMs alone changed (begin AND accel identical) → fresh too.
        Assert.Null(tracker.Observe(new List<SkillCooldown> { Row(1, 100, 6000, 4200, 1000) }, fresh));
        Assert.Single(fresh);
        Assert.Equal(4200, fresh[0].ValidCdTimeMs);

        // The real shape of a cooldown buff landing on a running cooldown: begin unchanged, the remaining
        // valid time collapses, and the row carries a NEW accelerate ratio → that ratio IS the new scalar.
        Assert.Equal(2500, tracker.Observe(new List<SkillCooldown> { Row(1, 100, 6000, 3800, 2500) }, fresh));
        Assert.Single(fresh);
        Assert.Equal(2500, tracker.Last);

        // Repeat the identical tuple → nothing fresh, nothing emitted.
        Assert.Null(tracker.Observe(new List<SkillCooldown> { Row(1, 100, 6000, 3800, 2500) }, fresh));
        Assert.Empty(fresh);
        Assert.Equal(2500, tracker.Last);
    }

    // Task 4 review gap: Disagreements must be computed over the FRESH rows of a tick, never over every row
    // presented. A stale row is cached at whatever ratio it last carried, so comparing it against a fresh row
    // would report a disagreement on every single tick after any ratio change — the counter is the signal that
    // settles the accelerate-vs-reduce formula in the first testing run, so a permanently-hot counter is useless.
    [Fact]
    public void A_stale_row_at_a_different_ratio_is_not_a_disagreement()
    {
        var tracker = new CdRatioTracker();
        tracker.Observe(new List<SkillCooldown> { Row(1, 100, 1000), Row(2, 100, 1000) });
        Assert.Equal(0, tracker.Disagreements);

        // Skill 1 recasts under a cooldown buff (fresh, 3000); skill 2's row is byte-identical to last tick,
        // i.e. still CACHED at 1000. Exactly ONE row is fresh → there is nothing to disagree with.
        Assert.Equal(3000, tracker.Observe(new List<SkillCooldown> { Row(1, 200, 3000), Row(2, 100, 1000) }));
        Assert.Equal(0, tracker.Disagreements);

        // Sanity that the counter is not simply dead: two genuinely fresh rows that disagree DO count once.
        Assert.Equal(4000, tracker.Observe(new List<SkillCooldown> { Row(1, 300, 4000), Row(2, 300, 2000) }));
        Assert.Equal(1, tracker.Disagreements);
    }

    // Final review finding I1: the framework keeps cooldown rows across a scene change (CombatService clears
    // _localCooldowns only on logout; the getter evicts only EXPIRED rows), so a Reset() that also cleared the
    // per-skill cache would make every RETAINED row read as fresh again on the very next tick — Observe would then
    // re-emit the PREVIOUS scene's peak ratio into the new segment (a biased fit observation) and bump
    // Disagreements. Reset() must forget only the emitted scalar: a scene change forgets the emitted scalar, not
    // what rows were seen — retained rows are stale, and stale rows never speak. Only a genuinely NEW or CHANGED
    // cooldown row states the new scene's own truth.
    [Fact]
    public void Reset_forgets_the_scalar_but_stale_rows_stay_silent()
    {
        var tracker = new CdRatioTracker();
        var rows = new List<SkillCooldown> { Row(1, 100, 2500), Row(2, 100, 2500) };
        Assert.Equal(2500, tracker.Observe(rows));

        tracker.Reset();
        Assert.Null(tracker.Last);

        // The SAME retained rows are STALE, not fresh — Reset must not let them restate the old scene's ratio.
        Assert.Null(tracker.Observe(rows));
        Assert.Null(tracker.Last);
        Assert.Equal(0, tracker.Disagreements);

        // A genuinely changed row (skill 1's cooldown begins anew) states the new scene's own truth.
        var changed = new List<SkillCooldown> { Row(1, 1000, 1000), Row(2, 100, 2500) };
        Assert.Equal(1000, tracker.Observe(changed));
    }

    [Fact]
    public void Fresh_list_receives_only_the_fresh_rows()
    {
        var tracker = new CdRatioTracker();
        var fresh = new List<SkillCooldown>();
        tracker.Observe(new List<SkillCooldown> { Row(1, 100, 1000), Row(2, 100, 1000) }, fresh);
        tracker.Observe(new List<SkillCooldown> { Row(1, 200, 3000), Row(2, 100, 1000) }, fresh);
        Assert.Single(fresh);
        Assert.Equal(1, fresh[0].SkillId);
    }
}
