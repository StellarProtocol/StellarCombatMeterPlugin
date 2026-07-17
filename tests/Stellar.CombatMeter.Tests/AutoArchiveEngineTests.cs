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
    public void Wipe_latches_until_a_revive_then_rearms()
    {
        var e = Armed(Live());
        var dead = Live() with { DeadCount = 4 };
        Assert.Equal(ArchiveReason.Wipe, e.Evaluate(in dead));
        e.OnArchived(dead.NowMs, ArchiveReason.Wipe);
        var later = dead with { NowMs = dead.NowMs + AutoArchiveEngine.CooldownMs + 1 };
        Assert.Null(e.Evaluate(in later));                       // still all dead — latched
        var revived = later with { DeadCount = 3, NowMs = later.NowMs + 1000 };
        Assert.Null(e.Evaluate(in revived));                     // re-armed, but nobody's wiped
        var deadAgain = revived with { DeadCount = 4, NowMs = revived.NowMs + 1000 };
        Assert.Equal(ArchiveReason.Wipe, e.Evaluate(in deadAgain));
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
        // allDead fires first; OutcomeFailed flips true a tick later while still all dead. Coupled
        // latches (OnArchived forces BOTH true on any Wipe archive) must block the second path too.
        var e = Armed(Live());
        var s1 = Live() with { DeadCount = 4 };
        Assert.Equal(ArchiveReason.Wipe, e.Evaluate(in s1));
        e.OnArchived(s1.NowMs, ArchiveReason.Wipe);
        var s2 = s1 with { NowMs = s1.NowMs + AutoArchiveEngine.CooldownMs + 1, OutcomeFailed = true };
        Assert.Null(e.Evaluate(in s2));   // still all dead, outcome now failed too — no duplicate archive
    }

    [Fact]
    public void Wipe_overlap_outcomefailed_then_alldead_fires_once()
    {
        // Mirror order: OutcomeFailed fires first; allDead catches up a tick later, past cooldown.
        var e = Armed(Live());
        var s1 = Live() with { OutcomeFailed = true };
        Assert.Equal(ArchiveReason.Wipe, e.Evaluate(in s1));
        e.OnArchived(s1.NowMs, ArchiveReason.Wipe);
        var s2 = s1 with { NowMs = s1.NowMs + AutoArchiveEngine.CooldownMs + 1, DeadCount = 4 };
        Assert.Null(e.Evaluate(in s2));   // outcome still failed, now all dead too — no duplicate archive
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
        var e = Armed(Live());
        var s = Live() with { LastDamageMs = 0, NowMs = 300_000 };
        Assert.Null(e.Evaluate(in s));             // LastDamageMs == 0 blocks regardless of elapsed time
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
        var e = Armed(Live());
        e.OnArchived(Live().NowMs, ArchiveReason.SceneChange);    // scene archive arms the cooldown
        var s = Live() with { DeadCount = 4, NowMs = Live().NowMs + AutoArchiveEngine.CooldownMs - 1 };
        Assert.Null(e.Evaluate(in s));                            // wipe suppressed inside the window
        var later = s with { NowMs = s.NowMs + 2 };
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
            DeadCount = 4, BossPresent = true, FlowStateVersion = 2,
            NowMs = 160_000 + 300_001,
        };
        Assert.Null(e.Evaluate(in s));
    }
}
