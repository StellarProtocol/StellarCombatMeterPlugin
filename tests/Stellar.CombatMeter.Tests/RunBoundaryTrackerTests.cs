using Stellar.CombatMeter.AutoArchive;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public class RunBoundaryTrackerTests
{
    // C-mode: run-id change commits under the old id (the healer).
    [Fact]
    public void Cmode_runid_change_commits_old_id()
    {
        var t = new RunBoundaryTracker();
        Assert.Equal(RunBoundaryTracker.BoundaryAction.None, t.Observe(100, 5_000, null, false));
        var a = t.Observe(200, 6_000, null, false);
        Assert.Equal(RunBoundaryTracker.BoundaryAction.Commit, a);
        Assert.Equal(100, t.CommittedOldRunId);
    }

    // C-mode: id -> 0 (leave) with the scene path having handled it already => no double commit.
    [Fact]
    public void Scene_handled_boundary_never_double_commits()
    {
        var t = new RunBoundaryTracker();
        t.Observe(100, 5_000, null, false);
        t.NotifySceneBoundaryHandled(0);
        Assert.Equal(RunBoundaryTracker.BoundaryAction.None, t.Observe(0, 0, null, false));
    }

    // C-mode: stale id + fresh timer does NOT commit (timer only compares across a load).
    [Fact]
    public void Cmode_timer_change_alone_never_commits()
    {
        var t = new RunBoundaryTracker();
        t.Observe(100, 5_000, null, false);
        Assert.Equal(RunBoundaryTracker.BoundaryAction.None, t.Observe(100, 9_000, null, false));
    }

    // B-mode yank: load rises (ARM), falls with SAME id but CHANGED timer -> Commit(old id).
    [Fact]
    public void Bmode_load_cycle_with_timer_change_commits()
    {
        var t = new RunBoundaryTracker();
        t.Observe(100, 5_000, false, false);
        t.Observe(100, 5_000, true, false);            // rising edge: ARM(100, 5_000)
        var a = t.Observe(100, 9_000, false, false);    // falling edge: timer moved
        Assert.Equal(RunBoundaryTracker.BoundaryAction.Commit, a);
        Assert.Equal(100, t.CommittedOldRunId);
    }

    // B-mode same-instance teleport: load cycle, id AND timer unchanged -> Discard (replay P0).
    [Fact]
    public void Bmode_same_instance_load_cycle_discards()
    {
        var t = new RunBoundaryTracker();
        t.Observe(100, 5_000, false, false);
        t.Observe(100, 5_000, true, false);
        Assert.Equal(RunBoundaryTracker.BoundaryAction.Discard, t.Observe(100, 5_000, false, false));
    }

    // B-mode: id change mid-load commits immediately (C wins inside the load too).
    [Fact]
    public void Bmode_id_change_during_load_commits()
    {
        var t = new RunBoundaryTracker();
        t.Observe(100, 5_000, false, false);
        t.Observe(100, 5_000, true, false);
        var a = t.Observe(200, 5_000, true, false);
        Assert.Equal(RunBoundaryTracker.BoundaryAction.Commit, a);
        Assert.Equal(100, t.CommittedOldRunId);
    }

    // Belt: combat while armed commits (should not happen, but must never glue).
    [Fact]
    public void Combat_while_armed_commits()
    {
        var t = new RunBoundaryTracker();
        t.Observe(100, 5_000, false, false);
        t.Observe(100, 5_000, true, false);
        var a = t.Observe(100, 5_000, true, true);
        Assert.Equal(RunBoundaryTracker.BoundaryAction.Commit, a);
    }

    // Run id 0 never arms and never commits (town/no-run states are inert).
    [Fact]
    public void Zero_runid_is_inert()
    {
        var t = new RunBoundaryTracker();
        Assert.Equal(RunBoundaryTracker.BoundaryAction.None, t.Observe(0, 0, false, false));
        Assert.Equal(RunBoundaryTracker.BoundaryAction.None, t.Observe(0, 0, true, false));
        Assert.Equal(RunBoundaryTracker.BoundaryAction.None, t.Observe(0, 0, false, false));
    }
}
