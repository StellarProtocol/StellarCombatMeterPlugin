using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public sealed class CdRatioTrackerTests
{
    private static SkillCooldown Row(int skillId, long beginMs, int accel) =>
        new(skillId, beginMs, 5000, SkillCooldownKind.Normal, 0, 5000, 0, 0, accel);

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
