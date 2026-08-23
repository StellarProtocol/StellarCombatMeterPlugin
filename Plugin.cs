using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Plugins;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

/// <summary>
/// Real-time combat damage/healing meter with history and skill breakdown. Subscribes to
/// <see cref="ICombatEvents.CombatEventOccurred"/> and maintains a per-source <see cref="SourceStats"/> map.
/// Three windows: a live meter (F9, Borderless — bespoke <see cref="MeterRowElement"/> rows with animated bars),
/// a History log (Shift+F9, master-detail), and a Skill Breakdown drill-in. Demonstrates bespoke element types,
/// multi-window state coordination, encounter-scoped data, and the snapshot pattern: state is copied once per
/// tick into cached rows so element Funcs never allocate or scan during refresh.
/// </summary>
public sealed partial class Plugin : IStellarPlugin
{
    public string Name => "CombatMeter";

    private const float ListWidth  = 460f;
    private const float ListHeight = 360f;   // seeds ~6 visible rows; user-resizable via the ↘ grip
    private const float PartyFocusW = 480f;
    private const float PartyFocusH = 612f;   // 20-player: header + 4×5 grid (menus closed)
    private const float PartyFocus5H = 330f;  // 5-player: header + single group of 5 (menus closed) — verify in-game
    // Party-focus is a fixed grid (no Fill/scroll), so the inline Scope/Pause menu (▾) and the metric
    // dropdown push the grid past the window bottom → overlap. Grow the window by the open panel's height
    // instead of squeezing the grid. Heights measured from the rendered panels (main ≈69px); rounded up so
    // a worst case leaves a tiny bottom gap rather than overlapping. Menus are mutually exclusive.
    private const float MainMenuPanelH   = 72f;   // Scope/party-size row + separator + Pause/Archive/History row + spacer

    private readonly IPluginServices _services;
    private readonly ILocalization _loc;   // plugin-scoped UI-text resolver (framework 2.1.0); reads only, never sets the language

    private IWindowControl _mainWindow = null!;
    private IWindowControl _historyWindow = null!;
    private IWindowControl _skillBreakdownWindow = null!;
    private IWindowControl _snapshotWindow = null!;
    private IWindowControl _settingsWindow = null!;
    private IHotkeyAction _toggleAction = null!;
    private IHotkeyAction _historyAction = null!;
    private IHotkeyAction _resetAction = null!;
    private IHotkeyAction _archiveAction = null!;
    private IHotkeyAction _pauseAction = null!;
    private IHotkeyAction _modeAction = null!;
    private IHotkeyAction _partyFocusAction = null!;

    // Role colours (DPS/Tank/Healer) + the HP-spine colour, themeable. The spine is a plain HP bar:
    // its length tracks HP, its colour stays a steady green (matching the game's own HP bar) — it does
    // NOT switch tiers by HP fraction.
    private IColorSlot _roleDpsSlot = null!;
    private IColorSlot _roleTankSlot = null!;
    private IColorSlot _roleHealerSlot = null!;
    private IColorSlot _hpSlot = null!;
    private IColorSlot _selfAccentSlot = null!;

    // Inferred Battle-Imagine cooldown/charge cache for other players (self uses LocalCooldowns).
    private readonly ResonanceTracker _resTracker = new();

    // EntityId -> stats. Drives History / Archive / skill-breakdown. Live meter rows are driven by _agg.
    private readonly Dictionary<EntityId, SourceStats> _stats = new();
    private readonly MeterAggregator _agg = new();

    // Complete killing-blow list for the active encounter (frozen into history at archive). Uncapped:
    // it rides on the derived aggregates, not the truncation-bounded event ring.
    private readonly List<DeathEntry> _deaths = new();

    // Battle-imagine cast log (all players) — TRUE timestamps for the web replay timeline. The raw
    // event ring truncates on long fights (a 20-man world boss keeps only the tail), so bubbles built
    // from raw events bunched at the end; this list rides on the aggregates instead. Self casts come
    // from the LocalCooldowns begin-advance detector; other players' from the damage burst-gap logic —
    // _lastImagineHitMs holds each (source, base imagine id)'s last SEEN hit time (refreshed on every
    // hit, recorded only after a gap of silence). Capped as a runaway guard.
    private readonly List<ImagineCastEntry> _imagineCasts = new();
    private readonly Dictionary<(EntityId, int), long> _lastImagineHitMs = new();

    // EntityId -> per-second time-series (dealt/healing/taken). Frozen into history at archive.
    // Bucket count is HARD-CAPPED at TimelineMaxBuckets: the timeline coalesces (doubles bucket width)
    // past it, so the series stays bounded no matter how long the fight runs — a 1-hour encounter just
    // gets coarser buckets, not a bigger payload. 1800 keeps 1s resolution up to 30 min and ~2s at 1 hr.
    internal const int TimelineBucketMs = 1000;
    internal const int TimelineMaxBuckets = 1800;
    private readonly Dictionary<EntityId, SourceTimeline> _timelines = new();

    private SourceTimeline TimelineFor(EntityId id)
    {
        if (!_timelines.TryGetValue(id, out var t))
        {
            t = new SourceTimeline(TimelineBucketMs, TimelineMaxBuckets);
            _timelines[id] = t;
        }
        return t;
    }

    private readonly IConfigSection _prefs;

    // Combat-timer state (unix ms). _lastDamageMs also DOUBLES as the settle-window clock for EVERY
    // deferrable auto-archive reason, BossKill included (owner ruling 2026-07-28, superseding
    // 2026-07-26's boss-targeted narrowing — see Plugin.AutoArchive.cs's retired-SettleClockMs note and
    // the 2026-07-26-combatmeter-bosskill-settle-design.md spec's corrected §2.6): only ever stamped by
    // AccumulateDamage (player-source, non-heal damage — see its call site in OnCombatEvent), so heals
    // still never count, matching the half of the 2026-07-26 ruling that stands. A dedicated
    // boss-targeted clock (_lastBossDamageMs / _settleBossId / IsSettleBossDamage) used to narrow the
    // window for BossKill specifically; it is RETIRED — adds/DoTs elsewhere kept that narrower clock
    // quiet while still landing damage that should have held the window open, spilling into the head of
    // the FOLLOWING archive (owner: "there's mini dps that left to early of 2,4,6").
    private long _combatStartMs;
    private long _lastDamageMs;
    private long _lastRunId;   // THIS run's id, latched at combat start; the archive stamps it (LevelUuid). Reset ONLY at a confirmed run boundary (scene change / OnSceneChanged, or the RunBoundaryTracker poll's commit — Plugin.RunBoundary.cs) — NOT by Clear() — so mid-run archives keep the run's own id
    private long _lastTeamId;  // party id (GrpcTeam team_id) latched at combat start; 0 = solo/unformed
    // Per-run dungeon-start timestamp, latched ONCE per run (sibling of _lastRunId; the archive stamps it
    // as DungeonStartMs). Root cause (owner-verified on prod sea/YvLLO3YSc8 + sea/yVfTrPylk7, levelUuid
    // 366250583092363264): the archive used to stamp DungeonStartMs from the LIVE _services.Dungeon.
    // RunTimerStartMs per banked segment. At run end (Victory/settlement — the "~5s before jump" phase)
    // the GAME re-stamps its run timer (measured 680000 → 802000), so a post-kill boundary/tail segment
    // read a bogus "start"; the server keys run identity on <levelUuid>-<dungeonStartMs/1000>, so ONE run
    // split into TWO pages (a ~15s all-zero kill phantom appeared on the boards). LevelUuid did NOT split
    // because it uses the latched _lastRunId; DungeonStartMs was the last field still read live.
    // Fix: latch like _lastRunId, with ONE critical difference — LATCH ONCE PER RUN, never re-latch per
    // combat start. RunTimerStartMs is UNSTABLE (re-stamped at run end), so a post-kill tail's own combat
    // start would otherwise read the reset value and re-split; the once-per-run latch (0-retry until
    // nonzero, in EnsureCombatStarted) pins the run's original start. Reset to 0 ONLY at a confirmed run
    // boundary (BankRunBoundary, beside _lastRunId = 0) — NEVER by Clear(). 0 = never latched (open-world
    // has no dungeon timer) → the archive falls back to the live RunTimerStartMs. OUT OF SCOPE: the
    // mid-dungeon-relaunch split (docs/recon/run-identity-relaunch-split.md) is a separate, unrelated
    // runStartS-changes-on-relaunch issue this latch does not address.
    private long _lastRunStartMs;
    private int  _difficultyAtCombatStart;  // Master N level latched at combat start — CurrentDifficulty resets to 0 on a
                                            // run-id change (e.g. a fail-out to a new scene) that can precede archive.
    private bool _combatActive;
    // IDungeonState.LastSettlement is sticky for the whole dungeon run (framework keeps it across the
    // drop-to-0 on leave-scene, and across a same-uuid re-entry) — so its mere non-null-ness does NOT mean
    // THIS encounter ended in a kill; it may be left over from an earlier kill/segment in the same run.
    // Snapshotting it here at combat start lets ManualArchive tell "settlement changed during this
    // encounter" (genuine fresh kill) apart from "settlement was already sitting there" (stale/false).
    private DungeonSettlementInfo? _settlementAtCombatStart;
    // One CLEAR marker per run: set when ManualArchive banks the late-settlement clear marker (an empty
    // run-end archive whose fight was already banked + cleared), reset when the next encounter's combat
    // starts. Guards against a dungeon exit — which steps through several run-end archives while the clear
    // settlement is still sticky — banking duplicate markers. See ShouldBankEmptyClearMarker.
    private bool _clearMarkerBanked;
    // Run-scoped CLEAR latch (vault-floor "partial-not-kill" P0, run sea/qyvCSXteqC). A multi-floor
    // dungeon's floor is its OWN framework run; when the NEXT floor's run-id latches the framework's
    // SetCurrentRun WIPES IDungeonState.LastOutcome/LastSettlement — and that wipe lands BEFORE the
    // plugin's always-firing run-end (scene) archive banks the OUTGOING floor. So BuildHistoryEntry read a
    // blank live latch (freshSettlement null, outcome None) and the freshly-cleared floor archived as
    // "partial". These two fields latch the clear fact IN THE PLUGIN the tick it is first observed
    // (BuildAutoArchiveInputs, while the framework latch is still fresh) so it survives that wipe; the
    // archive verdict reads _clearedThisRun (ResolveVerdict's clearedThisRun arg) and _clearedSettlement
    // (the pass-time/score fallback). Set via the pure UpdateClearLatch seam; reset — alongside
    // _clearMarkerBanked — only at the NEXT encounter's combat start (EnsureCombatStarted), which runs
    // AFTER the outgoing floor's run-end archive has banked, so the clear cannot bleed into the next run.
    // Deliberately NOT reset by Clear() (which runs on every banked archive, incl. the fast-boss archive
    // that fires ~1 s before the settlement even lands) — that would erase the latch before the run-end
    // archive could read it.
    private bool _clearedThisRun;
    private DungeonSettlementInfo? _clearedSettlement;

    // Persisted UI state.
    private Metric     _metric = Metric.Dps;
    private FilterMode _filter = FilterMode.Party;
    private ViewMode   _viewMode = ViewMode.List;
    private bool       _lastViewWas20;   // tracks party-size view (5↔20) to re-fit the window on a live switch
    private bool       _paused;

    // Per-mode window geometry (the framework persists one rect per window id, so the plugin remembers each
    // view's size+position separately and restores it on mode switch — like the IMGUI build's list/party rects).
    private float _listW, _listH, _listX, _listY, _partyX, _partyY, _partyW;
    // Current window width (captured each tick) — drives the List spec/secondary/share collapse breakpoints.
    private float _listWidthNow = ListWidth;

    public Plugin(IPluginServices services)
    {
        _services = services;
        _loc = services.Localization;   // i18n P1: plugin-scoped catalog façade (auto-discovers embedded Lang/*.json)
        _services.Log.Info("[CombatMeter] plugin constructed");

        _inspectIconPng  = BuildInspectMagnifierPng();   // procedural magnifier for the history Inspect button (main thread)
        _locationPinPng  = BuildLocationPinPng();        // flat-white location pin for the Marking header button
        _checkmarkPng    = BuildCheckmarkPng();          // flat-white checkmark for the Ready Check header button
        _megaphonePng    = BuildMegaphonePng();          // flat-white megaphone for the Convene header button
        _countdownPng    = BuildCountdownPng();          // flat-white stopwatch for the Countdown header button
        _settingsGearPng = LoadSettingsGearPng();        // embedded gear icon for the header Settings button

        _prefs = _services.Config.GetSection("combatmeter");
        _metric   = (Metric)     _prefs.Get("metric", (int)Metric.Dps);
        _filter   = (FilterMode) _prefs.Get("scope",  (int)FilterMode.Party);
        _viewMode = (ViewMode)   _prefs.Get("mode",   (int)ViewMode.List);
        InitUploadPolicy();  // SP1: load/migrate the 8 upload-policy cells + cache the hot-path bools
        InitReplay();      // Replay R1: load pref + create capture instance
        InitAutoArchive(); // Auto-archive Part B: load wipe/boss/idle/stage prefs into the engine
        LoadDiscordPrefs(); // SP1 Discord webhook: load enabled/url/per-content prefs (Plugin.DiscordWebhook.cs)

        // Encounter history is persisted in its own config section (string[] of per-entry JSON). Load it before
        // the windows are built so the History window has its sessions on first show.
        _historyPrefs = _services.Config.GetSection("history");
        LoadHistory();
        LoadActiveRunMarker();   // read the persisted mid-relaunch marker (null on a clean session) — Plugin.RelaunchMarker.cs

        RegisterColours();
        BuildWindows();
        RegisterTeamContextMenuItems();

        _services.PlayerStats.Subscribe(ImagineCdReductionAttr);   // cooldown reduction (~10%) + acceleration (gear/buffs)
        _services.PlayerStats.Subscribe(ImagineCdAccelAttr);

        _services.CombatEvents.CombatEventOccurred += OnCombatEvent;
        _services.Framework.Update                 += OnUpdate;
        _services.ClientState.SceneChanged         += OnSceneChanged;
        WireSocialCapture();
        // Post-parse live build-state change (framework 2.2.0): the ONE trigger for per-setup
        // capture — gear/module/talent/imagine edits and class swaps alike. Replaces the pre-parse
        // IInventory.SelfGearChanged subscription, which raced the framework's own refresh.
        _services.Loadout.LiveStateChanged         += OnLoadoutLiveStateChanged;
        _lastSceneName = _services.ClientState.CurrentSceneName;
        _sceneIsCandidate = ResolveSceneCandidate(_lastSceneName);

        OnSkillBreakdownRequested += HandleSkillBreakdownRequested;
        OnInspectRequested += HandleInspectRequested;

        // i18n P1: draw-time Func labels re-poll each frame and switch live for free; baked-at-registration
        // window titles + registered context-menu labels + the cached chart series do not — re-register them.
        _loc.LanguageChanged += OnLanguageChanged;
    }

    private void RegisterColours()
    {
        var registry = _services.Theme.ColorRegistry;
        // Theme-editor color labels (shown in the framework Themes panel). Resolved once at registration in
        // the active language; not re-registered on language change (that could reset user color overrides).
        _roleDpsSlot    = registry.Register("CombatMeter.Role.Dps",    _loc.T("theme.color.roleDps"),    RoleClassifier.DefaultColor(Role.Dps));
        _roleTankSlot   = registry.Register("CombatMeter.Role.Tank",   _loc.T("theme.color.roleTank"),   RoleClassifier.DefaultColor(Role.Tank));
        _roleHealerSlot = registry.Register("CombatMeter.Role.Healer", _loc.T("theme.color.roleHealer"), RoleClassifier.DefaultColor(Role.Healer));
        _hpSlot = registry.Register("CombatMeter.Hp", _loc.T("theme.color.hpBar"), new ColorRgba(0.25f, 0.70f, 0.30f));
        _selfAccentSlot = registry.Register("CombatMeter.SelfAccent", _loc.T("theme.color.selfAccent"), new ColorRgba(0.12f, 0.30f, 0.33f, 0.70f));
    }

    private void BuildWindows()
    {
        _listW = _prefs.Get("listW", ListWidth);   _listH = _prefs.Get("listH", ListHeight);
        _listX = _prefs.Get("listX", 2099f);       _listY = _prefs.Get("listY", 664f);
        _partyX = _prefs.Get("partyX", 2072f);     _partyY = _prefs.Get("partyY", 333f);
        _partyW = _prefs.Get("partyW", PartyFocusW);

        var startRect = _viewMode == ViewMode.PartyFocus
            ? new WindowRect(_partyX, _partyY, _partyW > 0 ? _partyW : PartyFocusW, PartyFocusHeight())
            : new WindowRect(_listX, _listY, _listW, _listH);
        _mainWindow = _services.Windows.Register(new WindowRegistration(
            new WindowSpec(
                Id:          "combatmeter.main",
                Title:       "CombatMeter",
                DefaultRect: startRect,
                Category:    WindowCategory.HUD,
                Style:       WindowPanelStyle.Borderless)
            { Draggable = true, EditModeDragOnly = true,
              Resizable = true, MinWidth = 500f, MinHeight = 160f, MaxWidth = 760f, MaxHeight = 1000f,
              ZOrder = -100,   // background layer: every other Stellar window draws over the meter
              // In-world meter HUD: draw only in the World phase, and hide behind blocking overlays (old
              // AutoHideBehindGameMenus == Blocking) and behind any menu incl. the line-selector (AnyMenu).
              ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                && (_services.ClientState.UiState & (GameUIState.Blocking | GameUIState.AnyMenu)) == 0 },
            BuildMainRoot()));

        _historyWindow = RegisterHistoryWindow();
        _skillBreakdownWindow = RegisterSkillBreakdownWindow();
        _snapshotWindow = RegisterSnapshotWindow();

        _settingsWindow = BuildAndRegisterSettings();
        _accountWindow = BuildAndRegisterAccount();
        _archiveSettingsWindow = BuildAndRegisterArchiveSettings();
        _rowMenuWindow = RegisterRowMenuWindow();

        RegisterHotkeys();
    }

    private void RegisterHotkeys()
    {
        _toggleAction = _services.Hotkeys.DeclareAction(
            new HotkeyAction("combatmeter.toggle", "Toggle CombatMeter", new KeyBinding(StellarKeyCode.F9)),
            callback: () => _mainWindow.SetVisible(!_mainWindow.IsShown));

        _historyAction = _services.Hotkeys.DeclareAction(
            new HotkeyAction("combatmeter.history-toggle", "Toggle CombatMeter history",
                new KeyBinding(StellarKeyCode.F9, ModifierKeys.Shift)),
            callback: ToggleHistory);

        // Action hotkeys for the meter's header controls. Unbound by default (SuggestedDefault: null) so they
        // never collide with game keys out of the box — they appear in Settings → Hotkeys for the user to bind.
        _resetAction = _services.Hotkeys.DeclareAction(
            new HotkeyAction("combatmeter.reset", "Reset CombatMeter", null), callback: Clear);
        _archiveAction = _services.Hotkeys.DeclareAction(
            new HotkeyAction("combatmeter.archive", "Archive CombatMeter encounter", null), callback: ManualArchive);
        _pauseAction = _services.Hotkeys.DeclareAction(
            new HotkeyAction("combatmeter.pause", "Pause / resume CombatMeter", null), callback: TogglePause);
        _modeAction = _services.Hotkeys.DeclareAction(
            new HotkeyAction("combatmeter.mode", "Cycle CombatMeter metric (DPS/HPS/Taken)", null), callback: CycleMetric);
        _partyFocusAction = _services.Hotkeys.DeclareAction(
            new HotkeyAction("combatmeter.party-focus", "Toggle CombatMeter Party-focus view", null), callback: ToggleViewMode);
    }

    public void Dispose()
    {
        _loc.LanguageChanged -= OnLanguageChanged;   // i18n P1 live-switch handler
        CaptureModeGeometry(); PersistPrefs();   // remember the active view's size/position across reloads
        DisposeLogUpload();    // SP1: clear the event buffer
        _services.CombatEvents.CombatEventOccurred -= OnCombatEvent;
        _services.Framework.Update                 -= OnUpdate;
        _services.ClientState.SceneChanged         -= OnSceneChanged;
        UnwireSocialCapture();
        _services.Loadout.LiveStateChanged         -= OnLoadoutLiveStateChanged;
        OnSkillBreakdownRequested -= HandleSkillBreakdownRequested;
        OnInspectRequested -= HandleInspectRequested;

        _roleDpsSlot.Dispose();
        _roleTankSlot.Dispose();
        _roleHealerSlot.Dispose();
        _hpSlot.Dispose();
        _selfAccentSlot.Dispose();

        _transferLeaderReg.Dispose();
        _kickMemberReg.Dispose();
        _inviteToTeamReg.Dispose();
        _createPartyReg.Dispose();
        _leavePartyReg.Dispose();
        _partyFocusAction.Dispose();
        _modeAction.Dispose();
        _pauseAction.Dispose();
        _archiveAction.Dispose();
        _resetAction.Dispose();
        _historyAction.Dispose();
        _toggleAction.Dispose();
        _rowMenuWindow.Remove();
        _settingsWindow.Remove();
        _snapshotWindow.Remove();
        _skillBreakdownWindow.Remove();
        _historyWindow.Remove();
        _mainWindow.Remove();
    }

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

    // Rebuild the visible window's row snapshot (throttled to ~10 Hz), so the element Funcs read cached rows
    // (no per-poll service scan / formatting). Only the shown window pays.
    private void RebuildSnapshots()
    {
        if (_mainWindow.IsShown)
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

    private void Clear()
    {
        _stats.Clear();
        _timelines.Clear();
        // Buckets share _stats' PER-SEGMENT lifecycle exactly (Spec B §7): a SUPPRESSED archive never
        // calls Clear() at all (suppression wipes NOTHING), so they carry forward with _stats and the
        // sums stay equal. NOT run-scoped — never move these into ResetRunScopedTrackers.
        _bossBuckets.Clear();
        _eliteBuckets.Clear();
        _deaths.Clear();
        _imagineCasts.Clear();
        _lastImagineHitMs.Clear();
        _summonAppearMs.Clear();
        // _selfImagineBegin intentionally NOT cleared — see its declaration in Plugin.Capture.cs.
        _agg.Reset();
        // Reset the per-entity bar-animation cache too — it's keyed by EntityId and would otherwise grow
        // unbounded across a session (one entry per entity ever ranked). Clear() is the encounter-reset hook
        // (Reset button + Archive + scene change via ManualArchive), so this also caps cross-scene growth.
        _barAnim.Clear();
        // Spec cache now lives in the framework (ICombatSpec, cleared on its scene Reset) — nothing meter-local
        // to clear here anymore.
        _resTracker.Clear();
        // Drop the sticky entity snapshots with the rest of the encounter. ManualArchive() transfers the frozen
        // copies into the history entry just before calling Clear(), so this only releases the live refs.
        _entitySnaps.Clear();
        _entitySnapAccum = 0f;
        _combatActive  = false;
        _combatStartMs = 0;
        _lastDamageMs  = 0;
        // _lastRunId is deliberately NOT reset here: it is THIS run's id and must survive every mid-run
        // archive (manual/boss/idle/stage all call Clear) so each one stamps the run's OWN id. It is
        // cleared ONLY at a confirmed run boundary (RunBoundaryCore — a scene change, or the poll's commit
        // for a missed scene event), then re-latched at the next run's combat start (EnsureCombatStarted).
        // Resetting it here would zero the run mid-way and let a later archive fall back to the live CurrentRunId already advanced to the next floor (the merge).
        _difficultyAtCombatStart = 0;
        _settlementAtCombatStart = null;
        // NOTE: Clear() no longer resets the replay (delta-window decouple, owner design 2026-07-19).
        // Clear() runs at the end of every BANKED archive and on the Reset button (suppressed junk
        // archives no longer call Clear() at all — owner ruling 2026-07-19: suppression wipes NOTHING) —
        // wiping the replay here destroyed the accumulating walk-in at a suppressed archive (THE
        // walk-in-clip root cause, proven 2026-07-19). The recorder is a per-RUN capture: it resets
        // ONLY at true run end (scene-leave / run-id change) via ResetReplay, and each banked archive
        // uploads a watermark window without stopping it. See _replayWatermarkMs / ResetReplay.
        _bossCheck.Clear();   // bounded boss-lookup cache; _stageBosses survives on purpose (see its doc)
        // Per-archive: the sticky stage-boss latch is scoped to ONE segment (final review, Critical 1) —
        // reset it here, AFTER BuildHistoryEntry already read it for THIS archive, so it never bleeds
        // into the next one (mirrors the retired _segmentBossKilled's own per-archive Clear() reset).
        _segmentStageBosses = Array.Empty<(EntityId Id, int ConfigId, bool Killed)>();
        _segmentElites = Array.Empty<(EntityId Id, int ConfigId, bool Killed)>();   // mirrors the line above (ELITE CAPTURE channel, EliteSet.cs)
    }

    private double EncounterElapsedSeconds()
    {
        if (!_combatActive || _combatStartMs == 0) return 0d;
        long end = _lastDamageMs > _combatStartMs ? _lastDamageMs : _combatStartMs;
        return (end - _combatStartMs) / 1000d;
    }

    private void PersistPrefs()
    {
        _prefs.Set("metric", (int)_metric);
        _prefs.Set("scope",  (int)_filter);
        _prefs.Set("mode",   (int)_viewMode);
        _prefs.Set("listW", _listW); _prefs.Set("listH", _listH);
        _prefs.Set("listX", _listX); _prefs.Set("listY", _listY);
        _prefs.Set("partyX", _partyX); _prefs.Set("partyY", _partyY); _prefs.Set("partyW", _partyW);
        _prefs.Save();
    }

    // Capture the live window rect into the active view's remembered geometry (so a mode switch can restore it)
    // + track the current width for the List collapse breakpoints.
    private void CaptureModeGeometry()
    {
        var r = _mainWindow.Rect;
        if (r.Width <= 0f) return;
        _listWidthNow = r.Width;
        if (_viewMode == ViewMode.PartyFocus) { _partyX = r.X; _partyY = r.Y; _partyW = r.Width; }
        else { _listW = r.Width; _listH = r.Height; _listX = r.X; _listY = r.Y; }
    }

    // Move/resize the window to the active view's remembered geometry (on mode switch). Party-focus is a fixed
    // structure → fixed size; List restores the user's resized size.
    private void ApplyModeSize()
    {
        var rect = _viewMode == ViewMode.PartyFocus
            ? new WindowRect(_partyX, _partyY, _partyW > 0 ? _partyW : PartyFocusW, PartyFocusHeight())
            : new WindowRect(_listX, _listY, _listW > 0 ? _listW : ListWidth, _listH > 0 ? _listH : ListHeight);
        _mainWindow.SetRect(rect);
    }

    // Party-focus window height: base grid height (20-player 4×5 vs 5-player single group) + any open inline
    // menu (so the grid is never squeezed). Follows the live party size; RefreshPartyFocusHeight re-applies it.
    private float PartyFocusHeight()
        => (IsRaid20View ? PartyFocusH : PartyFocus5H)
           + (_mainMenuOpen ? MainMenuPanelH : 0f);

    // Re-apply the party-focus window height in place (keep current pos + width) when a menu opens/closes.
    // Uses the LIVE rect, not the remembered _partyX/_partyY, so toggling a menu never teleports a
    // window the user has dragged. No-op outside party-focus (List mode reflows via its Fill scroll).
    private void RefreshPartyFocusHeight()
    {
        if (_viewMode != ViewMode.PartyFocus || _mainWindow is null) return;
        var r = _mainWindow.Rect;
        if (r.Width <= 0f) return;
        _mainWindow.SetRect(new WindowRect(r.X, r.Y, r.Width, PartyFocusHeight()));
    }

    // ----- shared identity / colour helpers (ColorRgba — fed to MeterRowData) -----

    private ColorRgba RoleColorFor(EntityId id)
        => (RoleClassifier.Classify(ResolveProfessionId(id)) switch
        {
            Role.Tank   => _roleTankSlot,
            Role.Healer => _roleHealerSlot,
            _           => _roleDpsSlot,
        }).Value;

    // Steady HP-bar colour — independent of the fraction. The spine shows HP by length, not by hue.
    private ColorRgba HpColor() => _hpSlot.Value;

    private string GetClassLine(EntityId id)
    {
        long charId = id.Value >> 16;
        foreach (var m in _services.PartyRoster.Members)
        {
            if (m.CharId != charId) continue;
            if (m.Profession > 0)
            {
                var partyProf = _services.GameData.Combat.GetProfession(m.Profession);
                if (partyProf is { Name: { Length: > 0 } pname }) return pname;
                return $"Class {m.Profession}";
            }
            break;
        }

        if (id == _services.CombatSnapshot.LocalEntityId)
        {
            var profId = _services.PlayerState.Profession;
            if (profId > 0)
            {
                var prof = _services.GameData.Combat.GetProfession(profId);
                if (prof is { Name: { Length: > 0 } name }) return name;
            }
            var level = _services.PlayerState.Level;
            if (level > 0) return $"Lv {level}";
        }
        return string.Empty;
    }

    private bool InScope(EntityId id)
    {
        if (_filter == FilterMode.All) return true;
        if (id == _services.CombatSnapshot.LocalEntityId) return true;
        if (_filter == FilterMode.Self) return false;
        long charId = id.Value >> 16;
        foreach (var m in _services.PartyRoster.Members)
            if (m.CharId == charId) return true;
        return false;
    }

    internal static string FormatAmount(long v)
    {
        if (v < 0) v = 0;
        if (v >= 1_000_000) return $"{v / 1_000_000f:F1}M";
        if (v >= 1_000)     return $"{v / 1_000f:F1}K";
        return v.ToString();
    }

    private enum FilterMode { Self, Party, All }
    private enum ViewMode   { List, PartyFocus }
}
