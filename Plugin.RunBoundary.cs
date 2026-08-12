using Stellar.CombatMeter.AutoArchive;

namespace Stellar.CombatMeter;

public sealed partial class Plugin
{
    // Pure ARM/COMMIT/DISCARD run-boundary state machine (Task 1, spec
    // 2026-08-12-combatmeter-run-boundary-design.md). C-mode only (inWorldLoading: null) until the
    // plugin's WindowSpec/2.0.0 migration exposes the loading bit (Task 4 then passes the real edge).
    // C-mode id-changes are grace-windowed (rb-task-2 review fix): the poll defers to the scene path
    // for SceneHandledGraceMs before committing on its own — see RunBoundaryTracker.Observe.
    private readonly RunBoundaryTracker _runBoundary = new();

    // The four per-run tracker resets, split out of RunBoundaryCore (review fix, rb-task-2 finding 2)
    // so OnSceneChanged (Plugin.History.cs) can run them UNCONDITIONALLY — including on the very first
    // scene observation — exactly like 3fd7559 did, while the archive half (BankRunBoundary below)
    // stays gated behind OnSceneChanged's own early-return guard. RunBoundaryCore still composes both
    // halves as one unit for the poll, which has no separate earlier reset call of its own.
    private void ResetRunScopedTrackers()
    {
        // New run = new run: forget which bosses died in the previous one, so the same boss template
        // in the next run cuts normally. Deliberately NOT in Clear() — that runs on every archive.
        _killedBosses.Clear();
        // Critical A (review round 2026-07-27, second pass; carried into the multi-boss set, Task 2
        // 2026-08-12): the tracked boss state gets a per-run reset here too. Before the original fix, a
        // run that ended without the tracked boss ever being observed at hp<=0 (wipe-and-leave — the
        // owner's normal loop, an abandoned pull, a fail-out, the boss despawning on reset) left the
        // tracker pinned to a dead-and-gone entity for the REST OF THE SESSION, blocking every later
        // boss — in this run or the next one — from ever being adopted again, so no BossKill ever fires
        // again. Scoping this to the run boundary is what makes a fresh dungeon in the same session
        // detect its own boss(es) normally. (A sibling _settleBossId used to get the same reset here —
        // retired with the rest of finding 3's boss-only settle clock, owner ruling 2026-07-28.)
        _stageBosses.Clear();
        _memberLastHpFrac.Clear();
        RecomputeUploadPolicyCache();   // new run ⇒ re-resolve kind + hot-path upload bools (Plugin.UploadPolicy.cs)
    }

    // The archive half of the bank+reset block: banks the outgoing run and clears the run-id latch.
    // Split out (review fix, rb-task-2 finding 2) so OnSceneChanged can call it AFTER its own
    // early-return guard without re-running ResetRunScopedTrackers, which OnSceneChanged already ran
    // unconditionally above that guard.
    private void BankRunBoundary(AutoArchive.ArchiveReason reason)
    {
        ManualArchive(reason);
        // The outgoing run is now archived under its OWN latched id (LevelUuid = _lastRunId) — clear the
        // latch so a later archive (an empty scene hop, or the next floor before its own combat
        // re-latches) can't reuse this run's id. The next run re-latches _lastRunId at its combat start
        // (Plugin.Capture.EnsureCombatStarted).
        _lastRunId = 0;
    }

    // The ONE run-boundary bank+reset block (spec §3 COMMIT), composed of the two halves above. Called
    // by PollRunBoundary below (the poll-driven commit: a missed-scene-event boundary — the re-entry
    // yank / open-world line switch); OnSceneChanged (Plugin.History.cs) calls the two halves
    // separately, straddling its own early-return guard (see ResetRunScopedTrackers's doc).
    // Invariant 2 (owner-approved rewording 2026-08-12, docs/recon/combatmeter-archive-flow.md):
    // _lastRunId resets ONLY here — at a confirmed run boundary — never in Clear().
    private void RunBoundaryCore(AutoArchive.ArchiveReason reason)
    {
        ResetRunScopedTrackers();
        BankRunBoundary(reason);
    }

    // Run-boundary poll (spec 2026-08-12): C-mode until the WindowSpec migration exposes the loading
    // bit (then Task 4 passes the real edge). Alloc-free: three reads + an enum compare. Called from
    // Plugin.cs's OnUpdate BEFORE TrackClearLatch() so a commit banks the OLD run before the new run's
    // state starts tracking. The CommittedOldRunId == _lastRunId guard makes the commit a no-op when
    // the scene path already banked it this tick (belt on top of NotifySceneBoundaryHandled). The
    // tracker itself now grace-windows a C-mode id-change (SceneHandledGraceMs, review fix rb-task-2
    // finding 1) so this poll defers to the scene path on every NORMAL floor transition and only fires
    // for real when the scene path is genuinely missed. Headless-untestable end-to-end (no test
    // instantiates a live Plugin — see tests/ convention); correctness rides on
    // RunBoundaryTrackerTests (the pure decision) + the full suite staying green (the shared
    // RunBoundaryCore/ResetRunScopedTrackers/BankRunBoundary behave identically to pre-extraction
    // OnSceneChanged).
    private void PollRunBoundary()
    {
        var boundaryAction = _runBoundary.Observe(
            _services.Dungeon.CurrentRunId, _services.Dungeon.RunTimerStartMs,
            inWorldLoading: null, combatEvent: false, nowMs: _services.CombatSnapshot.ServerNowMs);
        if (boundaryAction == RunBoundaryTracker.BoundaryAction.Commit && _runBoundary.CommittedOldRunId == _lastRunId)
        {
            LogRunBoundary("poll-runid", _runBoundary.CommittedOldRunId, _services.Dungeon.CurrentRunId, _stats.Count);
            RunBoundaryCore(AutoArchive.ArchiveReason.RunBoundary);
        }
    }
}
