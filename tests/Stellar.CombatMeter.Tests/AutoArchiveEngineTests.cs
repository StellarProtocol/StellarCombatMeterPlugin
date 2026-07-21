using Stellar.Abstractions.Domain;
using Stellar.CombatMeter.AutoArchive;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public class AutoArchiveEngineTests
{
    // Baseline live-combat snapshot: 60s of content, damage 61s ago at now=200_000 (not idle),
    // full 4-man roster alive, in a run, no boss, flow version 1 pre-adopted via first Evaluate.
    private static AutoArchiveInputs Live(long nowMs = 200_000) => new()
    {
        NowMs = nowMs, CombatActive = true, CombatStartMs = 100_000, LastDamageMs = 160_000,
        HasStats = true, RosterSize = 4, DeadCount = 0, UnknownCount = 0,
        OutcomeFailed = false, BossPresent = false, BossGone = false, BossDead = false,
        InstancedRun = true, FlowStateVersion = 1,
    };

    private static AutoArchiveEngine Armed(AutoArchiveInputs baseline)
    {
        var e = new AutoArchiveEngine();
        Assert.Null(e.Evaluate(in baseline));   // adopt flow version / arm latches silently
        return e;
    }

    // ---- wipe ----

    [Fact]
    public void Wipe_fires_when_every_member_reads_dead()
    {
        var e = Armed(Live());
        e.WipeGraceMs = 0;   // pre-existing test fires on the SAME tick allDead turns true — grace default (2000ms) would suppress it; isolate the wipe-fire assertion from the new revive-grace debounce
        var s = Live() with { DeadCount = 4 };
        Assert.Equal(ArchiveReason.Wipe, e.Evaluate(in s));
    }

    [Fact]
    public void Wipe_blocked_by_unknown_vitals_members()
    {
        var e = Armed(Live());
        var s = Live() with { DeadCount = 3, UnknownCount = 1, RosterSize = 4 };
        Assert.Null(e.Evaluate(in s));
    }

    [Fact]
    public void RosterSize_zero_never_fires_wipe()
    {
        var e = Armed(Live());
        var s = Live() with { RosterSize = 0, DeadCount = 0 };
        Assert.Null(e.Evaluate(in s));
    }

    [Fact]
    public void Wipe_does_not_refire_while_still_dead_then_refires_after_revive()
    {
        // Renamed/adapted for round 2 (was Wipe_latches_until_a_revive_then_rearms): mechanism is
        // now edge detection, not a manual latch — allDead staying true produces no NEW edge (no
        // refire), a revive (allDead false) consumes the edge with no fire, and a fresh death after
        // that IS a new rising edge and fires again. Same intent, edge semantics give it for free.
        var e = Armed(Live());
        e.WipeGraceMs = 0;   // both fires below happen on the SAME tick allDead turns true — isolate from revive-grace
        var dead = Live() with { DeadCount = 4 };
        Assert.Equal(ArchiveReason.Wipe, e.Evaluate(in dead));
        e.OnArchived(dead.NowMs, ArchiveReason.Wipe);
        var later = dead with { NowMs = dead.NowMs + AutoArchiveEngine.DefaultCooldownMs + 1 };
        Assert.Null(e.Evaluate(in later));                       // still all dead — no new edge
        var revived = later with { DeadCount = 3, NowMs = later.NowMs + 1000 };
        Assert.Null(e.Evaluate(in revived));                     // revived — edge consumed, nobody's wiped
        var deadAgain = revived with { DeadCount = 4, NowMs = revived.NowMs + 1000 };
        Assert.Equal(ArchiveReason.Wipe, e.Evaluate(in deadAgain));   // fresh death — new edge fires
    }

    [Fact]
    public void Wipe_fires_on_outcome_failed_edge()
    {
        var e = Armed(Live());
        var s = Live() with { OutcomeFailed = true };
        Assert.Equal(ArchiveReason.Wipe, e.Evaluate(in s));
        e.OnArchived(s.NowMs, ArchiveReason.Wipe);
        var later = s with { NowMs = s.NowMs + AutoArchiveEngine.DefaultCooldownMs + 1 };
        Assert.Null(e.Evaluate(in later));                       // edge consumed, sticky Failed doesn't refire
    }

    [Fact]
    public void Wipe_overlap_alldead_then_outcomefailed_fires_once()
    {
        // Restored to ORIGINAL (round 1) past-cooldown timing per round-3 integrity rule: allDead
        // fires first; OutcomeFailed flips true a tick later, past the cooldown the first archive
        // armed, while the party is STILL all dead throughout. Under round 3's episode latch,
        // dedup is neither "coupled latch" (round 1) nor "cooldown gate" (round 2) — it's simply
        // that `_wipeArchived` never got a chance to clear, because `allDead` never went false in
        // between. The level condition, not the timing relative to the cooldown, is what proves
        // single-fire here.
        var e = Armed(Live());
        e.WipeGraceMs = 0;   // s1 fires on the SAME tick allDead turns true — isolate from revive-grace
        var s1 = Live() with { DeadCount = 4 };
        Assert.Equal(ArchiveReason.Wipe, e.Evaluate(in s1));
        e.OnArchived(s1.NowMs, ArchiveReason.Wipe);
        var s2 = s1 with { NowMs = s1.NowMs + AutoArchiveEngine.DefaultCooldownMs + 1, OutcomeFailed = true };
        Assert.Null(e.Evaluate(in s2));   // still all dead, outcome now failed too — no duplicate archive
    }

    [Fact]
    public void Wipe_overlap_outcomefailed_then_alldead_fires_once()
    {
        // Mirror order, restored to ORIGINAL (round 1) past-cooldown timing: OutcomeFailed fires
        // first; allDead catches up a tick later, past cooldown, while OutcomeFailed is still true
        // the whole time (sticky) — `_wipeArchived` never clears because `allDead` was never false
        // after the first fire (it went straight from false to true across the two ticks with no
        // gap for recovery to run in between the archive and the catch-up tick).
        var e = Armed(Live());
        var s1 = Live() with { OutcomeFailed = true };
        Assert.Equal(ArchiveReason.Wipe, e.Evaluate(in s1));
        e.OnArchived(s1.NowMs, ArchiveReason.Wipe);
        var s2 = s1 with { NowMs = s1.NowMs + AutoArchiveEngine.DefaultCooldownMs + 1, DeadCount = 4 };
        Assert.Null(e.Evaluate(in s2));   // outcome still failed, now all dead too — no duplicate archive
    }

    [Fact]
    public void Wipe_second_independent_wipe_in_same_run_fires()
    {
        // RED-first pin (round 2): allDead is MOMENTARY (clears the instant anyone revives) while
        // OutcomeFailed is STICKY at run level (stays true until a brand-new run). ea58a42's coupled
        // AND re-arm (`!allDead && !OutcomeFailed`) can never clear once OutcomeFailed sticks true,
        // wedging every later independent wipe in the same run. This FAILS on ea58a42.
        var e = Armed(Live());
        e.WipeGraceMs = 0;   // wipe2's fire relies on allDead's fresh edge alone (OutcomeFailed can't re-edge, sticky) on the SAME tick it turns true — isolate from revive-grace
        var wipe1 = Live() with { DeadCount = 4, OutcomeFailed = true };
        Assert.Equal(ArchiveReason.Wipe, e.Evaluate(in wipe1));
        e.OnArchived(wipe1.NowMs, ArchiveReason.Wipe);

        var revived = wipe1 with
        {
            DeadCount = 0, NowMs = wipe1.NowMs + AutoArchiveEngine.DefaultCooldownMs + 1_000,
        };
        Assert.Null(e.Evaluate(in revived));           // OutcomeFailed still true (sticky), nobody's dead

        var wipe2 = revived with { DeadCount = 4, NowMs = revived.NowMs + 5_000 };
        Assert.Equal(ArchiveReason.Wipe, e.Evaluate(in wipe2));   // second, independent wipe — must fire
    }

    [Fact]
    public void Wipe_double_signal_wide_gap_party_stays_dead_fires_once()
    {
        // RED-first pin (round 3): allDead fires once; the party never recovers (stays fully dead);
        // well past the first archive's cooldown, OutcomeFailed also rises. This is still ONE
        // episode (nobody was ever alive in between), so it must be a SINGLE total archive. On
        // 85766da's pure edge model, OutcomeFailed rising is a fresh edge in its own right
        // (independent of allDead) and, landing past the first archive's cooldown, fires a SECOND
        // Wipe — a double-fire for one continuous wipe. This FAILS on 85766da. (NowMs is kept inside
        // Live()'s Idle timeout window — CooldownMs + 5_000 past wipe1, only 55s since LastDamageMs
        // vs the 60s IdleTimeoutMs default — so this pins ONLY the wipe double-fire, not a
        // coincidental Idle fire from the elapsed time.)
        var e = Armed(Live());
        e.WipeGraceMs = 0;   // wipe1 fires on the SAME tick allDead turns true — isolate from revive-grace
        var wipe1 = Live() with { DeadCount = 4 };
        Assert.Equal(ArchiveReason.Wipe, e.Evaluate(in wipe1));
        e.OnArchived(wipe1.NowMs, ArchiveReason.Wipe);

        var later = wipe1 with
        {
            NowMs = wipe1.NowMs + AutoArchiveEngine.DefaultCooldownMs + 5_000, OutcomeFailed = true,
        };   // party still all dead throughout — no revive ever happened
        Assert.Null(e.Evaluate(in later));   // must NOT re-fire — same episode, already archived
    }

    [Fact]
    public void Wipe_alldead_rises_during_unrelated_cooldown_then_fires_on_lift()
    {
        // RED-first pin (round 3): an unrelated archive (SceneChange) arms the cooldown; allDead
        // rises INSIDE that window and stays true throughout. allDead is a LEVEL condition, not a
        // one-shot edge — a wipe that rises during someone else's cooldown must not be lost, it
        // must fire the instant the cooldown lifts. On 85766da's pure edge model, the rising tick
        // stamps _prevAllDead=true even though the fire was cooldown-suppressed, so by the time the
        // cooldown lifts there is no NEW edge (allDead was already true last tick) and the wipe is
        // silently lost forever. This FAILS on 85766da.
        var e = Armed(Live());
        e.OnArchived(Live().NowMs, ArchiveReason.SceneChange);                      // unrelated archive arms cooldown
        var rising = Live() with { DeadCount = 4, NowMs = Live().NowMs + 2_000 };   // allDead rises inside cooldown
        Assert.Null(e.Evaluate(in rising));                                        // cooldown suppresses the fire
        var stillDead = rising with { NowMs = rising.NowMs + 3_000 };              // still inside cooldown, still dead
        Assert.Null(e.Evaluate(in stillDead));                                     // still suppressed
        var afterLift = stillDead with { NowMs = Live().NowMs + AutoArchiveEngine.DefaultCooldownMs + 1 };
        Assert.Equal(ArchiveReason.Wipe, e.Evaluate(in afterLift));                // level persisted — fires on lift
    }

    [Fact]
    public void Wipe_outcomefailed_only_edge_lost_inside_unrelated_cooldown_is_accepted_residual()
    {
        // ACCEPTED RESIDUAL (round 3, documented — not fixed): unlike allDead (fixed above to be
        // level-gated), OutcomeFailed-only still needs a one-shot edge, because it's sticky — a
        // level check on OutcomeFailed alone would fire every single tick once true. That one-shot
        // edge is stamped into _prevOutcomeFailed every tick, including the tick a cooldown
        // swallows the fire — so an OutcomeFailed-only wipe (no allDead ever) whose edge rises
        // during an unrelated archive's cooldown is genuinely lost: by the time the cooldown lifts,
        // _prevOutcomeFailed is already true, there is no new edge, and the sticky signal can never
        // produce a second one to retry. This requires a no-allDead wipe landing within CooldownMs
        // of an unrelated archive — doubly rare, since allDead is the primary wipe signal and it is
        // level-gated (see Wipe_alldead_rises_during_unrelated_cooldown_then_fires_on_lift), so the
        // real-world case that matters never suffers this loss.
        var e = Armed(Live());
        e.OnArchived(Live().NowMs, ArchiveReason.SceneChange);                          // unrelated archive arms cooldown
        var rising = Live() with { OutcomeFailed = true, NowMs = Live().NowMs + 2_000 }; // edge rises inside cooldown
        Assert.Null(e.Evaluate(in rising));                                             // suppressed — edge consumed here
        var afterLift = rising with { NowMs = Live().NowMs + AutoArchiveEngine.DefaultCooldownMs + 1 };
        Assert.Null(e.Evaluate(in afterLift));   // accepted loss: sticky signal, no new edge, allDead never true
    }

    // ---- wipe revive-grace + ignore-solo ----

    [Fact]
    public void Wipe_waits_out_revive_grace_and_a_revive_cancels_it()
    {
        var e = Armed(Live());
        var t0 = Live() with { DeadCount = 4, NowMs = 210_000 };
        Assert.Null(e.Evaluate(in t0));                                   // all-dead just started — within grace
        var revived = t0 with { DeadCount = 3, NowMs = 211_000 };          // revive inside the 2s grace
        Assert.Null(e.Evaluate(in revived));                              // cancelled, no wipe
        var deadAgain = revived with { DeadCount = 4, NowMs = 212_000 };   // dies again — grace restarts
        Assert.Null(e.Evaluate(in deadAgain));
        var held = deadAgain with { NowMs = deadAgain.NowMs + AutoArchiveEngine.DefaultCooldownMs + 2001 }; // held >= grace, cooldown clear
        Assert.Equal(ArchiveReason.Wipe, e.Evaluate(in held));
    }

    [Fact]
    public void Wipe_outcome_failed_fires_immediately_ignoring_grace()
    {
        // Both signals present on the SAME just-started tick: DeadCount==RosterSize means allDead is
        // true but allDeadHeld is false (0ms held, under the 2000ms default grace) — if outcomeEdge
        // were coupled to the debounce at all, this would fire null. It fires Wipe immediately, proving
        // outcomeEdge (server-authoritative OutcomeFailed) is wired independently of allDeadHeld/grace.
        var e = Armed(Live());
        var failed = Live() with { OutcomeFailed = true, DeadCount = 4, NowMs = 210_000 };
        Assert.Equal(ArchiveReason.Wipe, e.Evaluate(in failed));   // server-authoritative fail = immediate
    }

    [Fact]
    public void Wipe_ignore_solo_skips_a_solo_death_but_party_wipe_still_fires()
    {
        var e = Armed(Live());
        e.WipeIgnoreSolo = true;
        e.WipeGraceMs = 0;   // isolate the solo gate from grace
        var solo = Live() with { RosterSize = 1, DeadCount = 1, NowMs = 210_000 };
        Assert.Null(e.Evaluate(in solo));
        var party = Live() with { RosterSize = 4, DeadCount = 4, NowMs = 220_000 };
        Assert.Equal(ArchiveReason.Wipe, e.Evaluate(in party));
    }

    [Fact]
    public void Wipe_and_stage_tie_at_default_grace_labels_stagechange()
    {
        // Deliberate, tracked tie-break — default WipeGraceMs (2000), NOT overridden to 0. When
        // allDead and a stage transition into a run-end state land on the SAME tick, allDead has
        // only just started (0ms held), so allDeadHeld is false and the grace debounce yields the
        // tick to StageChange instead of Wipe. This is the canonical default-grace pin for that
        // tie-break (see the migration comment on
        // Stage_transition_banked_across_an_overlapping_archive_is_consumed, which isolates ITSELF
        // from grace via WipeGraceMs=0 rather than pinning the tie-break). Coverage is preserved —
        // an archive still fires at this exact tick, cutting the segment with no gap — only the
        // trigger LABEL changes; a genuine mid-run wipe with no coinciding stage transition still
        // fires Wipe once grace elapses (see Wipe_waits_out_revive_grace_and_a_revive_cancels_it).
        var e = Armed(Live());
        var tie = Live() with { DeadCount = 4, FlowStateVersion = 2, CurrentFlowState = DungeonFlowState.End };
        Assert.Equal(ArchiveReason.StageChange, e.Evaluate(in tie));   // archive fires — labeled Stage, not Wipe
    }

    // ---- boss phase ----

    [Fact]
    public void Boss_sighting_cuts_the_trash_segment_once()
    {
        var e = Armed(Live());
        var s = Live() with { BossPresent = true };
        Assert.Equal(ArchiveReason.BossPhase, e.Evaluate(in s));
        e.OnArchived(s.NowMs, ArchiveReason.BossPhase);
        var later = s with { NowMs = s.NowMs + AutoArchiveEngine.DefaultCooldownMs + 1 };
        Assert.Null(e.Evaluate(in later));                       // boss segment active — no refire
    }

    [Fact]
    public void Boss_rearms_after_boss_gone()
    {
        var e = Armed(Live());
        e.BossRecutOnRedetect = true;   // pins the legacy re-detect path explicitly (found breaking
                                         // under the new recut-off default while implementing the
                                         // boss re-cut fix — same category as the two brief-named
                                         // migrations, not itself in that list)
        var s = Live() with { BossPresent = true };
        Assert.Equal(ArchiveReason.BossPhase, e.Evaluate(in s));
        e.OnArchived(s.NowMs, ArchiveReason.BossPhase);
        var gone = s with { BossPresent = false, BossGone = true, NowMs = s.NowMs + AutoArchiveEngine.DefaultCooldownMs + 1 };
        Assert.Null(e.Evaluate(in gone));
        var next = gone with { BossPresent = true, BossGone = false, NowMs = gone.NowMs + AutoArchiveEngine.DefaultCooldownMs + 1 };
        Assert.Equal(ArchiveReason.BossPhase, e.Evaluate(in next));
    }

    [Fact]
    public void NonBoss_archive_ends_the_boss_segment()
    {
        var e = Armed(Live());
        e.BossRecutOnRedetect = true;   // pins the legacy re-detect path explicitly
        var s = Live() with { BossPresent = true };
        Assert.Equal(ArchiveReason.BossPhase, e.Evaluate(in s));
        e.OnArchived(s.NowMs, ArchiveReason.BossPhase);
        e.OnArchived(s.NowMs + 1000, ArchiveReason.Manual);      // user archived mid-boss — segment over
        var later = s with { NowMs = s.NowMs + 1000 + AutoArchiveEngine.DefaultCooldownMs + 1 };
        Assert.Equal(ArchiveReason.BossPhase, e.Evaluate(in later));
    }

    [Fact]
    public void Boss_seen_and_gone_entirely_within_cooldown_still_cuts_once_lifted()
    {
        // Judgment call (Fix 3, review round): a boss that starts AND ends inside one cooldown
        // window used to be lost entirely (fire branch unreachable while cooldown blocks, and
        // BossPresent reads false again by the time cooldown lifts). Bank the sighting like
        // _stagePending and consume it once the cooldown clears.
        var e = Armed(Live());
        e.OnArchived(Live().NowMs, ArchiveReason.SceneChange);              // arm the cooldown
        var sighted = Live() with { BossPresent = true, NowMs = Live().NowMs + 2000 };
        Assert.Null(e.Evaluate(in sighted));                                // cooldown swallows the live fire
        var goneAlready = sighted with { BossPresent = false, BossGone = true, NowMs = sighted.NowMs + 3000 };
        Assert.Null(e.Evaluate(in goneAlready));                            // boss already left, still cooling down
        var cooldownLifted = goneAlready with { NowMs = Live().NowMs + AutoArchiveEngine.DefaultCooldownMs + 1 };
        Assert.Equal(ArchiveReason.BossPhase, e.Evaluate(in cooldownLifted)); // banked sighting fires once able
    }

    [Fact]
    public void Boss_pending_cleared_by_a_superseding_nonboss_archive()
    {
        // A banked boss sighting must not resurface once another trigger already cut a fresh
        // segment boundary in the meantime — same "supersede" rule as _stagePending.
        var e = Armed(Live());
        e.OnArchived(Live().NowMs, ArchiveReason.SceneChange);
        var sighted = Live() with { BossPresent = true, NowMs = Live().NowMs + 2000 };
        Assert.Null(e.Evaluate(in sighted));                                // banks _bossPending
        e.OnArchived(sighted.NowMs, ArchiveReason.Manual);                  // manual archive supersedes it
        var later = sighted with
        {
            BossPresent = false, NowMs = sighted.NowMs + AutoArchiveEngine.DefaultCooldownMs + 1,
        };
        Assert.Null(e.Evaluate(in later));                                  // no stale BossPhase resurfaces
    }

    [Fact]
    public void Boss_stale_banked_fire_does_not_wedge_next_real_boss()
    {
        // RED-first pin (round 2): firing off a stale banked _bossPending where BossPresent is
        // ALREADY false (boss came and went entirely inside one cooldown window) must not mark a
        // boss segment "active" — there is no live segment to close later. On ea58a42 the fire
        // branch unconditionally sets _bossSegmentActive = true, and OnArchived's re-arm only
        // clears it on a NON-boss archive — so a BossPhase archive never clears it, wedging every
        // later real boss engagement behind the !_bossSegmentActive gate forever. This FAILS on
        // ea58a42.
        var e = Armed(Live());
        e.BossRecutOnRedetect = true;   // pins the legacy re-detect path explicitly
        e.OnArchived(Live().NowMs, ArchiveReason.SceneChange);                 // arm the cooldown
        var sighted = Live() with { BossPresent = true, NowMs = Live().NowMs + 2000 };
        Assert.Null(e.Evaluate(in sighted));                                   // banks _bossPending, cooldown blocks
        var goneAlready = sighted with { BossPresent = false, BossGone = true, NowMs = sighted.NowMs + 3000 };
        Assert.Null(e.Evaluate(in goneAlready));                               // boss already left, still cooling down
        var cooldownLifted = goneAlready with { NowMs = Live().NowMs + AutoArchiveEngine.DefaultCooldownMs + 1 };
        Assert.Equal(ArchiveReason.BossPhase, e.Evaluate(in cooldownLifted));  // stale banked sighting fires once able
        e.OnArchived(cooldownLifted.NowMs, ArchiveReason.BossPhase);

        // A genuinely new boss engagement, well after, must fire too — not wedged by a phantom
        // "segment active" left over from the stale fire above.
        var newBoss = cooldownLifted with
        {
            BossPresent = true, BossGone = false, NowMs = cooldownLifted.NowMs + AutoArchiveEngine.DefaultCooldownMs + 1,
        };
        Assert.Equal(ArchiveReason.BossPhase, e.Evaluate(in newBoss));
    }

    [Fact]
    public void MinBossSegment_suppresses_a_too_short_boss_cut()
    {
        var e = Armed(Live());
        e.MinBossSegmentMs = 10_000;
        // Boss sighted only 3s into combat (CombatStartMs 100_000, now 103_000) — below the floor.
        var earlyBoss = Live() with { BossPresent = true, CombatStartMs = 100_000, NowMs = 103_000 };
        Assert.Null(e.Evaluate(in earlyBoss));
        // Same boss, now 12s in — above the floor.
        var laterBoss = earlyBoss with { NowMs = 112_000 };
        Assert.Equal(ArchiveReason.BossPhase, e.Evaluate(in laterBoss));
    }

    [Fact]
    public void MinBossSegment_suppresses_pending_path_fire()
    {
        // The floor must gate the _bossPending fire path too, not just the live BossPresent path —
        // otherwise a sighting banked while too-short, then vanishing before the floor lifts, fires a
        // sub-floor sliver via the banked-pending branch instead of the live one.
        var e = Armed(Live());
        e.MinBossSegmentMs = 10_000;
        // Boss sighted 3s into combat — floored on the live path, banks _bossPending regardless.
        var sighted = Live() with { BossPresent = true, CombatStartMs = 100_000, NowMs = 103_000 };
        Assert.Null(e.Evaluate(in sighted));
        // Boss vanishes a tick later, still only 4s in — the pending path must be floored too.
        var vanished = sighted with { BossPresent = false, BossGone = true, NowMs = 104_000 };
        Assert.Null(e.Evaluate(in vanished));
        // Now 12s in — past the floor — the banked pending sighting fires.
        var later = vanished with { NowMs = 112_000 };
        Assert.Equal(ArchiveReason.BossPhase, e.Evaluate(in later));
    }

    // ---- boss re-cut fix (default: one fight = one cut) ----

    // Baseline: a boss is present and a boss segment is active (first boss sighting already fired).
    private static (AutoArchiveEngine e, AutoArchiveInputs bossOn) BossEngaged()
    {
        var e = Armed(Live());
        // Isolate boss-recut behavior from Idle: NowMs climbs well past Live()'s fixed LastDamageMs
        // across these tests' later ticks, which would otherwise let a stale Idle timeout leak
        // through once the boss branch stops firing (the very thing under test here) — found while
        // running this suite; not itself part of what the brief's tests were pinning.
        e.IdleEnabled = false;
        var sighting = Live() with { BossPresent = true, NowMs = 260_000 };
        Assert.Equal(ArchiveReason.BossPhase, e.Evaluate(in sighting));   // first cut
        e.OnArchived(sighting.NowMs, ArchiveReason.BossPhase);
        return (e, sighting);
    }

    [Fact]
    public void Boss_transient_eviction_does_not_recut_when_recut_off()
    {
        var (e, on) = BossEngaged();   // BossRecutOnRedetect defaults false
        // Cache blinks: boss "gone" via eviction (NOT a real death), boss still present next tick.
        var evicted = on with { BossPresent = false, BossGone = true, BossDead = false, NowMs = on.NowMs + AutoArchiveEngine.DefaultCooldownMs + 1 };
        Assert.Null(e.Evaluate(in evicted));
        var back = on with { BossPresent = true, NowMs = evicted.NowMs + 500 };
        Assert.Null(e.Evaluate(in back));   // must NOT re-cut — one fight, one cut
    }

    [Fact]
    public void Boss_intervening_manual_archive_does_not_recut_when_recut_off()
    {
        var (e, on) = BossEngaged();
        e.OnArchived(on.NowMs + 100, ArchiveReason.Manual);   // a manual archive mid-boss
        var still = on with { BossPresent = true, NowMs = on.NowMs + AutoArchiveEngine.DefaultCooldownMs + 200 };
        Assert.Null(e.Evaluate(in still));   // boss still present, but no re-cut
    }

    [Fact]
    public void Boss_confirmed_death_then_new_boss_recuts_even_when_recut_off()
    {
        var (e, on) = BossEngaged();
        var dead = on with { BossPresent = false, BossGone = true, BossDead = true, NowMs = on.NowMs + AutoArchiveEngine.DefaultCooldownMs + 1 };
        Assert.Null(e.Evaluate(in dead));   // death itself doesn't archive-by-boss; segment ends
        var newBoss = dead with { BossPresent = true, BossGone = false, BossDead = false, NowMs = dead.NowMs + 5000 };
        Assert.Equal(ArchiveReason.BossPhase, e.Evaluate(in newBoss));   // a genuinely new fight re-cuts
    }

    [Fact]
    public void Boss_recut_on_preserves_legacy_redetect_behavior()
    {
        var e = Armed(Live());
        e.IdleEnabled = false;   // isolate boss behavior from Idle — see BossEngaged()'s comment
        e.BossRecutOnRedetect = true;
        var sighting = Live() with { BossPresent = true, NowMs = 260_000 };
        Assert.Equal(ArchiveReason.BossPhase, e.Evaluate(in sighting));
        e.OnArchived(sighting.NowMs, ArchiveReason.BossPhase);
        var evicted = sighting with { BossPresent = false, BossGone = true, NowMs = sighting.NowMs + AutoArchiveEngine.DefaultCooldownMs + 1 };
        Assert.Null(e.Evaluate(in evicted));
        var back = sighting with { BossPresent = true, NowMs = evicted.NowMs + 500 };
        Assert.Equal(ArchiveReason.BossPhase, e.Evaluate(in back));   // legacy: re-detect re-cuts
    }

    [Fact]
    public void Boss_run_boundary_resets_segment_for_next_run()
    {
        // Gap closed post-review: with BossRecutOnRedetect=false (default), _bossSegmentActive only
        // reset on a CONFIRMED death — but a boss fight very often ends WITHOUT one (you leave, the
        // party wipes, the run just ends). Without a run-boundary reset, the segment would stay
        // "active" for the rest of the SESSION, and every later dungeon run's boss would go uncut.
        // Leaving the instanced run (InstancedRun -> false) must end the segment so the NEXT run's
        // boss gets a fresh cut, even though BossDead was never observed.
        var (e, on) = BossEngaged();   // first boss cut already fired; segment active; recut off (default)
        var left = on with { BossPresent = false, InstancedRun = false, NowMs = on.NowMs + AutoArchiveEngine.DefaultCooldownMs + 1 };
        Assert.Null(e.Evaluate(in left));   // leaving itself doesn't archive-by-boss
        var newRunBoss = left with { InstancedRun = true, BossPresent = true, NowMs = left.NowMs + 5000 };
        Assert.Equal(ArchiveReason.BossPhase, e.Evaluate(in newRunBoss));   // new run's boss re-cuts
    }

    // ---- overlap: a banked stage transition must not survive an overlapping archive ----

    [Fact]
    public void Stage_transition_banked_across_an_overlapping_archive_is_consumed()
    {
        // Spec: "a shared 10 s cooldown prevents double-archives when triggers overlap (e.g. wipe +
        // stage change)". A banked _stagePending surviving the wipe's OnArchived and firing a stale
        // StageChange archive once stats re-accumulate IS a double-archive in slow motion — pin that
        // OnArchived consumes any pending transition, whichever trigger actually fired.
        var e = new AutoArchiveEngine();
        // Isolates ITSELF from revive-grace (the wipe below must win the tick over StageChange on the
        // SAME tick allDead turns true) — this test pins the OnArchived-consumes-pending-transition
        // behavior, not the wipe/stage tie-break. See Wipe_and_stage_tie_at_default_grace_labels_stagechange
        // for the canonical pin of what happens at the DEFAULT grace when the two genuinely tie.
        e.WipeGraceMs = 0;
        Assert.Null(e.Evaluate(Live()));                                  // adopt flow version 1
        // Transition into End (a run-END state that DOES arm under the new rule) AND a wipe overlap.
        var overlap = Live() with { FlowStateVersion = 2, CurrentFlowState = DungeonFlowState.End, DeadCount = 4 };
        Assert.Equal(ArchiveReason.Wipe, e.Evaluate(in overlap));          // wipe is checked first, wins the tick
        e.OnArchived(overlap.NowMs, ArchiveReason.Wipe);
        var later = overlap with { NowMs = overlap.NowMs + AutoArchiveEngine.DefaultCooldownMs + 1, DeadCount = 0 };
        Assert.Null(e.Evaluate(in later));                                // no stale StageChange fires later
    }

    // ---- idle ----

    [Fact]
    public void Idle_fires_after_timeout_with_content()
    {
        var e = Armed(Live());
        // 60s content (100k->160k), last damage 61s ago.
        var s = Live() with { NowMs = 160_000 + 61_000 };
        Assert.Equal(ArchiveReason.Idle, e.Evaluate(in s));
    }

    [Fact]
    public void Idle_content_guard_blocks_trivial_segments()
    {
        var e = Armed(Live());
        // Only 10s of combat span — under MinContentMs.
        var s = Live() with { CombatStartMs = 150_000, NowMs = 160_000 + 61_000 };
        Assert.Null(e.Evaluate(in s));
    }

    [Fact]
    public void Idle_respects_configured_timeout()
    {
        var e = Armed(Live());
        e.IdleTimeoutMs = 120_000;
        Assert.Null(e.Evaluate(Live(160_000 + 61_000)));
        var s = Live() with { NowMs = 160_000 + 121_000 };
        Assert.Equal(ArchiveReason.Idle, e.Evaluate(in s));
    }

    // ---- idle re-fire guard (Fix 2, review round): the ONLY thing stopping Idle from refiring
    // every cooldown window is `!s.CombatActive` — the real re-arm happens out-of-engine (caller's
    // archive -> Clear() -> CombatActive false). Pin that this single guard actually holds, and
    // that it's genuinely re-armable rather than a one-way latch. ----

    [Fact]
    public void Idle_does_not_refire_while_combat_stays_inactive_after_archive()
    {
        var e = Armed(Live());
        var s = Live() with { NowMs = 221_000 };   // 61s after last damage, 60s of content — idle fires
        Assert.Equal(ArchiveReason.Idle, e.Evaluate(in s));
        e.OnArchived(s.NowMs, ArchiveReason.Idle);
        var cleared = s with { CombatActive = false, NowMs = s.NowMs + AutoArchiveEngine.DefaultCooldownMs + 1 };
        Assert.Null(e.Evaluate(in cleared));       // past cooldown, but CombatActive=false blocks
        var stillInactive = cleared with { NowMs = cleared.NowMs + AutoArchiveEngine.DefaultCooldownMs + 1 };
        Assert.Null(e.Evaluate(in stillInactive)); // a second window later — still no refire
    }

    [Fact]
    public void Idle_blocked_when_no_damage_ever_recorded()
    {
        // Fixed (reviewer minor, round 2): pre-fix this passed for the wrong reason — with the
        // Live() baseline CombatStartMs (100_000), the content-guard math alone already failed
        // (0 - 100_000 = -100_000, well under MinContentMs) regardless of the explicit
        // `LastDamageMs == 0` guard. Override CombatStartMs so the content-guard math would PASS on
        // its own (LastDamageMs - CombatStartMs >= MinContentMs), isolating the explicit guard as
        // the ONLY thing that can be blocking the fire.
        var e = Armed(Live());
        var s = Live() with { LastDamageMs = 0, CombatStartMs = -40_000, NowMs = 300_000 };
        Assert.Null(e.Evaluate(in s));             // LastDamageMs == 0 blocks even though content-guard math would pass
    }

    [Fact]
    public void Idle_refires_after_fresh_combat_following_the_clear()
    {
        var e = Armed(Live());
        var s = Live() with { NowMs = 221_000 };
        Assert.Equal(ArchiveReason.Idle, e.Evaluate(in s));
        e.OnArchived(s.NowMs, ArchiveReason.Idle);
        var cleared = s with { CombatActive = false, NowMs = s.NowMs + 1000 };
        Assert.Null(e.Evaluate(in cleared));
        // Fresh combat starts: CombatActive true again with a new span, well past cooldown + idle timeout.
        var fresh = cleared with
        {
            CombatActive = true, CombatStartMs = 225_000, LastDamageMs = 256_000, NowMs = 316_001,
        };
        Assert.Equal(ArchiveReason.Idle, e.Evaluate(in fresh)); // fresh combat re-enables a later idle fire
    }

    // ---- stage change ----

    [Fact]
    public void Stage_transition_into_run_end_fires_and_first_observation_is_silent()
    {
        var e = new AutoArchiveEngine();
        Assert.Null(e.Evaluate(Live()));                          // first sight of version 1: adopt, no fire
        var s = Live() with { FlowStateVersion = 2, CurrentFlowState = DungeonFlowState.End };
        Assert.Equal(ArchiveReason.StageChange, e.Evaluate(in s));
    }

    [Fact]
    public void Stage_version_reset_is_adopted_silently()
    {
        var e = new AutoArchiveEngine();
        Assert.Null(e.Evaluate(Live() with { FlowStateVersion = 5 }));
        // New run resets the service counter to a LOWER value — adopt, never fire.
        Assert.Null(e.Evaluate(Live() with { FlowStateVersion = 1 }));
    }

    [Fact]
    public void Stage_outside_instanced_run_never_fires()
    {
        var e = Armed(Live());
        // Into End (would arm under the new rule) but not in an instanced run — never fires.
        var s = Live() with { FlowStateVersion = 2, CurrentFlowState = DungeonFlowState.End, InstancedRun = false };
        Assert.Null(e.Evaluate(in s));
    }

    [Fact]
    public void Stage_pending_discarded_by_flowversion_decrease_mid_cooldown()
    {
        // Pin current behavior (accepted as correct new-run-reset semantics): a _stagePending
        // banked mid-cooldown is unconditionally overwritten — not merely superseded — by the next
        // version change. If that next change is a DECREASE (a new run resetting the service's
        // counter), the banked transition is silently discarded rather than surviving to fire once
        // the cooldown lifts. This is intentional: a version decrease means "new run", and a stale
        // pre-reset transition archive would be meaningless in the new run's context.
        var e = new AutoArchiveEngine();
        Assert.Null(e.Evaluate(Live()));                                      // adopt version 1
        e.OnArchived(Live().NowMs, ArchiveReason.SceneChange);                // arm the cooldown
        var banked = Live() with { FlowStateVersion = 2, CurrentFlowState = DungeonFlowState.End, NowMs = Live().NowMs + 2000 };
        Assert.Null(e.Evaluate(in banked));                                   // banks _stagePending (into End), cooldown blocks
        var reset = banked with { FlowStateVersion = 1, NowMs = banked.NowMs + 1000 };
        Assert.Null(e.Evaluate(in reset));                                    // decrease discards the banked pending
        var cooldownLifted = reset with { NowMs = Live().NowMs + AutoArchiveEngine.DefaultCooldownMs + 1 };
        Assert.Null(e.Evaluate(in cooldownLifted));                           // no stale StageChange resurfaces
    }

    // ---- stage change: arms ONLY on transitions INTO a run-END state (owner ruling 2026-07-20) ----
    // End(4)/Settlement(5)/Vote(6) arm; entry-side transitions (into Active/Ready/Playing, or anything
    // else) never arm — a player poking a boss coincides with ->Playing, and cutting an archive of just
    // the opener there is wrong. The pre-pull opener now stays accumulated and lands in the next segment.

    [Fact]
    public void Stage_entry_transition_into_playing_does_not_arm()
    {
        // (a) A real version bump whose landing state is Playing (entry-side) must NOT arm. This is the
        // motivating case: engaging a boss bumps the flow to Playing and would otherwise cut the opener.
        var e = Armed(Live());
        var s = Live() with { FlowStateVersion = 2, CurrentFlowState = DungeonFlowState.Playing };
        Assert.Null(e.Evaluate(in s));
    }

    [Fact]
    public void Stage_transition_into_end_arms()
    {
        // (b) A version bump landing in End (run-END) arms and fires StageChange (the deferred-commit /
        // quiet-settle wait after that is Plugin.AutoArchive's job — see AutoArchiveSettleDelayTests).
        var e = Armed(Live());
        var s = Live() with { FlowStateVersion = 2, CurrentFlowState = DungeonFlowState.End };
        Assert.Equal(ArchiveReason.StageChange, e.Evaluate(in s));
    }

    [Fact]
    public void Stage_transition_into_settlement_arms()
    {
        // (c) Settlement is also a run-END state — arms.
        var e = Armed(Live());
        var s = Live() with { FlowStateVersion = 2, CurrentFlowState = DungeonFlowState.Settlement };
        Assert.Equal(ArchiveReason.StageChange, e.Evaluate(in s));
    }

    [Fact]
    public void Stage_same_version_redelivery_in_end_state_does_not_rearm()
    {
        // (d) belt-and-braces: the framework only bumps FlowStateVersion on a real change, but a
        // same-version re-delivery while sitting in End must not produce a second arm. First arm+fire
        // on the transition into End, then a same-version (no bump) re-delivery yields nothing.
        var e = Armed(Live());
        var end = Live() with { FlowStateVersion = 2, CurrentFlowState = DungeonFlowState.End };
        Assert.Equal(ArchiveReason.StageChange, e.Evaluate(in end));
        e.OnArchived(end.NowMs, ArchiveReason.StageChange);
        var redelivered = end with { NowMs = end.NowMs + AutoArchiveEngine.DefaultCooldownMs + 1 };   // same version, still End
        Assert.Null(e.Evaluate(in redelivered));
    }

    // ---- shared gates ----

    [Fact]
    public void Cooldown_spans_all_triggers_including_manual_archives()
    {
        // Restored to ORIGINAL (round 1) timing/assertions per round-3 integrity rule: a scene
        // archive arms the cooldown; allDead rises INSIDE that window (suppressed); the SAME
        // allDead level, still true, fires the instant the window lifts — no revive, no fresh
        // OutcomeFailed edge needed. This is exactly the "allDead rising during an unrelated
        // cooldown then staying true fires on lift" behavior round 3 restores (see
        // <see cref="Wipe_alldead_rises_during_unrelated_cooldown_then_fires_on_lift"/>): the level
        // condition persists through the suppressed tick because `_wipeArchived` is only latched
        // true at the moment of an actual fire, never while cooldown-suppressed.
        var e = Armed(Live());
        e.WipeGraceMs = 0;   // the fire below lands only 2ms after allDead turns true (well under the 2000ms default grace) — isolate from revive-grace
        e.OnArchived(Live().NowMs, ArchiveReason.SceneChange);    // scene archive arms the cooldown
        var s = Live() with { DeadCount = 4, NowMs = Live().NowMs + AutoArchiveEngine.DefaultCooldownMs - 1 };
        Assert.Null(e.Evaluate(in s));                            // wipe suppressed inside the window
        var later = s with { NowMs = s.NowMs + 2 };
        Assert.Equal(ArchiveReason.Wipe, e.Evaluate(in later));
    }

    [Fact]
    public void Cooldown_is_configurable()
    {
        var e = Armed(Live());
        e.WipeGraceMs = 0;   // isolate CooldownMs configurability from the unrelated revive-grace
                              // debounce — both fires below land on the SAME tick allDead turns true
        e.CooldownMs = 30_000;
        var dead = Live() with { DeadCount = 4, NowMs = 210_000 };
        Assert.Equal(ArchiveReason.Wipe, e.Evaluate(in dead));
        e.OnArchived(dead.NowMs, ArchiveReason.Wipe);
        var revived = dead with { DeadCount = 0, NowMs = dead.NowMs + 1000 };  // re-arm the episode
        Assert.Null(e.Evaluate(in revived));
        var deadAgain = revived with { DeadCount = 4, NowMs = dead.NowMs + 20_000 }; // <30s cooldown
        Assert.Null(e.Evaluate(in deadAgain));
        var later = deadAgain with { NowMs = dead.NowMs + 30_001 };            // past 30s
        Assert.Equal(ArchiveReason.Wipe, e.Evaluate(in later));
    }

    [Fact]
    public void No_stats_means_no_fire()
    {
        var e = Armed(Live());
        var s = Live() with { HasStats = false, DeadCount = 4 };
        Assert.Null(e.Evaluate(in s));
    }

    [Fact]
    public void Toggles_off_never_fire()
    {
        var e = Armed(Live());
        e.WipeEnabled = false; e.BossEnabled = false; e.IdleEnabled = false; e.StageEnabled = false;
        var s = Live() with
        {
            DeadCount = 4, BossPresent = true, FlowStateVersion = 2, CurrentFlowState = DungeonFlowState.End,
            NowMs = 160_000 + 300_001,
        };
        Assert.Null(e.Evaluate(in s));
    }
}
