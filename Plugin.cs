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
    private const float MainMenuPanelH   = 72f;   // Scope row + separator + Pause/Archive/History row + spacer
    private const float MetricMenuPanelH = 96f;   // DPS/HPS/Taken stacked (3 rows) + spacer

    private readonly IPluginServices _services;

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

    // EntityId -> per-second time-series (dealt/healing/taken). Frozen into history at archive.
    internal const int TimelineBucketMs = 1000;
    internal const int TimelineMaxBuckets = 600;
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

    // Combat-timer state (unix ms).
    private long _combatStartMs;
    private long _lastDamageMs;
    private bool _combatActive;

    // Persisted UI state.
    private Metric     _metric = Metric.Dps;
    private FilterMode _filter = FilterMode.Party;
    private ViewMode   _viewMode = ViewMode.List;
    private bool       _lastViewWas20;   // tracks party-size view (5↔20) to re-fit the window on a live switch
    private bool       _paused;

    // Per-mode window geometry (the framework persists one rect per window id, so the plugin remembers each
    // view's size+position separately and restores it on mode switch — like the IMGUI build's list/party rects).
    private float _listW, _listH, _listX, _listY, _partyX, _partyY;
    // Current window width (captured each tick) — drives the List spec/secondary/share collapse breakpoints.
    private float _listWidthNow = ListWidth;

    public Plugin(IPluginServices services)
    {
        _services = services;
        _services.Log.Info("[CombatMeter] plugin constructed");

        _inspectIconPng = BuildInspectMagnifierPng();   // procedural magnifier for the history Inspect button (main thread)

        _prefs = _services.Config.GetSection("combatmeter");
        _metric   = (Metric)     _prefs.Get("metric", (int)Metric.Dps);
        _filter   = (FilterMode) _prefs.Get("scope",  (int)FilterMode.Party);
        _viewMode = (ViewMode)   _prefs.Get("mode",   (int)ViewMode.List);

        // Encounter history is persisted in its own config section (string[] of per-entry JSON). Load it before
        // the windows are built so the History window has its sessions on first show.
        _historyPrefs = _services.Config.GetSection("history");
        LoadHistory();

        RegisterColours();
        BuildWindows();

        _services.PlayerStats.Subscribe(ImagineCdReductionAttr);   // cooldown reduction (~10%) + acceleration (gear/buffs)
        _services.PlayerStats.Subscribe(ImagineCdAccelAttr);

        _services.CombatEvents.CombatEventOccurred += OnCombatEvent;
        _services.Framework.Update                 += OnUpdate;
        _services.ClientState.SceneChanged         += OnSceneChanged;
        _lastSceneName = _services.ClientState.CurrentSceneName;

        OnSkillBreakdownRequested += HandleSkillBreakdownRequested;
        OnInspectRequested += HandleInspectRequested;
    }

    private void RegisterColours()
    {
        var registry = _services.Theme.ColorRegistry;
        _roleDpsSlot    = registry.Register("CombatMeter.Role.Dps",    "Role: DPS",    RoleClassifier.DefaultColor(Role.Dps));
        _roleTankSlot   = registry.Register("CombatMeter.Role.Tank",   "Role: Tank",   RoleClassifier.DefaultColor(Role.Tank));
        _roleHealerSlot = registry.Register("CombatMeter.Role.Healer", "Role: Healer", RoleClassifier.DefaultColor(Role.Healer));
        _hpSlot = registry.Register("CombatMeter.Hp", "HP bar", new ColorRgba(0.25f, 0.70f, 0.30f));
        _selfAccentSlot = registry.Register("CombatMeter.SelfAccent", "Self-row highlight", new ColorRgba(0.12f, 0.30f, 0.33f, 0.70f));
    }

    private void BuildWindows()
    {
        _listW = _prefs.Get("listW", ListWidth);   _listH = _prefs.Get("listH", ListHeight);
        _listX = _prefs.Get("listX", 2099f);       _listY = _prefs.Get("listY", 664f);
        _partyX = _prefs.Get("partyX", 2072f);     _partyY = _prefs.Get("partyY", 333f);

        var startRect = _viewMode == ViewMode.PartyFocus
            ? new WindowRect(_partyX, _partyY, PartyFocusW, PartyFocusHeight())
            : new WindowRect(_listX, _listY, _listW, _listH);
        _mainWindow = _services.Windows.Register(new WindowRegistration(
            new WindowSpec(
                Id:          "combatmeter.main",
                Title:       "CombatMeter",
                DefaultRect: startRect,
                Category:    WindowCategory.HUD,
                Style:       WindowPanelStyle.Borderless)
            { AutoHideBehindGameMenus = true, HideUntilInWorld = true, Draggable = true, EditModeDragOnly = true,
              Resizable = true, MinWidth = 240f, MinHeight = 160f, MaxWidth = 760f, MaxHeight = 1000f },
            BuildMainRoot()));

        _historyWindow = RegisterHistoryWindow();
        _skillBreakdownWindow = RegisterSkillBreakdownWindow();
        _snapshotWindow = RegisterSnapshotWindow();

        _settingsWindow = BuildAndRegisterSettings();
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
        CaptureModeGeometry(); PersistPrefs();   // remember the active view's size/position across reloads
        _services.CombatEvents.CombatEventOccurred -= OnCombatEvent;
        _services.Framework.Update                 -= OnUpdate;
        _services.ClientState.SceneChanged         -= OnSceneChanged;
        OnSkillBreakdownRequested -= HandleSkillBreakdownRequested;
        OnInspectRequested -= HandleInspectRequested;

        _roleDpsSlot.Dispose();
        _roleTankSlot.Dispose();
        _roleHealerSlot.Dispose();
        _hpSlot.Dispose();
        _selfAccentSlot.Dispose();

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
        PumpClassIcons();
        _snapshotAccum += deltaTime;
        if (_snapshotAccum < SnapshotIntervalS) return;
        _snapshotAccum = 0f;
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
        _agg.Reset();
        // Reset the per-entity bar-animation cache too — it's keyed by EntityId and would otherwise grow
        // unbounded across a session (one entry per entity ever ranked). Clear() is the encounter-reset hook
        // (Reset button + Archive + scene change via ManualArchive), so this also caps cross-scene growth.
        _barAnim.Clear();
        // Spec cache now lives in the framework (ICombatSpec, cleared on its scene Reset) — nothing meter-local
        // to clear here anymore.
        _resTracker.Clear();
        _combatActive  = false;
        _combatStartMs = 0;
        _lastDamageMs  = 0;
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
        _prefs.Set("partyX", _partyX); _prefs.Set("partyY", _partyY);
        _prefs.Save();
    }

    // Capture the live window rect into the active view's remembered geometry (so a mode switch can restore it)
    // + track the current width for the List collapse breakpoints.
    private void CaptureModeGeometry()
    {
        var r = _mainWindow.Rect;
        if (r.Width <= 0f) return;
        _listWidthNow = r.Width;
        if (_viewMode == ViewMode.PartyFocus) { _partyX = r.X; _partyY = r.Y; }
        else { _listW = r.Width; _listH = r.Height; _listX = r.X; _listY = r.Y; }
    }

    // Move/resize the window to the active view's remembered geometry (on mode switch). Party-focus is a fixed
    // structure → fixed size; List restores the user's resized size.
    private void ApplyModeSize()
    {
        var rect = _viewMode == ViewMode.PartyFocus
            ? new WindowRect(_partyX, _partyY, PartyFocusW, PartyFocusHeight())
            : new WindowRect(_listX, _listY, _listW > 0 ? _listW : ListWidth, _listH > 0 ? _listH : ListHeight);
        _mainWindow.SetRect(rect);
    }

    // Party-focus window height: base grid height (20-player 4×5 vs 5-player single group) + any open inline
    // menu (so the grid is never squeezed). Follows the live party size; RefreshPartyFocusHeight re-applies it.
    private float PartyFocusHeight()
        => (IsRaid20View ? PartyFocusH : PartyFocus5H)
           + (_mainMenuOpen ? MainMenuPanelH : _metricMenuOpen ? MetricMenuPanelH : 0f);

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

    private static string FormatAmount(long v)
    {
        if (v < 0) v = 0;
        if (v >= 1_000_000) return $"{v / 1_000_000f:F1}M";
        if (v >= 1_000)     return $"{v / 1_000f:F1}K";
        return v.ToString();
    }

    private enum FilterMode { Self, Party, All }
    private enum ViewMode   { List, PartyFocus }
}
