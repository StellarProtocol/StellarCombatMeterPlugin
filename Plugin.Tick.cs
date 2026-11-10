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
        TickDebuffTipPlace();   // Plugin.DebuffTooltip.cs — re-assert cursor rect after destroy-on-hide remount
        PumpClassIcons();
        PumpDungeonIcon();
        TickEntitySnapshots(deltaTime);
        TickReadyCheckCooldown(deltaTime);
        TickReadyCheckResult(deltaTime);
        TickReadyCheck(deltaTime);
        TickReplayCapture(deltaTime);
        TickReplayDiagnostics(deltaTime);
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

    // Main meter rows refresh at ~5 Hz, not every ~10 Hz tick (raid perf 2026-11-10: a 20-player fight changed
    // ~108 UI graphics/frame, the meter's rows among the top). Only the DISPLAY is throttled — combat events still
    // accumulate in full on every event, and the history/breakdown/snapshot windows keep the 10 Hz tick. The bar
    // lerp (Plugin.List.cs BarLerpPerRefresh) is scaled to this rate so the animation speed is unchanged. A window
    // that was hidden refreshes on its first shown tick, so opening it never shows stale rows.
    private const float MainRowsIntervalS = 0.2f;
    private const float BarLerpPerRefresh = 0.33f;
    private float _mainRowsLastAt = float.NegativeInfinity;
    private bool  _mainRowsShownLastTick;

    private bool MainRowsDue()
    {
        var now = UnityEngine.Time.unscaledTime;
        // The tick lands on frame boundaries (≥0.1 s apart), so allow a frame of slack or 2 ticks could miss 0.2 s.
        var due = !_mainRowsShownLastTick || now - _mainRowsLastAt >= MainRowsIntervalS - 0.03f;
        _mainRowsShownLastTick = true;
        if (due) _mainRowsLastAt = now;
        return due;
    }

    // Rebuild the visible window's row snapshot (throttled to ~10 Hz; the main meter's rows to ~5 Hz via
    // MainRowsDue), so the element Funcs read cached rows (no per-poll service scan / formatting). Only the shown
    // window pays.
    private void RebuildSnapshots()
    {
        if (!_mainWindow.IsShown) _mainRowsShownLastTick = false;
        else if (MainRowsDue())
        {
            CaptureModeGeometry();
            if (_viewMode == ViewMode.PartyFocus)
            {
                // Follow a live 5↔20 size switch: re-fit the window when the party size changes.
                if (_lastViewWas20 != IsRaid20View) { _lastViewWas20 = IsRaid20View; RefreshPartyFocusHeight(); }
                RebuildPartyFocusRows();
            }
            else RebuildListRows();
        }
        if (_historyWindow.IsShown) RebuildHistorySnapshots();
        if (_skillBreakdownWindow.IsShown) RebuildSkillRows();
        if (_snapshotWindow.IsShown) RebuildSnapshotRows();
    }
}
