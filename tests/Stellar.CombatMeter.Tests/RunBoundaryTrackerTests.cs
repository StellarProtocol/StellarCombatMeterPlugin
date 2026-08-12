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
        Assert.Equal(RunBoundaryTracker.BoundaryAction.None, t.Observe(100, 5_000, null, false, nowMs: 0));
        var onChange = t.Observe(200, 6_000, null, false, nowMs: 1_000);
        Assert.Equal(RunBoundaryTracker.BoundaryAction.None, onChange);
        var afterGrace = t.Observe(200, 6_000, null, false, nowMs: 1_000 + RunBoundaryTracker.SceneHandledGraceMs);
        Assert.Equal(RunBoundaryTracker.BoundaryAction.Commit, afterGrace);
        Assert.Equal(100, t.CommittedOldRunId);
    }

    // (i) Scene path claims the boundary BEFORE the grace deadline (the normal case on every ordinary
    // floor transition) => the poll must never commit it, even long after what would have been the deadline.
    [Fact]
    public void Cmode_id_change_then_scene_handled_before_deadline_never_commits()
    {
        var t = new RunBoundaryTracker();
        t.Observe(100, 5_000, null, false, nowMs: 0);
        var onChange = t.Observe(200, 6_000, null, false, nowMs: 1_000);
        Assert.Equal(RunBoundaryTracker.BoundaryAction.None, onChange);
        t.NotifySceneBoundaryHandled(200);   // OnSceneChanged claims it first — cancels the pending boundary.
        var wayPastWhatWouldHaveBeenTheDeadline = t.Observe(200, 6_000, null, false,
            nowMs: 1_000 + RunBoundaryTracker.SceneHandledGraceMs + 10_000);
        Assert.Equal(RunBoundaryTracker.BoundaryAction.None, wayPastWhatWouldHaveBeenTheDeadline);
    }

    // (ii) Quiet ticks (no scene-handled notification, no further id change) past the deadline commit
    // exactly once — never a second time on a later quiet tick.
    [Fact]
    public void Cmode_id_change_then_quiet_ticks_past_deadline_commits_exactly_once()
    {
        var t = new RunBoundaryTracker();
        t.Observe(100, 5_000, null, false, nowMs: 0);
        t.Observe(200, 6_000, null, false, nowMs: 1_000);   // pending: old=100, deadline=1_000+grace
        Assert.Equal(RunBoundaryTracker.BoundaryAction.None, t.Observe(200, 6_000, null, false, nowMs: 2_000));
        var deadline = 1_000 + RunBoundaryTracker.SceneHandledGraceMs;
        var a = t.Observe(200, 6_000, null, false, nowMs: deadline);
        Assert.Equal(RunBoundaryTracker.BoundaryAction.Commit, a);
        Assert.Equal(100, t.CommittedOldRunId);
        var next = t.Observe(200, 6_000, null, false, nowMs: deadline + 500);
        Assert.Equal(RunBoundaryTracker.BoundaryAction.None, next);   // no double-commit
    }

    // (iii) A pending boundary survives any number of pre-deadline ticks, returning None every time,
    // until one finally crosses the deadline.
    [Fact]
    public void Cmode_pending_boundary_survives_multiple_pre_deadline_ticks()
    {
        var t = new RunBoundaryTracker();
        t.Observe(100, 5_000, null, false, nowMs: 0);
        t.Observe(200, 6_000, null, false, nowMs: 1_000);   // pending: old=100, deadline=1_000+grace
        var deadline = 1_000 + RunBoundaryTracker.SceneHandledGraceMs;
        for (long now = 1_200; now < deadline; now += 500)
            Assert.Equal(RunBoundaryTracker.BoundaryAction.None, t.Observe(200, 6_000, null, false, nowMs: now));
        var a = t.Observe(200, 6_000, null, false, nowMs: deadline);
        Assert.Equal(RunBoundaryTracker.BoundaryAction.Commit, a);
        Assert.Equal(100, t.CommittedOldRunId);
    }

    // A second/third id-change while already pending must not reset the clock or swap which old id
    // eventually commits — only the FIRST candidate boundary's old id/deadline latches.
    [Fact]
    public void Cmode_second_id_change_while_pending_keeps_original_old_id_and_deadline()
    {
        var t = new RunBoundaryTracker();
        t.Observe(100, 5_000, null, false, nowMs: 0);
        t.Observe(200, 6_000, null, false, nowMs: 1_000);          // pending: old=100, deadline=1_000+grace
        var secondChange = t.Observe(300, 7_000, null, false, nowMs: 1_200);   // rapid second hop while pending
        Assert.Equal(RunBoundaryTracker.BoundaryAction.None, secondChange);
        var deadline = 1_000 + RunBoundaryTracker.SceneHandledGraceMs;   // NOT reset relative to 1_200
        Assert.Equal(RunBoundaryTracker.BoundaryAction.None, t.Observe(300, 7_000, null, false, nowMs: deadline - 100));
        var a = t.Observe(300, 7_000, null, false, nowMs: deadline);
        Assert.Equal(RunBoundaryTracker.BoundaryAction.Commit, a);
        Assert.Equal(100, t.CommittedOldRunId);   // the ORIGINAL old id, not 200
    }

    // C-mode: id -> 0 (leave) with the scene path having handled it already => no double commit.
    [Fact]
    public void Scene_handled_boundary_never_double_commits()
    {
        var t = new RunBoundaryTracker();
        t.Observe(100, 5_000, null, false, nowMs: 0);
        t.NotifySceneBoundaryHandled(0);
        Assert.Equal(RunBoundaryTracker.BoundaryAction.None, t.Observe(0, 0, null, false, nowMs: 1_000));
    }

    // C-mode: stale id + fresh timer does NOT commit (timer only compares across a load).
    [Fact]
    public void Cmode_timer_change_alone_never_commits()
    {
        var t = new RunBoundaryTracker();
        t.Observe(100, 5_000, null, false, nowMs: 0);
        Assert.Equal(RunBoundaryTracker.BoundaryAction.None, t.Observe(100, 9_000, null, false, nowMs: 1_000));
    }

    // B-mode yank: load rises (ARM), falls with SAME id but CHANGED timer -> Commit(old id).
    [Fact]
    public void Bmode_load_cycle_with_timer_change_commits()
    {
        var t = new RunBoundaryTracker();
        t.Observe(100, 5_000, false, false, nowMs: 0);
        t.Observe(100, 5_000, true, false, nowMs: 500);            // rising edge: ARM(100, 5_000)
        var a = t.Observe(100, 9_000, false, false, nowMs: 1_000); // falling edge: timer moved
        Assert.Equal(RunBoundaryTracker.BoundaryAction.Commit, a);
        Assert.Equal(100, t.CommittedOldRunId);
    }

    // B-mode same-instance teleport: load cycle, id AND timer unchanged -> Discard (replay P0).
    [Fact]
    public void Bmode_same_instance_load_cycle_discards()
    {
        var t = new RunBoundaryTracker();
        t.Observe(100, 5_000, false, false, nowMs: 0);
        t.Observe(100, 5_000, true, false, nowMs: 500);
        Assert.Equal(RunBoundaryTracker.BoundaryAction.Discard, t.Observe(100, 5_000, false, false, nowMs: 1_000));
    }

    // B-mode: id change mid-load commits immediately (C wins inside the load too) — a concrete
    // loading bit is already in hand, so there is no scene-path race to defer to; unaffected by the
    // C-mode grace-window fix (that only guards the pure-C-mode poll usage, inWorldLoading: null).
    [Fact]
    public void Bmode_id_change_during_load_commits()
    {
        var t = new RunBoundaryTracker();
        t.Observe(100, 5_000, false, false, nowMs: 0);
        t.Observe(100, 5_000, true, false, nowMs: 500);
        var a = t.Observe(200, 5_000, true, true, nowMs: 1_000);
        Assert.Equal(RunBoundaryTracker.BoundaryAction.Commit, a);
        Assert.Equal(100, t.CommittedOldRunId);
    }

    // Belt: combat while armed commits (should not happen, but must never glue).
    [Fact]
    public void Combat_while_armed_commits()
    {
        var t = new RunBoundaryTracker();
        t.Observe(100, 5_000, false, false, nowMs: 0);
        t.Observe(100, 5_000, true, false, nowMs: 500);
        var a = t.Observe(100, 5_000, true, true, nowMs: 1_000);
        Assert.Equal(RunBoundaryTracker.BoundaryAction.Commit, a);
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
}
