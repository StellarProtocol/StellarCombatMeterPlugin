using Stellar.CombatMeter.AutoArchive;

namespace Stellar.CombatMeter;

public sealed partial class Plugin
{
    // Pure ARM/COMMIT/DISCARD run-boundary state machine (Task 1, spec
    // 2026-08-12-combatmeter-run-boundary-design.md). C-mode only (inWorldLoading: null) until the
    // plugin's WindowSpec/2.0.0 migration exposes the loading bit (Task 4 then passes the real edge).
    private readonly RunBoundaryTracker _runBoundary = new();

    // The ONE run-boundary bank+reset block (spec §3 COMMIT). Called by OnSceneChanged (Plugin.History.cs
    // — the scene-change boundary, today's path, unchanged semantics) and by PollRunBoundary below (the
    // poll-driven commit: a missed-scene-event boundary — the re-entry yank / open-world line switch).
    // Invariant 2 (owner-approved rewording 2026-08-12, docs/recon/combatmeter-archive-flow.md):
    // _lastRunId resets ONLY here — at a confirmed run boundary — never in Clear().
    private void RunBoundaryCore(AutoArchive.ArchiveReason reason)
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
        ManualArchive(reason);
        // The outgoing run is now archived under its OWN latched id (LevelUuid = _lastRunId) — clear the
        // latch so a later archive (an empty scene hop, or the next floor before its own combat
        // re-latches) can't reuse this run's id. The next run re-latches _lastRunId at its combat start
        // (Plugin.Capture.EnsureCombatStarted).
        _lastRunId = 0;
    }

    // Run-boundary poll (spec 2026-08-12): C-mode until the WindowSpec migration exposes the loading
    // bit (then Task 4 passes the real edge). Alloc-free: two long reads + an enum compare. Called from
    // Plugin.cs's OnUpdate BEFORE TrackClearLatch() so a commit banks the OLD run before the new run's
    // state starts tracking. The CommittedOldRunId == _lastRunId guard makes the commit a no-op when
    // the scene path already banked it this tick (belt on top of NotifySceneBoundaryHandled). Headless-
    // untestable end-to-end (no test instantiates a live Plugin — see tests/ convention); correctness
    // rides on RunBoundaryTrackerTests (the pure decision) + the full suite staying green (the shared
    // RunBoundaryCore behaves identically to pre-extraction OnSceneChanged).
    private void PollRunBoundary()
    {
        var boundaryAction = _runBoundary.Observe(
            _services.Dungeon.CurrentRunId, _services.Dungeon.RunTimerStartMs,
            inWorldLoading: null, combatEvent: false);
        if (boundaryAction == RunBoundaryTracker.BoundaryAction.Commit && _runBoundary.CommittedOldRunId == _lastRunId)
            RunBoundaryCore(AutoArchive.ArchiveReason.RunBoundary);
    }
}
