using System;
using Stellar.Abstractions.Domain;
using Stellar.CombatMeter.AutoArchive;

namespace Stellar.CombatMeter;

public sealed partial class Plugin
{
    // Pure ARM/COMMIT/DISCARD run-boundary state machine (Task 1, spec
    // 2026-08-12-combatmeter-run-boundary-design.md). B-mode glue landed (Task 4, post-2.0.0
    // migration): PollRunBoundary now passes the real GameUIState.Loading bit, so the tracker's rising/
    // falling-edge ARM/COMMIT/DISCARD logic is live, not just its C-mode (id-change-only) half.
    // C-mode id-changes remain grace-windowed (rb-task-2 review fix): the poll defers to the scene path
    // for SceneHandledGraceMs before committing on its own — see RunBoundaryTracker.Observe.
    private readonly RunBoundaryTracker _runBoundary = new();

    // The four per-run tracker resets, split out of RunBoundaryCore (review fix, rb-task-2 finding 2)
    // so OnSceneChanged (Plugin.History.cs) can run them UNCONDITIONALLY — including on the very first
    // scene observation — exactly like 3fd7559 did, while the archive half (BankRunBoundary below)
    // stays gated behind OnSceneChanged's own early-return guard. RunBoundaryCore still composes both
    // halves as one unit for the poll, which has no separate earlier reset call of its own.
    // Since 2026-08-26 this is ALSO called from RunSegmentCut (the evidence-less belt cut) —
    // deliberately: the pre-2026-08-26 belt commit ran this identical reset at these identical
    // moments (measured 7×/raid), and the owner's "keep cutting" decision preserves that behavior
    // byte-for-byte; only the identity block (BankRunBoundary's half, below) moved out of the cut
    // path. Where a field comment below says a value must "survive a mid-run archive", that
    // contrast is against the WITHIN-RUN auto-archive triggers (wipe/boss/idle/stage call
    // ManualArchive directly) — those still never run this reset; RunSegmentCut is boundary-shaped
    // (like RunBoundaryCore/OnSceneChanged), not a within-run trigger.
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
        // Critical 1 fix (final review): latch the outgoing stage's membership BEFORE this clears it —
        // the scene/run-boundary archive that follows (BankRunBoundary, below) reads the latch when its
        // own BuildHistoryEntry finds the live set already emptied (Plugin.BossDetection.cs).
        LatchStageBosses();
        _stageBosses.Clear();
        _memberLastHpFrac.Clear();
        // Elite capture channel (owner ruling 2026-08-13): same run/scene-boundary reset as the stage-
        // boss set above — CAPTURE ONLY, feeds nothing in AutoArchive/BossStatus/verdict paths (see
        // AutoArchive/EliteSet.cs). Latch BEFORE clearing (mirrors LatchStageBosses/_stageBosses.Clear())
        // so the scene archive's deferred BuildHistoryEntry — which runs AFTER this reset, via
        // BankRunBoundary below — still resolves this run's elites through ResolveCurrentElites's
        // sticky-latch fallback.
        LatchElites();
        _eliteSet.Clear();
        // Appear-sourced imagine-cast novelty set (Plugin.Capture.cs, 2026-08-14): run-scoped like the
        // trackers above, deliberately NOT in Clear() — a long-lived companion surviving a mid-run
        // archive must stay "seen", or its next AOI blink after the archive would mint a phantom cast.
        // CAPTURE ONLY — feeds nothing in AutoArchive/BossStatus/verdict paths.
        _seenSummons.Clear();
        // Sticky bucket-routing memory (Plugin.BucketRouting.cs, owner-approved fix 2026-08-15): the
        // last routing input, run-scoped exactly like the two live sets it backs up — a new run's
        // entities are new entities, and holding a previous run's ids would let a recycled entity id
        // credit a boss/elite that is not even in this instance. Deliberately NOT in Clear(): that runs
        // on every archive, and the whole point of the map is that a boss cut banks at the kill while
        // its DoT tail keeps ticking into the NEXT segment — those ticks must still credit that boss.
        // CAPTURE ONLY — feeds nothing in AutoArchive/BossStatus/verdict paths.
        _stickyRoutes.Clear();
        RecomputeUploadPolicyCache();   // new run ⇒ re-resolve kind + hot-path upload bools (Plugin.UploadPolicy.cs)
    }

    // The archive half of the bank+reset block: banks the outgoing run and clears the run-id latch.
    // Split out (review fix, rb-task-2 finding 2) so OnSceneChanged can call it AFTER its own
    // early-return guard without re-running ResetRunScopedTrackers, which OnSceneChanged already ran
    // unconditionally above that guard.
    private void BankRunBoundary(AutoArchive.ArchiveReason reason)
    {
        var outgoingRunId = _lastRunId;   // captured BEFORE the archive zeroes the latch below
        ManualArchive(reason);
        // The outgoing run is now archived under its OWN latched id (LevelUuid = _lastRunId) — clear the
        // latch so a later archive (an empty scene hop, or the next floor before its own combat
        // re-latches) can't reuse this run's id. The next run re-latches _lastRunId at its combat start
        // (Plugin.Capture.EnsureCombatStarted).
        _lastRunId = 0;
        // Reset the once-per-run dungeon-start latch alongside _lastRunId, at the SAME confirmed run
        // boundary and NOWHERE else (never in Clear()) — so the NEXT run re-latches its own start fresh at
        // its first combat event and cannot inherit this run's start. Keeping it non-zero across the
        // boundary would re-key the next run under this run's DungeonStartMs. See _lastRunStartMs's doc
        // (Plugin.cs) + LatchRunStartMs (Plugin.History.cs).
        _lastRunStartMs = 0;
        _relaunchPartyFallback = 0;   // per-run, same lifecycle as _lastRunStartMs (Plugin.RelaunchMarker.cs)
        // Drop the mid-dungeon-relaunch marker ONLY when THIS boundary is the marked run actually ending —
        // outgoingRunId (the run being banked) == the marker's run. A genuine leave/run-end clears it (so a
        // later re-entry of the same instance is FRESH); a crash/relaunch never reaches this path, so its
        // marker stands for recovery. CRITICAL: BankRunBoundary ALSO fires on a dungeon ENTRY (a reconnect
        // loads into the instance → OnSceneChanged with outgoingRunId==0, BEFORE the first combat) — an
        // unconditional clear there wiped the marker the restore needs (root cause of the first owner test's
        // missing [relaunch] line). ShouldClearOnBoundary gates that out. See Plugin.RelaunchMarker.cs.
        if (RelaunchMarker.ShouldClearOnBoundary(_activeRunMarker, outgoingRunId))
            ClearActiveRunMarker();
        // New finding (re-review, 2026-08-13) — stage-boss latch staleness: _segmentStageBosses
        // (Plugin.BossDetection.cs) is otherwise only reset by Clear(), which ManualArchive's skip-empty
        // (Plugin.History.cs, `_stats.Count == 0` early return) and suppressed-junk (ShouldSuppressAutoArchive
        // early return) paths both SKIP. A boss admitted by a whiffed/abandoned pull (CheckBossCandidate
        // runs on every combat event, incl. 0-amount ones) then dropped via one of those two early returns
        // left the latch pinned to a dead run's boss past its own run boundary — a LATER, unrelated banked
        // archive (next run, no boss engaged) would read it via ResolveCurrentStageBosses/BuildBossHpTracks
        // and misattribute a stale boss to itself. Reset unconditionally here, right after ManualArchive
        // above returns (whichever branch it took — banked, skip-empty, or suppressed all count as "this
        // boundary's archive attempt already had its read"), so the latch can never survive past the
        // boundary entitled to consume it. Boundary-scoped ONLY — this reset runs from RunBoundaryCore's
        // poll-commit path, OnSceneChanged's post-guard call, and (2026-08-26) RunSegmentCut's
        // evidence-less belt cut (its own inline copy of this same staleness fix — see that method's
        // doc) — still never from a within-run auto-archive trigger (wipe/boss/idle/stage calls
        // ManualArchive directly, not through here) — so a still-open stage's latch keeps flowing
        // forward correctly within the same run.
        // GLUE GAP (documented, not pinned — same IL2CPP-adjacent convention as the other instance-state
        // mutations in this region; Plugin can't be instantiated in tests): this call itself is untestable
        // headless. The invariant it restores — empty live set + empty latch yields no bosses — is pinned
        // by PreferLiveStageBosses_both_empty_returns_the_empty_live_set (AutoArchiveContentGuardTests.cs).
        _segmentStageBosses = Array.Empty<(EntityId Id, int ConfigId, bool Killed)>();
        // Elite capture channel: same staleness fix as _segmentStageBosses above (2026-08-13) — a
        // dropped/skip-empty/suppressed archive attempt must not leave this latch pinned past its own
        // run boundary. CAPTURE ONLY either way — no engine/verdict impact.
        _segmentElites = Array.Empty<(EntityId Id, int ConfigId, bool Killed)>();
        NotifyDiscordRunEnded(outgoingRunId);   // read-only downstream observer (Plugin.DiscordWebhook.cs)
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

    // Segment CUT with run identity preserved (raid run-split fix, spec 2026-08-26). A mid-run
    // loading flash resolved by a combat event with NO re-key evidence (same run id, same
    // IRunTimer.Epoch) is a stage transition INSIDE one run — measured 7×/raid on Clash! Field of
    // Forgotten Illusions (668840469433679872 / 420861014951591936). Bank the segment exactly as
    // the old boundary commit did (same ArchiveReason → trig "boundary", same run-scoped tracker
    // resets, same stage-boss/elite latch hygiene) but KEEP the identity block: _lastRunId,
    // _lastRunStartMs and _relaunchPartyFallback survive, the relaunch marker stays, and
    // NotifyDiscordRunEnded does not fire (the run did NOT end — it used to fire 7×/raid here).
    // This is what makes a split structurally impossible without run-id/epoch evidence: the
    // once-per-run start latch can no longer be reset mid-run, so a framework timer rank-upgrade
    // has nothing to re-latch through. Owner decision 2026-08-26: cuts are PRESERVED (the
    // scripted-death stage seam is cut only by this path), identity is not.
    private void RunSegmentCut(AutoArchive.ArchiveReason reason)
    {
        ResetRunScopedTrackers();
        ManualArchive(reason);
        // Same post-archive latch hygiene as BankRunBoundary (see its 2026-08-13 staleness note):
        // this boundary-shaped archive attempt had its read; the latches must not outlive it. Load-
        // bearing order, not an inert leftover: ResetRunScopedTrackers (above) LATCHES the outgoing
        // stage into _segmentStageBosses/_segmentElites (LatchStageBosses/LatchElites) before
        // clearing the live sets; ManualArchive's BuildHistoryEntry then CONSUMES that latch to build
        // THIS segment's boss/elite rows; only once that read has happened may the latch be CLEARED
        // below — same latch→consume→clear order as BankRunBoundary.
        _segmentStageBosses = Array.Empty<(EntityId Id, int ConfigId, bool Killed)>();
        _segmentElites = Array.Empty<(EntityId Id, int ConfigId, bool Killed)>();
    }

    // Run-boundary poll (spec 2026-08-12, B-mode glue landed Task 4). Alloc-free: four reads + a mask
    // compare + an enum compare. Called from Plugin.cs's OnUpdate BEFORE TrackClearLatch() so a commit
    // banks the OLD run before the new run's state starts tracking. The CommittedOldRunId == _lastRunId
    // guard makes the commit a no-op when the scene path already banked it this tick (belt on top of
    // NotifySceneBoundaryHandled). The tracker itself grace-windows a C-mode id-change with no loading
    // signal yet (SceneHandledGraceMs, review fix rb-task-2 finding 1) so this poll still defers to the
    // scene path on every NORMAL floor transition; the loading bit below is what lets it ALSO catch the
    // re-entry-yank / same-instance-teleport cases the pure id-change signal can't distinguish (spec §3).
    // Headless-untestable end-to-end (no test instantiates a live Plugin — see tests/ convention);
    // correctness rides on RunBoundaryTrackerTests (the pure decision) + the full suite staying green
    // (the shared RunBoundaryCore/ResetRunScopedTrackers/BankRunBoundary behave identically to
    // pre-extraction OnSceneChanged).
    private void PollRunBoundary()
    {
        bool loading = (_services.ClientState.UiState & GameUIState.Loading) != 0;
        var boundaryAction = _runBoundary.Observe(
            _services.Dungeon.CurrentRunId, _services.RunTimer.Epoch,
            inWorldLoading: loading, combatEvent: false, nowMs: _services.CombatSnapshot.ServerNowMs);
        if (boundaryAction == RunBoundaryTracker.BoundaryAction.Commit && _runBoundary.CommittedOldRunId == _lastRunId)
        {
            LogRunBoundary("poll-runid", _runBoundary.CommittedOldRunId, _services.Dungeon.CurrentRunId, _stats.Count);
            RunBoundaryCore(AutoArchive.ArchiveReason.RunBoundary);
        }
    }

    // Combat-event belt (Task 4, spec 2026-08-12-combatmeter-run-boundary-design.md §5). Called from
    // Plugin.Capture.cs's OnCombatEvent, before MaybeCutForBossPhase/EnsureCombatStarted. A genuine
    // DamageDealt event cannot fire mid-load, so its mere arrival while the tracker is ARMED (a B-mode
    // loading rising edge already fired) is itself proof the load has already resolved — resolves the
    // boundary NOW instead of waiting on the next ~10Hz PollRunBoundary tick. Hot path when NOT armed
    // (the overwhelming majority of combat events) costs exactly one field read (IsArmed) before this
    // method returns. Same Commit + CommittedOldRunId == _lastRunId no-op guard as PollRunBoundary
    // (belt on top of a scene/poll commit that already banked this tick).
    private void ResolveArmedBoundaryBelt()
    {
        if (!_runBoundary.IsArmed) return;
        bool loading = (_services.ClientState.UiState & GameUIState.Loading) != 0;
        var boundaryAction = _runBoundary.Observe(
            _services.Dungeon.CurrentRunId, _services.RunTimer.Epoch,
            inWorldLoading: loading, combatEvent: true, nowMs: _services.CombatSnapshot.ServerNowMs);
        if (boundaryAction == RunBoundaryTracker.BoundaryAction.Commit && _runBoundary.CommittedOldRunId == _lastRunId)
        {
            LogRunBoundary("combat-belt", _runBoundary.CommittedOldRunId, _services.Dungeon.CurrentRunId, _stats.Count);
            RunBoundaryCore(AutoArchive.ArchiveReason.RunBoundary);
        }
        else if (boundaryAction == RunBoundaryTracker.BoundaryAction.Cut && _lastRunId != 0)
        {
            // Guard: a Cut with no latched _lastRunId means a boundary Commit already banked (and
            // zeroed the identity latch) earlier THIS tick — the absorb-pending-into-rising-edge
            // re-arm interleave lets the tracker independently resolve Cut on the same frame its own
            // Commit already zeroed Plugin._lastRunId. There is nothing left to cut; skip rather than
            // stamp a bogus LevelUuid = 0 segment archive.
            LogRunBoundary("combat-belt-cut", _lastRunId, _services.Dungeon.CurrentRunId, _stats.Count);
            RunSegmentCut(AutoArchive.ArchiveReason.RunBoundary);
        }
    }
}
