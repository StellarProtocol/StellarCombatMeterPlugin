namespace Stellar.CombatMeter;

// Per-frame tick + throttled snapshot rebuild. Split out of Plugin.cs (500-LoC cap).
public sealed partial class Plugin
{
    // Snapshot-rebuild throttle. The window bindings poll at the framework's capped cadence (~10 Hz), so there
    // is no point rebuilding the row snapshots (which allocate display strings) every 60 fps frame — rebuild at
    // the same ~10 Hz the bindings consume. The bar-fraction lerp lives in BuildRowData and so steps at this
    // cadence too, which matches what the bindings can actually show.
    private const float SnapshotIntervalS = 0.1f;
    private float _snapshotAccum;

    private void OnUpdate(float deltaTime)
    {
        DrainPortraitAcks();
        DrainSocialCatchup();
        EnsureReadyCheckSubscribed();
        TickRowMenuPlace();
        TickMorePopover();      // Plugin.MorePopover.cs — re-assert the anchored rect after remount; close with the meter
        TickDebuffTipPlace();   // Plugin.DebuffTooltip.cs — re-assert cursor rect after destroy-on-hide remount
        PumpClassIcons();
        PumpDungeonIcon();
        TickEntitySnapshots(deltaTime);
        TickReadyCheckCooldown(deltaTime);
        TickReadyCheckResult(deltaTime);
        TickReadyCheck(deltaTime);
        TickReplayCapture(deltaTime);
        TickReplayDiagnostics(deltaTime);
        PumpEffectCatalog();    // Plugin.EffectCatalog.cs — sliced Settings › Effects catalog build; one bool test when idle
        _snapshotAccum += deltaTime;
        if (_snapshotAccum < SnapshotIntervalS) return;
        _snapshotAccum = 0f;
        PersistUploadStateIfDirty();   // re-persist history after an async upload settled its Done/Failed phase
        DrainDiscordPendingPosts();    // Discord webhook: resolve a link-wait post once its deadline/link lands
        DrainContentKindsNotice();     // surface a manual content-list refresh result (Notifications is main-thread only)
        DetectSelfImagineCasts();   // ~10 Hz: LocalCooldowns begin-advance = self imagine cast (pre-combat capable)
        PollRunBoundary();           // Plugin.RunBoundary.cs — BEFORE TrackClearLatch (bank OLD run first)
        TrackClearLatch();           // ~10 Hz: run-scoped clear latch — UNCONDITIONAL. DO NOT move inside
                                     // TickAutoArchiveTriggers or behind the _autoArchive.Enabled gate: the kill
                                     // latch MUST track even in manual-only mode so a MANUAL archive of a cleared
                                     // run still reads "kill" (owner design 2026-08-06). Headless-untestable —
                                     // this call-order guarantee has no unit test; this comment IS the guard.
        TickBossStatus();            // ~10 Hz: boss kill-state poll — ALWAYS-ON, runs with the MASTER
                                     // Auto-archive toggle OFF too (owner ruling 2026-08-14). DO NOT move
                                     // it back inside TickAutoArchiveTriggers — see Plugin.BossDetection.cs.
        TickRelaunchMarker();        // mid-relaunch recovery (Plugin.RelaunchMarker.cs): ~30 s marker heartbeat + stale-marker clear
        TickAutoArchiveTriggers();   // ~10 Hz trigger poll (auto-archive spec Part B)
        TickRunUploadQueue();        // drains the run-level "Upload all" queue, one segment at a time
        TickLoadoutCapture();        // ~10 Hz: per-class loadout accumulator (poll profession + run-boundary reset)
        RebuildSnapshots();
    }

    // Rebuild the visible window's row snapshot (throttled to ~10 Hz), so the element Funcs read cached rows
    // (no per-poll service scan / formatting). Only the shown window pays. The main meter's rows stay at this
    // ~10 Hz: a separate ~5 Hz main-row throttle was tried for raid perf (2026-11-10) and read as too sluggish —
    // the redraw saving now comes from DisplayQuantize (sub-pixel changes collapse to the same value, so the
    // framework skips the rebind) plus the bar lerp's 1 px settle in Plugin.List.cs.
    private void RebuildSnapshots()
    {
        if (_mainWindow.IsShown)
        {
            // Geometry only once the framework has placed the window at its saved layout (Plugin.ModeGeometry.cs settle
            // gate): before that Rect is the (20,20) mount placeholder, and a SetRect would persist it (2026-11-17 bug).
            if (MainGeometrySettled())
            {
                CaptureModeGeometry();
                // Re-fit the window height (ONE SetRect) on the first settled tick / a 5↔20 switch. NOT on leadership —
                // the LEADER line's slot is always reserved (Plugin.LeaderLine.cs).
                if (_viewMode == ViewMode.PartyFocus) SyncPartyFocusHeight();
            }
            if (_viewMode == ViewMode.PartyFocus)
            {
                SyncLeaderLine();   // LEADER line on top vs blank at the bottom, no window geometry → no settle gate needed
                RebuildPartyFocusRows();
            }
            else RebuildListRows();
        }
        else ResetGeometrySettle();
        if (_historyWindow.IsShown) RebuildHistorySnapshots();
        if (_skillBreakdownWindow.IsShown) RebuildSkillRows();
        if (_snapshotWindow.IsShown) RebuildSnapshotRows();
    }
}
