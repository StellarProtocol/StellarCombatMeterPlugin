namespace Stellar.CombatMeter.AutoArchive;

/// <summary>
/// Pure run-boundary state machine (spec 2026-08-12-combatmeter-run-boundary-design.md §3).
/// Observed per plugin frame tick. C-mode (inWorldLoading=null): a CurrentRunId tick-over-tick
/// change (old != 0) is a CANDIDATE boundary — it does NOT commit on the spot. The poll runs every
/// frame, before the wire's own scene-change event has necessarily been dispatched to
/// <c>OnSceneChanged</c>, so an immediate commit would steal the boundary from the scene path on
/// every ordinary floor transition (review fix, rb-task-2 finding 1). Instead the candidate is
/// recorded PENDING (old id + a <see cref="SceneHandledGraceMs"/> deadline); <see
/// cref="NotifySceneBoundaryHandled"/> arriving first (the normal case) cancels it silently, and
/// only a later Observe tick that crosses the deadline while still pending returns Commit — the
/// missed-scene-event heal (re-entry yank / open-world line switch) this poll exists for. B-mode
/// adds the load cycle: rising edge ARMs a (runId, runTimerStartMs) reference; the falling edge
/// commits when EITHER differs (yank: stale id, fresh timer — the measured IkriESpwsl shape) and
/// discards when both match (same-instance teleport — replay continuity, P0). An id change WHILE a
/// concrete loading bit is already known (B-mode, inWorldLoading is not null) commits immediately,
/// same as before this fix — the grace window only guards the pure-C-mode poll usage, where no
/// other signal distinguishes "the scene path just hasn't run yet" from "the scene path was
/// missed". Timer changes compare ONLY across a load cycle: mid-run rank-upgrade refinements
/// (SetRunTimerStart) must never cut a run. Alloc-free; plain fields only.
/// </summary>
internal sealed class RunBoundaryTracker
{
    internal enum BoundaryAction { None, Commit, Discard }

    // Grace window the pure-C-mode poll gives the scene path to claim a run-id change itself
    // (NotifySceneBoundaryHandled) before this tracker commits it unilaterally. Normal play calls
    // OnSceneChanged well within this window on every ordinary floor transition; only a genuinely
    // missed scene event (the poll's whole reason to exist) ever reaches the deadline.
    internal const long SceneHandledGraceMs = 2500;

    private long _lastRunId;
    private long _armedRunId;        // 0 = not armed
    private long _armedTimerMs;
    private bool _lastLoading;

    private long _pendingOldRunId;     // 0 = no C-mode boundary pending
    private long _pendingDeadlineMs;

    /// <summary>The OLD run id of the boundary returned by the last Commit-returning Observe call.</summary>
    internal long CommittedOldRunId { get; private set; }

    internal BoundaryAction Observe(long runId, long runTimerStartMs, bool? inWorldLoading, bool combatEvent, long nowMs)
    {
        var action = BoundaryAction.None;
        bool loading = inWorldLoading ?? false;

        // C: id change with a real prior id = a candidate boundary.
        if (runId != _lastRunId && _lastRunId != 0)
        {
            if (inWorldLoading is null)
            {
                // Pure C-mode (the poll): defer to the scene path — record PENDING rather than
                // committing here. A second/third id-change while already pending (e.g. two rapid
                // floor hops before the deadline) must not reset the clock or swap which old id
                // eventually commits, so only the FIRST one latches _pendingOldRunId/_pendingDeadlineMs.
                if (_pendingOldRunId == 0)
                {
                    _pendingOldRunId = _lastRunId;
                    _pendingDeadlineMs = nowMs + SceneHandledGraceMs;
                }
            }
            else
            {
                // B-mode: the id changed with a concrete loading bit already in hand — no scene-path
                // race to defer to here (unchanged from before this fix; "C wins inside the load too").
                CommittedOldRunId = _lastRunId;
                action = BoundaryAction.Commit;
            }
            _armedRunId = 0;
        }
        else if (_pendingOldRunId != 0)
        {
            if (nowMs >= _pendingDeadlineMs)
            {
                CommittedOldRunId = _pendingOldRunId;   // missed scene event — commit for real
                action = BoundaryAction.Commit;
                _pendingOldRunId = 0;
            }
            // else: still within the grace window — None; wait for NotifySceneBoundaryHandled or the deadline.
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
    /// poll does not see a change and double-commit (invariant 6: one entry per boundary), and
    /// cancel any pending C-mode boundary silently (the normal case: the scene path always wins
    /// when it fires within the grace window).</summary>
    internal void NotifySceneBoundaryHandled(long newRunId)
    {
        _lastRunId = newRunId;
        _armedRunId = 0;
        _pendingOldRunId = 0;
    }
}
