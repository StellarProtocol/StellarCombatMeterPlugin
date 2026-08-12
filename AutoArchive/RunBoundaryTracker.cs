namespace Stellar.CombatMeter.AutoArchive;

/// <summary>
/// Pure run-boundary state machine (spec 2026-08-12-combatmeter-run-boundary-design.md §3).
/// Observed per plugin frame tick. C-mode (inWorldLoading=null): a CurrentRunId tick-over-tick
/// change (old != 0) IS a confirmed boundary — commit under the old id. B-mode adds the load
/// cycle: rising edge ARMs a (runId, runTimerStartMs) reference; the falling edge commits when
/// EITHER differs (yank: stale id, fresh timer — the measured IkriESpwsl shape) and discards when
/// both match (same-instance teleport — replay continuity, P0). Timer changes compare ONLY across
/// a load cycle: mid-run rank-upgrade refinements (SetRunTimerStart) must never cut a run.
/// Alloc-free; plain fields only.
/// </summary>
internal sealed class RunBoundaryTracker
{
    internal enum BoundaryAction { None, Commit, Discard }

    private long _lastRunId;
    private long _armedRunId;        // 0 = not armed
    private long _armedTimerMs;
    private bool _lastLoading;

    /// <summary>The OLD run id of the boundary returned by the last Commit-returning Observe call.</summary>
    internal long CommittedOldRunId { get; private set; }

    internal BoundaryAction Observe(long runId, long runTimerStartMs, bool? inWorldLoading, bool combatEvent)
    {
        var action = BoundaryAction.None;
        bool loading = inWorldLoading ?? false;

        // C: id change with a real prior id = confirmed boundary (scene path idempotence is handled
        // by NotifySceneBoundaryHandled syncing _lastRunId before this poll sees the change).
        if (runId != _lastRunId && _lastRunId != 0)
        {
            CommittedOldRunId = _lastRunId;
            action = BoundaryAction.Commit;
            _armedRunId = 0;
        }
        else if (inWorldLoading is not null && _armedRunId != 0)
        {
            if (combatEvent || (!loading && _lastLoading && runTimerStartMs != _armedTimerMs))
            {
                CommittedOldRunId = _armedRunId;     // yank: same stale id, run restarted
                action = BoundaryAction.Commit;
                _armedRunId = 0;
            }
            else if (!loading && _lastLoading)
            {
                action = BoundaryAction.Discard;      // same instance, same run
                _armedRunId = 0;
            }
        }
        else if (inWorldLoading is not null && loading && !_lastLoading && runId != 0)
        {
            _armedRunId = runId;                      // rising edge: ARM
            _armedTimerMs = runTimerStartMs;
        }

        _lastRunId = runId;
        _lastLoading = loading;
        return action;
    }

    /// <summary>The scene-change path banked this boundary itself — adopt the new id so the next
    /// poll does not see a change and double-commit (invariant 6: one entry per boundary).</summary>
    internal void NotifySceneBoundaryHandled(long newRunId)
    {
        _lastRunId = newRunId;
        _armedRunId = 0;
    }
}
