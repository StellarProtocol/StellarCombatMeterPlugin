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

    // -------------------------------------------------------------------------
    // Others: EntitySummonAppeared timestamp anchor (ResolveImagineCastMs).
    // -------------------------------------------------------------------------

    [Fact]
    public void No_appear_on_file_falls_back_to_hit_time()
    {
        Assert.Equal(5000, Plugin.ResolveImagineCastMs(hitMs: 5000, appearMs: null, maxWindowMs: 8000));
    }

    [Fact]
    public void Recent_appear_anchors_the_recorded_time_earlier_than_the_hit()
    {
        // The summon appeared 4s before its first hit landed (wind-up) — record the earlier, near-press
        // time instead of the first-hit-late time.
        Assert.Equal(6000, Plugin.ResolveImagineCastMs(hitMs: 10_000, appearMs: 6000, maxWindowMs: 8000));
    }

    [Fact]
    public void Appear_exactly_at_the_window_boundary_still_anchors()
    {
        Assert.Equal(2000, Plugin.ResolveImagineCastMs(hitMs: 10_000, appearMs: 2000, maxWindowMs: 8000));
    }

    [Fact]
    public void Stale_appear_outside_the_window_falls_back_to_hit_time()
    {
        // The cached appear belongs to an earlier, unrelated summon spawn — too old to be this burst's.
        Assert.Equal(10_000, Plugin.ResolveImagineCastMs(hitMs: 10_000, appearMs: 1999, maxWindowMs: 8000));
    }

    [Fact]
    public void Appear_after_the_hit_is_clock_skew_and_ignored()
    {
        Assert.Equal(5000, Plugin.ResolveImagineCastMs(hitMs: 5000, appearMs: 5001, maxWindowMs: 8000));
    }

    [Fact]
    public void Appear_exactly_at_hit_time_anchors()
    {
        Assert.Equal(5000, Plugin.ResolveImagineCastMs(hitMs: 5000, appearMs: 5000, maxWindowMs: 8000));
    }

    // -------------------------------------------------------------------------
    // Others: appear-SOURCED cast recording (buff-only companions, 2026-08-14).
    // Plugin.DecideAppearCast is the pure record/skip gate TryRecordImagineCastFromAppear runs on;
    // SeenSummonSet (novelty input) is pinned separately in SeenSummonSetTests.
    // -------------------------------------------------------------------------

    [Fact]
    public void Appear_gate_records_a_novel_foreign_summon_of_a_known_combatant()
    {
        Assert.Equal(Plugin.AppearCastGate.Record,
            Plugin.DecideAppearCast(summonerIsSelf: false, summonNovel: true, ownerIsKnownCombatant: true));
    }

    [Fact]
    public void Appear_gate_excludes_self()
    {
        // Self is on the authoritative LocalCooldowns begin-advance detector — recording from the
        // appear too would double-count the same cast. Self wins over every other input.
        Assert.Equal(Plugin.AppearCastGate.SelfSummoner,
            Plugin.DecideAppearCast(summonerIsSelf: true, summonNovel: true, ownerIsKnownCombatant: true));
    }

    [Fact]
    public void Appear_gate_rejects_a_reappearing_summon()
    {
        // AOI blink / re-entry of the SAME summon entity is never a new cast.
        Assert.Equal(Plugin.AppearCastGate.RepeatSummon,
            Plugin.DecideAppearCast(summonerIsSelf: false, summonNovel: false, ownerIsKnownCombatant: true));
    }

    [Fact]
    public void Appear_gate_rejects_a_phantom_from_an_owner_not_yet_in_combat()
    {
        // A player walking INTO view with an already-active companion fires an appear that is NOT a
        // fresh cast — without a _stats row for the owner, the appear must be skipped.
        Assert.Equal(Plugin.AppearCastGate.OwnerNotInCombat,
            Plugin.DecideAppearCast(summonerIsSelf: false, summonNovel: true, ownerIsKnownCombatant: false));
    }

    [Fact]
    public void Composite_guard_accepts_real_monster_and_companion_config_ids()
    {
        Assert.True(Plugin.CanProbeImagineComposite(10084));      // Celestial Flier (monster band)
        Assert.True(Plugin.CanProbeImagineComposite(3000033));    // Tina "- Resonance": *100 = 300003300 fits int
        Assert.True(Plugin.CanProbeImagineComposite(int.MaxValue / 100));   // boundary inclusive
    }

    [Fact]
    public void Composite_guard_rejects_zero_negative_and_overflowing_config_ids()
    {
        Assert.False(Plugin.CanProbeImagineComposite(0));
        Assert.False(Plugin.CanProbeImagineComposite(-1));
        Assert.False(Plugin.CanProbeImagineComposite(int.MaxValue / 100 + 1));   // *100 would overflow
    }

    [Fact]
    public void Appear_record_primes_the_burst_key_so_the_first_hit_does_not_rerecord()
    {
        // A DAMAGING imagine detected via its appear: the appear-path record runs ObserveBurstHit
        // first (returns true, PRIMES the (owner, base) key), so the summon's first hit seconds
        // later — the damage path's own ObserveBurstHit call — reads as the same burst and is NOT a
        // second cast. Only after a real silence gap does the key open again.
        var seen = new Dictionary<(EntityId, int), long>();
        Assert.True(Plugin.ObserveBurstHit(seen, (PlayerA, ImagineX), ms: 1000, gapMs: Plugin.ImagineRetriggerGapMs));   // appear records
        Assert.False(Plugin.ObserveBurstHit(seen, (PlayerA, ImagineX), ms: 5000, gapMs: Plugin.ImagineRetriggerGapMs));  // first hit — deduped
        Assert.True(Plugin.ObserveBurstHit(seen, (PlayerA, ImagineX), ms: 5000 + Plugin.ImagineRetriggerGapMs, gapMs: Plugin.ImagineRetriggerGapMs));
    }

    [Fact]
    public void Hit_recorded_first_dedupes_a_trailing_appear()
    {
        // Symmetric order: the damage path already recorded this burst; an appear event arriving
        // moments later must be swallowed by the same key.
        var seen = new Dictionary<(EntityId, int), long>();
        Assert.True(Plugin.ObserveBurstHit(seen, (PlayerA, ImagineX), ms: 1000, gapMs: Plugin.ImagineRetriggerGapMs));   // hit records
        Assert.False(Plugin.ObserveBurstHit(seen, (PlayerA, ImagineX), ms: 1200, gapMs: Plugin.ImagineRetriggerGapMs));  // appear — deduped
    }
}
