using Stellar.Abstractions.Domain;
using Stellar.CombatMeter.AutoArchive;
using Xunit;

namespace Stellar.CombatMeter.Tests;

// Stage/flow transition tests (arms only on transitions INTO a run-END state), plus the
// wipe/stage overlap pin (a banked stage transition must not survive an overlapping archive).
// Split out of AutoArchiveEngineTests.cs (2026-07-26, review round) — see that file's banner for
// the full partial map. Live()/Armed() live there.
public partial class AutoArchiveEngineTests
{
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

    // ---- stage change ----

    [Fact]
    public void Stage_transition_into_run_end_fires_and_first_observation_is_silent()
    {
        var e = new AutoArchiveEngine();
        Assert.Null(e.Evaluate(Live()));                          // first sight of version 1: adopt, no fire
        var s = Live() with { FlowStateVersion = 2, CurrentFlowState = DungeonFlowState.End };
        Assert.Equal(ArchiveReason.StageChange, e.Evaluate(in s));
    }

    // ---- fast-kill clear override (measured root cause, run sea/xHC0xrYY8r) ----

    [Fact]
    public void Stage_clear_fires_through_no_stats_and_cooldown_gates()
    {
        // On a fast kill the boss-kill archive banks + Clear()s the fight ~1s before the clear/settlement
        // packet, so the run-end (End) transition that carries the clear arrives with NO stats AND inside
        // the Min-gap cooldown — dropped by BOTH gates today. A fresh CLEAR must fire it through both so
        // the clear archive lands at End (run still live) instead of being lost to the late scene archive.
        var e = new AutoArchiveEngine();
        Assert.Null(e.Evaluate(Live()));                         // adopt flow version 1
        e.OnArchived(200_000, ArchiveReason.BossKill);           // the fight just banked -> arms the cooldown
        var end = Live() with
        {
            NowMs = 201_000,                                     // 1s later — well inside DefaultCooldownMs
            FlowStateVersion = 2, CurrentFlowState = DungeonFlowState.End,
            HasStats = false, HasFreshClear = true,
        };
        Assert.Equal(ArchiveReason.StageChange, e.Evaluate(in end));
    }

    [Fact]
    public void Stage_without_a_fresh_clear_stays_gated_by_stats_and_cooldown()
    {
        // The override is TARGETED: a run-end transition with NO fresh clear keeps the normal HasStats +
        // cooldown gates (the 2026-07-30 deterministic stage-count behaviour). No stats -> no fire.
        var e = new AutoArchiveEngine();
        Assert.Null(e.Evaluate(Live()));
        e.OnArchived(200_000, ArchiveReason.BossKill);
        var end = Live() with
        {
            NowMs = 201_000, FlowStateVersion = 2, CurrentFlowState = DungeonFlowState.End,
            HasStats = false, HasFreshClear = false,
        };
        Assert.Null(e.Evaluate(in end));
    }

    [Fact]
    public void Stage_clear_override_does_not_fire_while_stats_are_still_live()
    {
        // LOAD-BEARING guard (regression run sea/gqBa7Nha78): a fast clear whose settlement beat the
        // boss-kill settle reaches End with the fight stats STILL LIVE. The clear override must NOT fire
        // here — doing so would preempt the boss-kill archive and bank the fight itself as a `stage`
        // segment (the bosskill segment vanished). With stats live + inside the cooldown, Evaluate returns
        // null via the normal gate, leaving the boss-kill want to bank the fight.
        var e = new AutoArchiveEngine();
        Assert.Null(e.Evaluate(Live()));
        e.OnArchived(200_000, ArchiveReason.BossKill);           // recent archive -> inside the cooldown
        var end = Live() with
        {
            NowMs = 201_000, FlowStateVersion = 2, CurrentFlowState = DungeonFlowState.End,
            HasStats = true, HasFreshClear = true,               // fight NOT yet banked
        };
        Assert.Null(e.Evaluate(in end));                         // override skipped (HasStats); normal path cooldown-blocked
    }

    [Fact]
    public void Clear_marker_fires_on_settlement_arrival_with_no_pending_stage_transition()
    {
        // NO-BOSS clear (Stimen Vault floor, measured sea/NMjjTgpx3O): the run-end archive banks the floor
        // combat as "partial" ~1s BEFORE the game's clear settlement (pass_time) arrives, and the End stage
        // transition is already consumed. When the settlement THEN lands (HasFreshClear) with the fight
        // banked (no stats) and no kill marked yet, the clear-marker must STILL fire — it must not depend
        // on a pending stage transition — so the run reads "kill" instead of "partial".
        var e = new AutoArchiveEngine();
        Assert.Null(e.Evaluate(Live()));                             // adopt flow v1
        e.OnArchived(200_000, ArchiveReason.StageChange);            // floor archive banked (partial) + armed cooldown
        var settled = Live() with
        {
            NowMs = 201_000, FlowStateVersion = 1,                   // NO new stage transition (End already consumed)
            HasStats = false, HasFreshClear = true, ClearMarkerBanked = false,
        };
        Assert.Equal(ArchiveReason.StageChange, e.Evaluate(in settled));
    }

    [Fact]
    public void Clear_marker_does_not_refire_once_banked()
    {
        // Once the marker banks (a "kill" entry sets Plugin._clearMarkerBanked), the ClearMarkerBanked
        // guard stops it re-firing every tick while the sticky settlement stays present.
        var e = new AutoArchiveEngine();
        Assert.Null(e.Evaluate(Live()));
        e.OnArchived(200_000, ArchiveReason.StageChange);
        var banked = Live() with
        {
            NowMs = 201_000, FlowStateVersion = 1,
            HasStats = false, HasFreshClear = true, ClearMarkerBanked = true,
        };
        Assert.Null(e.Evaluate(in banked));
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
    public void Stage_transition_into_settlement_arms_when_that_stage_is_selected()
    {
        // (c) Settlement is also a run-END state — arms, ONCE SELECTED. Since 2026-07-30 the stages are a
        // user choice defaulting to End alone, so this now also proves the selector actually works.
        var e = Armed(Live());
        e.SetStageSelected(DungeonFlowState.Settlement, true);
        var s = Live() with { FlowStateVersion = 2, CurrentFlowState = DungeonFlowState.Settlement };
        Assert.Equal(ArchiveReason.StageChange, e.Evaluate(in s));
    }

    // REGRESSION PIN (2026-07-30) — the duplicate-archive bug. A run steps End -> Settlement -> Vote and
    // ALL THREE used to arm, so one run end cut 1-3 archives depending purely on whether the shared Min-gap
    // cooldown had expired between them: the owner measured bosskill + 2x stage at cooldownS=5 and only 1 at
    // cooldownS=10 on the same build and content. With the default single stage the count is deterministic.
    // Do NOT relax this by widening the default selection.
    [Fact]
    public void Stage_fires_once_across_the_whole_run_end_sequence_by_default()
    {
        var e = Armed(Live());
        // Isolate the stage trigger: the later ticks deliberately sit far past the cooldown, which would
        // otherwise let Idle fire and mask what is being asserted here.
        e.WipeEnabled = false; e.BossEnabled = false; e.IdleEnabled = false;
        // End is the default selection -> fires.
        var end = Live() with { FlowStateVersion = 2, CurrentFlowState = DungeonFlowState.End };
        Assert.Equal(ArchiveReason.StageChange, e.Evaluate(in end));
        e.OnArchived(end.NowMs, ArchiveReason.StageChange);

        // Settlement and Vote follow in the same run. Both are UNSELECTED, so neither may arm — and this
        // must hold even once the cooldown has long expired, which is what makes it cooldown-independent.
        var settle = Live() with
        {
            FlowStateVersion = 3, CurrentFlowState = DungeonFlowState.Settlement,
            NowMs = end.NowMs + AutoArchiveEngine.DefaultCooldownMs * 10,
        };
        Assert.Null(e.Evaluate(in settle));
        var vote = settle with
        {
            FlowStateVersion = 4, CurrentFlowState = DungeonFlowState.Vote,
            NowMs = settle.NowMs + AutoArchiveEngine.DefaultCooldownMs * 10,
        };
        Assert.Null(e.Evaluate(in vote));
    }

    // Selecting two stages is allowed and yields two archives — the user asked for them. This is the
    // counterpart to the pin above: the fix must constrain the DEFAULT, not remove the capability.
    [Fact]
    public void Stage_fires_for_each_selected_stage()
    {
        var e = Armed(Live());
        e.SetStageSelected(DungeonFlowState.Settlement, true);

        var end = Live() with { FlowStateVersion = 2, CurrentFlowState = DungeonFlowState.End };
        Assert.Equal(ArchiveReason.StageChange, e.Evaluate(in end));
        e.OnArchived(end.NowMs, ArchiveReason.StageChange);

        var settle = Live() with
        {
            FlowStateVersion = 3, CurrentFlowState = DungeonFlowState.Settlement,
            NowMs = end.NowMs + AutoArchiveEngine.DefaultCooldownMs + 1,
        };
        Assert.Equal(ArchiveReason.StageChange, e.Evaluate(in settle));
    }

    [Fact]
    public void Stage_deselecting_every_stage_never_fires()
    {
        var e = Armed(Live());
        foreach (var stage in AutoArchiveEngine.SelectableStages) e.SetStageSelected(stage, false);
        foreach (var stage in AutoArchiveEngine.SelectableStages)
            Assert.Null(e.Evaluate(Live() with { FlowStateVersion = 2, CurrentFlowState = stage }));
    }

    // Entry-side states are not selectable at all: arming on Playing would cut an archive of just the
    // opener (a player poking a boss bumps the flow to Playing).
    [Fact]
    public void Stage_entry_side_states_cannot_be_selected()
    {
        var e = Armed(Live());
        foreach (var s in new[] { DungeonFlowState.None, DungeonFlowState.Active, DungeonFlowState.Ready, DungeonFlowState.Playing })
        {
            e.SetStageSelected(s, true);
            Assert.False(e.IsStageSelected(s));
        }
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
}
