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
        OutcomeFailed = false, BossPresent = false, BossGone = false,
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
        var dead = Live() with { DeadCount = 4 };
        Assert.Equal(ArchiveReason.Wipe, e.Evaluate(in dead));
        e.OnArchived(dead.NowMs, ArchiveReason.Wipe);
        var later = dead with { NowMs = dead.NowMs + AutoArchiveEngine.CooldownMs + 1 };
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
        var later = s with { NowMs = s.NowMs + AutoArchiveEngine.CooldownMs + 1 };
        Assert.Null(e.Evaluate(in later));                       // edge consumed, sticky Failed doesn't refire
    }

    [Fact]
    public void Wipe_overlap_alldead_then_outcomefailed_fires_once()
    {
        // Adapted for round 2: allDead fires first; OutcomeFailed flips true a tick later while
        // still all dead AND still inside the shared cooldown that first fire armed. Dedupe is no
        // longer latch coupling (removed with the edge-trigger rewrite) but the shared cooldown —
        // the OutcomeFailed rising edge is real, but lands inside the still-active window, so it's
        // dropped (not deferred) -> single archive. (Was NowMs = s1.NowMs + CooldownMs + 1, i.e.
        // past cooldown, under round 1's latch mechanism; moved inside the window since edge
        // dedupe now only works via the cooldown gate.)
        var e = Armed(Live());
        var s1 = Live() with { DeadCount = 4 };
        Assert.Equal(ArchiveReason.Wipe, e.Evaluate(in s1));
        e.OnArchived(s1.NowMs, ArchiveReason.Wipe);
        var s2 = s1 with { NowMs = s1.NowMs + 2_000, OutcomeFailed = true };   // still within the 10s cooldown
        Assert.Null(e.Evaluate(in s2));   // outcome-failed edge cooldown-suppressed — no duplicate archive
    }

    [Fact]
    public void Wipe_overlap_outcomefailed_then_alldead_fires_once()
    {
        // Mirror order, same round-2 adaptation as above: OutcomeFailed fires first; allDead
        // catches up a tick later, still inside the shared cooldown -> cooldown-suppressed, not a
        // latch re-fire.
        var e = Armed(Live());
        var s1 = Live() with { OutcomeFailed = true };
        Assert.Equal(ArchiveReason.Wipe, e.Evaluate(in s1));
        e.OnArchived(s1.NowMs, ArchiveReason.Wipe);
        var s2 = s1 with { NowMs = s1.NowMs + 2_000, DeadCount = 4 };   // still within the 10s cooldown
        Assert.Null(e.Evaluate(in s2));   // allDead edge cooldown-suppressed — no duplicate archive
    }

    [Fact]
    public void Wipe_second_independent_wipe_in_same_run_fires()
    {
        // RED-first pin (round 2): allDead is MOMENTARY (clears the instant anyone revives) while
        // OutcomeFailed is STICKY at run level (stays true until a brand-new run). ea58a42's coupled
        // AND re-arm (`!allDead && !OutcomeFailed`) can never clear once OutcomeFailed sticks true,
        // wedging every later independent wipe in the same run. This FAILS on ea58a42.
        var e = Armed(Live());
        var wipe1 = Live() with { DeadCount = 4, OutcomeFailed = true };
        Assert.Equal(ArchiveReason.Wipe, e.Evaluate(in wipe1));
        e.OnArchived(wipe1.NowMs, ArchiveReason.Wipe);

        var revived = wipe1 with
        {
            DeadCount = 0, NowMs = wipe1.NowMs + AutoArchiveEngine.CooldownMs + 1_000,
        };
        Assert.Null(e.Evaluate(in revived));           // OutcomeFailed still true (sticky), nobody's dead

        var wipe2 = revived with { DeadCount = 4, NowMs = revived.NowMs + 5_000 };
        Assert.Equal(ArchiveReason.Wipe, e.Evaluate(in wipe2));   // second, independent wipe — must fire
    }

    // ---- boss phase ----

    [Fact]
    public void Boss_sighting_cuts_the_trash_segment_once()
    {
        var e = Armed(Live());
        var s = Live() with { BossPresent = true };
        Assert.Equal(ArchiveReason.BossPhase, e.Evaluate(in s));
        e.OnArchived(s.NowMs, ArchiveReason.BossPhase);
        var later = s with { NowMs = s.NowMs + AutoArchiveEngine.CooldownMs + 1 };
        Assert.Null(e.Evaluate(in later));                       // boss segment active — no refire
    }

    [Fact]
    public void Boss_rearms_after_boss_gone()
    {
        var e = Armed(Live());
        var s = Live() with { BossPresent = true };
        Assert.Equal(ArchiveReason.BossPhase, e.Evaluate(in s));
        e.OnArchived(s.NowMs, ArchiveReason.BossPhase);
        var gone = s with { BossPresent = false, BossGone = true, NowMs = s.NowMs + AutoArchiveEngine.CooldownMs + 1 };
        Assert.Null(e.Evaluate(in gone));
        var next = gone with { BossPresent = true, BossGone = false, NowMs = gone.NowMs + AutoArchiveEngine.CooldownMs + 1 };
        Assert.Equal(ArchiveReason.BossPhase, e.Evaluate(in next));
    }

    [Fact]
    public void NonBoss_archive_ends_the_boss_segment()
    {
        var e = Armed(Live());
        var s = Live() with { BossPresent = true };
        Assert.Equal(ArchiveReason.BossPhase, e.Evaluate(in s));
        e.OnArchived(s.NowMs, ArchiveReason.BossPhase);
        e.OnArchived(s.NowMs + 1000, ArchiveReason.Manual);      // user archived mid-boss — segment over
        var later = s with { NowMs = s.NowMs + 1000 + AutoArchiveEngine.CooldownMs + 1 };
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
        var cooldownLifted = goneAlready with { NowMs = Live().NowMs + AutoArchiveEngine.CooldownMs + 1 };
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
            BossPresent = false, NowMs = sighted.NowMs + AutoArchiveEngine.CooldownMs + 1,
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
        e.OnArchived(Live().NowMs, ArchiveReason.SceneChange);                 // arm the cooldown
        var sighted = Live() with { BossPresent = true, NowMs = Live().NowMs + 2000 };
        Assert.Null(e.Evaluate(in sighted));                                   // banks _bossPending, cooldown blocks
        var goneAlready = sighted with { BossPresent = false, BossGone = true, NowMs = sighted.NowMs + 3000 };
        Assert.Null(e.Evaluate(in goneAlready));                               // boss already left, still cooling down
        var cooldownLifted = goneAlready with { NowMs = Live().NowMs + AutoArchiveEngine.CooldownMs + 1 };
        Assert.Equal(ArchiveReason.BossPhase, e.Evaluate(in cooldownLifted));  // stale banked sighting fires once able
        e.OnArchived(cooldownLifted.NowMs, ArchiveReason.BossPhase);

        // A genuinely new boss engagement, well after, must fire too — not wedged by a phantom
        // "segment active" left over from the stale fire above.
        var newBoss = cooldownLifted with
        {
            BossPresent = true, BossGone = false, NowMs = cooldownLifted.NowMs + AutoArchiveEngine.CooldownMs + 1,
        };
        Assert.Equal(ArchiveReason.BossPhase, e.Evaluate(in newBoss));
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
        Assert.Null(e.Evaluate(Live()));                                  // adopt flow version 1
        var overlap = Live() with { FlowStateVersion = 2, DeadCount = 4 }; // stage transition AND wipe overlap
        Assert.Equal(ArchiveReason.Wipe, e.Evaluate(in overlap));          // wipe is checked first, wins the tick
        e.OnArchived(overlap.NowMs, ArchiveReason.Wipe);
        var later = overlap with { NowMs = overlap.NowMs + AutoArchiveEngine.CooldownMs + 1, DeadCount = 0 };
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
        var cleared = s with { CombatActive = false, NowMs = s.NowMs + AutoArchiveEngine.CooldownMs + 1 };
        Assert.Null(e.Evaluate(in cleared));       // past cooldown, but CombatActive=false blocks
        var stillInactive = cleared with { NowMs = cleared.NowMs + AutoArchiveEngine.CooldownMs + 1 };
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
    public void Stage_transition_fires_and_first_observation_is_silent()
    {
        var e = new AutoArchiveEngine();
        Assert.Null(e.Evaluate(Live()));                          // first sight of version 1: adopt, no fire
        var s = Live() with { FlowStateVersion = 2 };
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
        var s = Live() with { FlowStateVersion = 2, InstancedRun = false };
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
        var banked = Live() with { FlowStateVersion = 2, NowMs = Live().NowMs + 2000 };
        Assert.Null(e.Evaluate(in banked));                                   // banks _stagePending, cooldown blocks
        var reset = banked with { FlowStateVersion = 1, NowMs = banked.NowMs + 1000 };
        Assert.Null(e.Evaluate(in reset));                                    // decrease discards the banked pending
        var cooldownLifted = reset with { NowMs = Live().NowMs + AutoArchiveEngine.CooldownMs + 1 };
        Assert.Null(e.Evaluate(in cooldownLifted));                           // no stale StageChange resurfaces
    }

    // ---- shared gates ----

    [Fact]
    public void Cooldown_spans_all_triggers_including_manual_archives()
    {
        // Adapted for round 2: under edge detection, an allDead edge that rises INSIDE the cooldown
        // window is consumed at that tick (dropped, not deferred) — the roster simply STAYING dead
        // once the window lifts produces no NEW edge, so it would no longer refire (unlike round
        // 1's level check, which re-evaluated `allDead && !_wipeLatched` fresh once cooldown lifted).
        // Use a genuinely fresh edge past the window — OutcomeFailed's independent rise — to keep
        // pinning "the shared cooldown gates Wipe like every other trigger" under the new mechanism.
        var e = Armed(Live());
        e.OnArchived(Live().NowMs, ArchiveReason.SceneChange);    // scene archive arms the cooldown
        var s = Live() with { DeadCount = 4, NowMs = Live().NowMs + AutoArchiveEngine.CooldownMs - 1 };
        Assert.Null(e.Evaluate(in s));                            // wipe edge suppressed inside the window
        var later = s with { OutcomeFailed = true, NowMs = s.NowMs + 2 };
        Assert.Equal(ArchiveReason.Wipe, e.Evaluate(in later));   // cooldown lifted + a fresh edge fires
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
            DeadCount = 4, BossPresent = true, FlowStateVersion = 2,
            NowMs = 160_000 + 300_001,
        };
        Assert.Null(e.Evaluate(in s));
    }
}
