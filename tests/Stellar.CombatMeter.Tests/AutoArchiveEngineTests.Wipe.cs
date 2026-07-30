using Stellar.Abstractions.Domain;
using Stellar.CombatMeter.AutoArchive;
using Xunit;

namespace Stellar.CombatMeter.Tests;

// Wipe episode/latch tests (allDead level + OutcomeFailed edge, cooldown interplay), plus the
// revive-grace debounce and ignore-solo gate. Split out of AutoArchiveEngineTests.cs (2026-07-26,
// review round) — see that file's banner for the full partial map. Live()/Armed() live there.
public partial class AutoArchiveEngineTests
{
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
}
