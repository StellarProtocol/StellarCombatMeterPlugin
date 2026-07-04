using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.CombatMeter;
using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>
/// Covers the Battle-Imagine cast-recording logic, modelled on the REAL detection paths (both proven
/// in-game by the meter's imagine-cooldown display; a SkillUsed-Begin scheme was live-falsified —
/// run 282346129222270976 recorded zero casts):
/// <list type="bullet">
/// <item>SELF — LocalCooldowns begin-advance: the wire moves an imagine cooldown row's
/// skill_begin_time only ON CAST (ImagineCooldownCalc's contract), so an advance IS a cast at the
/// press instant. Predicates: <see cref="Plugin.IsSelfCastBeginAdvance"/> + <see cref="Plugin.IsFreshBegin"/>.</item>
/// <item>OTHERS — DamageDealt hits resolving via GetImagineForSkill, collapsed per damage burst:
/// <see cref="Plugin.ObserveBurstHit"/> refreshes the (src, base) last-seen time on EVERY hit and only
/// reports a new cast after ≥ <see cref="Plugin.ImagineRetriggerGapMs"/> of silence.</item>
/// </list>
/// Plugin itself cannot be headless-instantiated (IL2CPP-bound services — see ReplayCaptureTests'
/// doc comment), hence the extracted static predicates.
/// </summary>
public sealed class ImagineCastTests
{
    private static readonly EntityId PlayerA = new(0x0000_0001_0000_0280L);   // low 16 bits = 640 → IsPlayer
    private static readonly EntityId PlayerB = new(0x0000_0002_0000_0280L);
    private const int ImagineX = 12345;
    private const int ImagineY = 54321;

    // -------------------------------------------------------------------------
    // Others: burst-gap collapse over the damage stream.
    // -------------------------------------------------------------------------

    [Fact]
    public void FirstHit_of_a_burst_records_a_cast()
    {
        var seen = new Dictionary<(EntityId, int), long>();
        Assert.True(Plugin.ObserveBurstHit(seen, (PlayerA, ImagineX), ms: 1000, gapMs: 10_000));
    }

    [Fact]
    public void Sustained_summon_damage_records_exactly_one_cast()
    {
        // Reproduces the "phantom cast bubble at 1:09 with no stacks left" symptom: the old dedup
        // compared against the last RECORDED time, so a summon hitting continuously re-recorded every
        // 5s. Burst-gap semantics refresh last-seen on every hit — a 90s stream of hits every 3s must
        // yield exactly ONE cast entry.
        var seen = new Dictionary<(EntityId, int), long>();
        int recorded = 0;
        for (long ms = 0; ms <= 90_000; ms += 3000)
            if (Plugin.ObserveBurstHit(seen, (PlayerA, ImagineX), ms, Plugin.ImagineRetriggerGapMs)) recorded++;
        Assert.Equal(1, recorded);
    }

    [Fact]
    public void A_new_burst_after_silence_records_a_second_cast()
    {
        var seen = new Dictionary<(EntityId, int), long>();
        Assert.True(Plugin.ObserveBurstHit(seen, (PlayerA, ImagineX), ms: 0, gapMs: 10_000));
        Assert.False(Plugin.ObserveBurstHit(seen, (PlayerA, ImagineX), ms: 4000, gapMs: 10_000));     // same burst
        Assert.True(Plugin.ObserveBurstHit(seen, (PlayerA, ImagineX), ms: 14_000, gapMs: 10_000));    // 10s silence → recast
    }

    [Fact]
    public void Different_imagines_and_different_players_have_independent_keys()
    {
        // "Two imagines cast back-to-back, only one recorded" symptom: keys include the base skill id
        // AND the source, so neither a second imagine nor a second player is swallowed by the gap.
        var seen = new Dictionary<(EntityId, int), long>();
        Assert.True(Plugin.ObserveBurstHit(seen, (PlayerA, ImagineX), ms: 29_000, gapMs: 10_000));
        Assert.True(Plugin.ObserveBurstHit(seen, (PlayerA, ImagineY), ms: 29_400, gapMs: 10_000));
        Assert.True(Plugin.ObserveBurstHit(seen, (PlayerB, ImagineX), ms: 29_500, gapMs: 10_000));
    }

    [Fact]
    public void Gap_boundary_is_inclusive()
    {
        var seen = new Dictionary<(EntityId, int), long>();
        Plugin.ObserveBurstHit(seen, (PlayerA, ImagineX), ms: 0, gapMs: 10_000);
        Assert.True(Plugin.ObserveBurstHit(seen, (PlayerA, ImagineX), ms: 10_000, gapMs: 10_000));
    }

    // -------------------------------------------------------------------------
    // Self: LocalCooldowns skill_begin_time advance.
    // -------------------------------------------------------------------------

    [Fact]
    public void First_sighting_of_a_begin_is_a_cast()
    {
        Assert.True(Plugin.IsSelfCastBeginAdvance(beginMs: 5000, lastBeginMs: null));
    }

    [Fact]
    public void Unchanged_begin_is_the_same_cast()
    {
        // The cooldown row is re-polled every ~100ms while recharging; the begin only moves on cast.
        Assert.False(Plugin.IsSelfCastBeginAdvance(beginMs: 5000, lastBeginMs: 5000));
    }

    [Fact]
    public void Begin_jitter_within_the_advance_threshold_is_not_a_cast()
    {
        Assert.False(Plugin.IsSelfCastBeginAdvance(beginMs: 5400, lastBeginMs: 5000));
    }

    [Fact]
    public void Begin_advance_past_the_threshold_is_a_new_cast()
    {
        // Two charges dumped back-to-back each move begin — both must count (pre-combat stack dump).
        Assert.True(Plugin.IsSelfCastBeginAdvance(beginMs: 5000 + Plugin.SelfBeginAdvanceMs + 1, lastBeginMs: 5000));
    }

    [Fact]
    public void Zero_or_negative_begin_is_never_a_cast()
    {
        Assert.False(Plugin.IsSelfCastBeginAdvance(beginMs: 0, lastBeginMs: null));
        Assert.False(Plugin.IsSelfCastBeginAdvance(beginMs: -1, lastBeginMs: null));
    }

    [Fact]
    public void First_sighted_recent_begin_is_fresh()
    {
        Assert.True(Plugin.IsFreshBegin(beginMs: 10_000, nowMs: 12_000));
    }

    [Fact]
    public void First_sighted_old_begin_is_stale_not_a_live_cast()
    {
        // Plugin load / scene re-entry mid-recharge: the row's begin is from a cast made long before
        // we started watching — must NOT be recorded retroactively.
        Assert.False(Plugin.IsFreshBegin(beginMs: 10_000, nowMs: 10_000 + Plugin.SelfBeginFreshMs + 1));
    }
}
