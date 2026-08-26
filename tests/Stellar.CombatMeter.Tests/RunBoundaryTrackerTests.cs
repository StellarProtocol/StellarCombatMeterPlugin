using Stellar.CombatMeter.AutoArchive;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public class RunBoundaryTrackerTests
{
    // C-mode: a run-id change is a CANDIDATE boundary only — it must NOT commit on the same tick
    // (review fix, rb-task-2 finding 1: the poll must defer to the scene path). Only once a LATER
    // tick crosses SceneHandledGraceMs with the boundary still pending does it commit, under the OLD id.
    [Fact]
    public void Cmode_runid_change_defers_to_scene_path_then_commits_after_grace()
    {
        var t = new RunBoundaryTracker();
        Assert.Equal(RunBoundaryTracker.BoundaryAction.None, t.Observe(100, 1, null, false, nowMs: 0));
        var onChange = t.Observe(200, 1, null, false, nowMs: 1_000);
        Assert.Equal(RunBoundaryTracker.BoundaryAction.None, onChange);
        var afterGrace = t.Observe(200, 1, null, false, nowMs: 1_000 + RunBoundaryTracker.SceneHandledGraceMs);
        Assert.Equal(RunBoundaryTracker.BoundaryAction.Commit, afterGrace);
        Assert.Equal(100, t.CommittedOldRunId);
    }

    // (i) Scene path claims the boundary BEFORE the grace deadline (the normal case on every ordinary
    // floor transition) => the poll must never commit it, even long after what would have been the deadline.
    [Fact]
    public void Cmode_id_change_then_scene_handled_before_deadline_never_commits()
    {
        var t = new RunBoundaryTracker();
        t.Observe(100, 1, null, false, nowMs: 0);
        var onChange = t.Observe(200, 1, null, false, nowMs: 1_000);
        Assert.Equal(RunBoundaryTracker.BoundaryAction.None, onChange);
        t.NotifySceneBoundaryHandled(200);   // OnSceneChanged claims it first — cancels the pending boundary.
        var wayPastWhatWouldHaveBeenTheDeadline = t.Observe(200, 1, null, false,
            nowMs: 1_000 + RunBoundaryTracker.SceneHandledGraceMs + 10_000);
        Assert.Equal(RunBoundaryTracker.BoundaryAction.None, wayPastWhatWouldHaveBeenTheDeadline);
    }

    // (ii) Quiet ticks (no scene-handled notification, no further id change) past the deadline commit
    // exactly once — never a second time on a later quiet tick.
    [Fact]
    public void Cmode_id_change_then_quiet_ticks_past_deadline_commits_exactly_once()
    {
        var t = new RunBoundaryTracker();
        t.Observe(100, 1, null, false, nowMs: 0);
        t.Observe(200, 1, null, false, nowMs: 1_000);   // pending: old=100, deadline=1_000+grace
        Assert.Equal(RunBoundaryTracker.BoundaryAction.None, t.Observe(200, 1, null, false, nowMs: 2_000));
        var deadline = 1_000 + RunBoundaryTracker.SceneHandledGraceMs;
        var a = t.Observe(200, 1, null, false, nowMs: deadline);
        Assert.Equal(RunBoundaryTracker.BoundaryAction.Commit, a);
        Assert.Equal(100, t.CommittedOldRunId);
        var next = t.Observe(200, 1, null, false, nowMs: deadline + 500);
        Assert.Equal(RunBoundaryTracker.BoundaryAction.None, next);   // no double-commit
    }

    // (iii) A pending boundary survives any number of pre-deadline ticks, returning None every time,
    // until one finally crosses the deadline.
    [Fact]
    public void Cmode_pending_boundary_survives_multiple_pre_deadline_ticks()
    {
        var t = new RunBoundaryTracker();
        t.Observe(100, 1, null, false, nowMs: 0);
        t.Observe(200, 1, null, false, nowMs: 1_000);   // pending: old=100, deadline=1_000+grace
        var deadline = 1_000 + RunBoundaryTracker.SceneHandledGraceMs;
        for (long now = 1_200; now < deadline; now += 500)
            Assert.Equal(RunBoundaryTracker.BoundaryAction.None, t.Observe(200, 1, null, false, nowMs: now));
        var a = t.Observe(200, 1, null, false, nowMs: deadline);
        Assert.Equal(RunBoundaryTracker.BoundaryAction.Commit, a);
        Assert.Equal(100, t.CommittedOldRunId);
    }

    // A second/third id-change while already pending must not reset the clock or swap which old id
    // eventually commits — only the FIRST candidate boundary's old id/deadline latches.
    [Fact]
    public void Cmode_second_id_change_while_pending_keeps_original_old_id_and_deadline()
    {
        var t = new RunBoundaryTracker();
        t.Observe(100, 1, null, false, nowMs: 0);
        t.Observe(200, 1, null, false, nowMs: 1_000);          // pending: old=100, deadline=1_000+grace
        var secondChange = t.Observe(300, 1, null, false, nowMs: 1_200);   // rapid second hop while pending
        Assert.Equal(RunBoundaryTracker.BoundaryAction.None, secondChange);
        var deadline = 1_000 + RunBoundaryTracker.SceneHandledGraceMs;   // NOT reset relative to 1_200
        Assert.Equal(RunBoundaryTracker.BoundaryAction.None, t.Observe(300, 1, null, false, nowMs: deadline - 100));
        var a = t.Observe(300, 1, null, false, nowMs: deadline);
        Assert.Equal(RunBoundaryTracker.BoundaryAction.Commit, a);
        Assert.Equal(100, t.CommittedOldRunId);   // the ORIGINAL old id, not 200
    }

    // C-mode: id -> 0 (leave) with the scene path having handled it already => no double commit.
    [Fact]
    public void Scene_handled_boundary_never_double_commits()
    {
        var t = new RunBoundaryTracker();
        t.Observe(100, 1, null, false, nowMs: 0);
        t.NotifySceneBoundaryHandled(0);
        Assert.Equal(RunBoundaryTracker.BoundaryAction.None, t.Observe(0, 0, null, false, nowMs: 1_000));
    }

    // C-mode: stale id + fresh epoch does NOT commit (epoch only compares across a load).
    [Fact]
    public void Cmode_epoch_change_alone_never_commits()
    {
        var t = new RunBoundaryTracker();
        t.Observe(100, 1, null, false, nowMs: 0);
        Assert.Equal(RunBoundaryTracker.BoundaryAction.None, t.Observe(100, 2, null, false, nowMs: 1_000));
    }

    // B-mode yank: load rises (ARM), falls with SAME id but CHANGED epoch -> Commit(old id).
    [Fact]
    public void Bmode_load_cycle_with_epoch_change_commits()
    {
        var t = new RunBoundaryTracker();
        t.Observe(100, 1, false, false, nowMs: 0);
        t.Observe(100, 1, true, false, nowMs: 500);            // rising edge: ARM(100, epoch 1)
        var a = t.Observe(100, 2, false, false, nowMs: 1_000); // falling edge: epoch moved
        Assert.Equal(RunBoundaryTracker.BoundaryAction.Commit, a);
        Assert.Equal(100, t.CommittedOldRunId);
    }

    // B-mode same-instance teleport: load cycle, id AND epoch unchanged -> Discard (replay P0).
    [Fact]
    public void Bmode_same_instance_load_cycle_discards()
    {
        var t = new RunBoundaryTracker();
        t.Observe(100, 1, false, false, nowMs: 0);
        t.Observe(100, 1, true, false, nowMs: 500);
        Assert.Equal(RunBoundaryTracker.BoundaryAction.Discard, t.Observe(100, 1, false, false, nowMs: 1_000));
    }

    // B-mode: id change mid-load commits immediately (C wins inside the load too) — a concrete
    // loading bit is already in hand, so there is no scene-path race to defer to; unaffected by the
    // C-mode grace-window fix (that only guards the pure-C-mode poll usage, inWorldLoading: null).
    [Fact]
    public void Bmode_id_change_during_load_commits()
    {
        var t = new RunBoundaryTracker();
        t.Observe(100, 1, false, false, nowMs: 0);
        t.Observe(100, 1, true, false, nowMs: 500);
        var a = t.Observe(200, 1, true, true, nowMs: 1_000);
        Assert.Equal(RunBoundaryTracker.BoundaryAction.Commit, a);
        Assert.Equal(100, t.CommittedOldRunId);
    }

    // Belt: combat while armed with NO re-key evidence (same id, same epoch) banks a segment CUT —
    // never a boundary COMMIT. This replaces the pre-2026-08-26 unconditional belt commit, which
    // reset run identity 7×/raid on stage-transition loading flashes (measured raids
    // 668840469433679872 / 420861014951591936) and, combined with a mid-run timer rank-upgrade,
    // split one raid into two sessions (the owner's 7:58p ×3 / 8:00p ×10 P0).
    [Fact]
    public void Combat_while_armed_without_rekey_evidence_cuts()
    {
        var t = new RunBoundaryTracker();
        t.Observe(100, 1, false, false, nowMs: 0);
        t.Observe(100, 1, true, false, nowMs: 500);
        var a = t.Observe(100, 1, true, true, nowMs: 1_000);
        Assert.Equal(RunBoundaryTracker.BoundaryAction.Cut, a);
        Assert.False(t.IsArmed);   // the armed reference is consumed either way
    }

    // Belt: combat while armed WITH the epoch moved (the framework re-keyed the run across the
    // load — the IkriESpwsl yank, now upgrade-proof) still commits a full boundary.
    [Fact]
    public void Combat_while_armed_with_epoch_change_commits()
    {
        var t = new RunBoundaryTracker();
        t.Observe(100, 1, false, false, nowMs: 0);
        t.Observe(100, 1, true, false, nowMs: 500);
        var a = t.Observe(100, 2, true, true, nowMs: 1_000);
        Assert.Equal(RunBoundaryTracker.BoundaryAction.Commit, a);
        Assert.Equal(100, t.CommittedOldRunId);
    }

    // Run id 0 never arms and never commits (town/no-run states are inert).
    [Fact]
    public void Zero_runid_is_inert()
    {
        var t = new RunBoundaryTracker();
        Assert.Equal(RunBoundaryTracker.BoundaryAction.None, t.Observe(0, 0, false, false, nowMs: 0));
        Assert.Equal(RunBoundaryTracker.BoundaryAction.None, t.Observe(0, 0, true, false, nowMs: 500));
        Assert.Equal(RunBoundaryTracker.BoundaryAction.None, t.Observe(0, 0, false, false, nowMs: 1_000));
    }

    // PIN (owner P0 — measured raid 668840469433679872 vs 420861014951591936, 2026-08-25): a
    // raid's stage-transition loading flashes resolve via combat events; with NO run-id change and
    // NO epoch change every one of them must CUT and never COMMIT. Both measured raids fired 7
    // such cycles; the split raid's 588000→620000 mid-run rank UPGRADE moves the timer VALUE only
    // (never the epoch, DungeonStateServiceTests.RunTimerEpoch_BumpsOnFirstLatch_NotOnUpgrade), so
    // no upgrade can ever convert one of these cuts into an identity reset. Weakening this test
    // re-opens the raid run-split P0.
    [Fact]
    public void Raid_stage_flashes_with_constant_epoch_cut_and_never_commit()
    {
        var t = new RunBoundaryTracker();
        t.Observe(100, 1, false, false, nowMs: 0);
        long now = 500;
        for (int i = 0; i < 7; i++)
        {
            t.Observe(100, 1, true, false, nowMs: now);                       // stage flash: ARM
            // Retention: a quiet still-loading tick mid-flash must not drop the armed reference.
            Assert.Equal(RunBoundaryTracker.BoundaryAction.None,
                t.Observe(100, 1, true, false, nowMs: now + 50));
            Assert.True(t.IsArmed);
            var resolved = t.Observe(100, 1, true, true, nowMs: now + 100);   // combat resolves it
            Assert.Equal(RunBoundaryTracker.BoundaryAction.Cut, resolved);
            // The flash's own falling edge afterwards is inert (nothing armed anymore).
            Assert.Equal(RunBoundaryTracker.BoundaryAction.None,
                t.Observe(100, 1, false, false, nowMs: now + 200));
            now += 60_000;
        }
    }

    // --- rb-task-4 review findings (both latent-until-now) ---------------------------------------

    // Finding (a): a loading rising edge arriving WHILE a C-mode candidate is still pending grace used
    // to be swallowed — the pending branch ran first in the if/else-if chain and _lastLoading updates
    // unconditionally at the end of Observe, so the edge could never be seen again this load cycle and
    // IsArmed stayed permanently false. Fix: the rising edge is itself proof the scene path won't reach
    // this in time, so it banks the still-open pending boundary NOW (instead of waiting out the grace
    // window) and arms fresh for the load cycle that is genuinely starting — two distinct boundaries,
    // neither lost, neither double-committed.
    [Fact]
    public void Cmode_pending_boundary_then_rising_edge_commits_pending_and_arms_fresh()
    {
        var t = new RunBoundaryTracker();
        t.Observe(100, 1, null, false, nowMs: 0);                       // baseline: run 100
        var onChange = t.Observe(200, 1, null, false, nowMs: 1_000);    // C-mode candidate: pending old=100
        Assert.Equal(RunBoundaryTracker.BoundaryAction.None, onChange);
        Assert.False(t.IsArmed);

        // Rising edge arrives well inside the SceneHandledGraceMs window (deadline would be 3_500).
        var onRisingEdge = t.Observe(200, 1, true, false, nowMs: 1_200);
        Assert.Equal(RunBoundaryTracker.BoundaryAction.Commit, onRisingEdge);
        Assert.Equal(100, t.CommittedOldRunId);    // the still-open boundary banks now, not lost
        Assert.True(t.IsArmed);                    // AND armed fresh for the new (200) load cycle

        // The fresh arm resolves independently and correctly on its own falling edge (epoch moved).
        var onFallingEdge = t.Observe(200, 2, false, false, nowMs: 1_500);
        Assert.Equal(RunBoundaryTracker.BoundaryAction.Commit, onFallingEdge);
        Assert.Equal(200, t.CommittedOldRunId);

        // The original pending deadline (3_500) passing later must never re-fire — no double commit.
        Assert.Equal(RunBoundaryTracker.BoundaryAction.None, t.Observe(200, 2, false, false, nowMs: 3_600));
    }

    // Same setup, but the absorbed load cycle turns out to be a same-instance teleport (epoch
    // unchanged at the falling edge) — it must Discard cleanly WITHOUT touching the pending boundary
    // that was already committed at the rising edge.
    [Fact]
    public void Cmode_pending_boundary_then_rising_edge_arm_can_still_discard_its_own_cycle()
    {
        var t = new RunBoundaryTracker();
        t.Observe(100, 1, null, false, nowMs: 0);
        t.Observe(200, 1, null, false, nowMs: 1_000);
        var onRisingEdge = t.Observe(200, 1, true, false, nowMs: 1_200);
        Assert.Equal(RunBoundaryTracker.BoundaryAction.Commit, onRisingEdge);
        Assert.Equal(100, t.CommittedOldRunId);

        var onFallingEdge = t.Observe(200, 1, false, false, nowMs: 1_500);   // same id + same epoch
        Assert.Equal(RunBoundaryTracker.BoundaryAction.Discard, onFallingEdge);
        Assert.Equal(100, t.CommittedOldRunId);   // unchanged — the earlier commit is not overwritten
    }

    // Finding (b): a tracker whose FIRST-EVER Observe call happens mid-load-screen (plugin construction,
    // or the poll's first tick after a load already started) must not misread it as a rising edge —
    // _lastLoading defaults false, so without a baseline-only guard the first call's loading=true looks
    // like a genuine false->true transition and arms against a reference with no real "before" state.
    [Fact]
    public void First_observe_while_loading_is_baseline_only_no_arm()
    {
        var t = new RunBoundaryTracker();
        var first = t.Observe(100, 1, true, false, nowMs: 0);
        Assert.Equal(RunBoundaryTracker.BoundaryAction.None, first);
        Assert.False(t.IsArmed);

        // The "falling" edge of this same phantom cycle must resolve to nothing — there was never a
        // legitimate ARM to close out.
        var falling = t.Observe(100, 1, false, false, nowMs: 500);
        Assert.Equal(RunBoundaryTracker.BoundaryAction.None, falling);
        Assert.False(t.IsArmed);
    }

    // Sanity: the first-observe guard doesn't interfere with the ordinary C-mode candidate path — a
    // genuine id change on the SECOND call (first call merely established the baseline) still behaves
    // exactly as every other C-mode test above.
    [Fact]
    public void First_observe_baseline_then_normal_cmode_candidate_still_works()
    {
        var t = new RunBoundaryTracker();
        t.Observe(100, 1, null, false, nowMs: 0);   // baseline only
        var onChange = t.Observe(200, 1, null, false, nowMs: 1_000);
        Assert.Equal(RunBoundaryTracker.BoundaryAction.None, onChange);
        var afterGrace = t.Observe(200, 1, null, false, nowMs: 1_000 + RunBoundaryTracker.SceneHandledGraceMs);
        Assert.Equal(RunBoundaryTracker.BoundaryAction.Commit, afterGrace);
        Assert.Equal(100, t.CommittedOldRunId);
    }
}
