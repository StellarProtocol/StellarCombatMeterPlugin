using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

/// <summary>
/// Main meter window root + the header bar and its dropdown menus (migrated off the IMGUI popovers). The header
/// is a Row: <c>Meter</c> title · Reset · {metric} ▾ · mode pill · ≡. The metric + main menus are inline
/// dropdown Columns toggled via <see cref="ConditionalElement"/> (the retained-mode analog of the old floating
/// popovers — they push the rows down while open). Metric/scope/mode changes persist via PersistPrefs().
/// </summary>
public sealed partial class Plugin
{
    private bool _metricMenuOpen;
    private bool _mainMenuOpen;

    private static readonly string[] MetricDrop = { "DPS ▾", "HPS ▾", "Taken ▾" };
    private static readonly string[] ModePill   = { "List", "Party-focus" };

    private ColorRgba? MutedCol() => new ColorRgba(0.66f, 0.70f, 0.73f, 1f);

    private HudElement BuildMainRoot() => new ColumnElement(new HudElement[]
    {
        BuildHeaderBar(),
        new ConditionalElement(() => _metricMenuOpen, BuildMetricMenu()),
        new ConditionalElement(() => _mainMenuOpen, BuildMainMenu()),
        // A little vertical margin (no divider line) between an open menu panel and the meter/group list below.
        new ConditionalElement(() => _metricMenuOpen || _mainMenuOpen, new SpacerElement(Height: 6f)),
        new ConditionalElement(() => _viewMode == ViewMode.List, BuildListBody(), Fill: true),
        new ConditionalElement(() => _viewMode == ViewMode.PartyFocus, BuildPartyFocusBody()),
    }, Gap: 4f);

    private HudElement BuildHeaderBar() => new RowElement(new HudElement[]
    {
        new TextElement(() => "Meter", Emphasis: true),
        new ButtonElement(() => "Reset", Clear),
        new SpacerElement(),
        new ButtonElement(() => MetricDrop[(int)_metric], ToggleMetricMenu, Active: () => _metricMenuOpen),
        new ButtonElement(() => ModePill[(int)_viewMode], ToggleViewMode),
        // Expand/collapse caret for the inline menu below (▾ expand · ▴ collapse) — not a hamburger, since it
        // now toggles the inline Scope/Pause/Archive/History panel rather than opening a floating popover.
        new ButtonElement(() => _mainMenuOpen ? "▴" : "▾", ToggleMainMenu, Active: () => _mainMenuOpen, Width: 30f),
    }, Gap: 6f);

    private HudElement BuildMetricMenu() => new ColumnElement(new HudElement[]
    {
        MetricItem("DPS", Metric.Dps),
        MetricItem("HPS", Metric.Hps),
        MetricItem("Taken", Metric.Taken),
    }, Gap: 2f);

    private HudElement MetricItem(string label, Metric m)
        => new ButtonElement(() => label, () => SelectMetric(m), Active: () => _metric == m);

    private HudElement BuildMainMenu() => new ColumnElement(new HudElement[]
    {
        // List mode shows the per-player Scope filter; party-focus replaces it with the 5/20 party-size
        // control (only while a party exists). One of the two is shown at a time, so the separator below
        // only renders when one of them is present (avoids a dangling line in party-focus + no party).
        new ConditionalElement(() => _viewMode == ViewMode.List, BuildScopeRow()),
        new ConditionalElement(() => ShowPartySizeControl, BuildPartySizeRow()),
        new ConditionalElement(() => _viewMode == ViewMode.List || ShowPartySizeControl, new SeparatorElement()),
        new RowElement(new HudElement[]
        {
            new ButtonElement(() => _paused ? "Resume" : "Pause", TogglePause),
            new ButtonElement(() => "Archive", ManualArchiveFromMenu),
            new ButtonElement(() => $"History ({_history.Count})", ToggleHistory, Active: () => _historyWindow.IsShown),
            new ButtonElement(() => "Appearance", ToggleAppearance, Active: () => _settingsWindow.IsShown),
        }, Gap: 4f),
    }, Gap: 4f);

    private HudElement BuildScopeRow() => new RowElement(new HudElement[]
    {
        new TextElement(() => "Scope:", MutedCol),
        ScopeItem("Self", FilterMode.Self),
        ScopeItem("Party", FilterMode.Party),
        ScopeItem("All", FilterMode.All),
    }, Gap: 4f);

    // 5/20 party-size control. The active pill reflects the live party size (the grid auto-follows server
    // broadcasts). Stage 1: selecting a size is a no-op placeholder; Stage 2 wires RequestPartySize to invoke
    // the game's own ChangeTeamMemberType (policy-clean, never crafts a packet).
    private HudElement BuildPartySizeRow() => new RowElement(new HudElement[]
    {
        new TextElement(() => "Party:", MutedCol),
        PartySizeItem("5",  PartyType.Regular5),
        PartySizeItem("20", PartyType.Raid20),
    }, Gap: 4f);

    private HudElement PartySizeItem(string label, PartyType size)
        => new ButtonElement(() => label, () => RequestPartySize(size),
                             Active: () => _services.PartySnapshot.PartyType == size);

    private HudElement ScopeItem(string label, FilterMode f)
        => new ButtonElement(() => label, () => SelectScope(f), Active: () => _filter == f);

    // ----- menu actions -----

    private void ToggleMetricMenu() { _metricMenuOpen = !_metricMenuOpen; if (_metricMenuOpen) _mainMenuOpen = false; RefreshPartyFocusHeight(); }
    private void ToggleMainMenu()   { _mainMenuOpen = !_mainMenuOpen;     if (_mainMenuOpen) _metricMenuOpen = false; RefreshPartyFocusHeight(); }

    // Selecting an item does NOT collapse the panel — only the caret/metric button toggles it (clicking inside
    // an open dropdown shouldn't snap it shut).
    private void SelectMetric(Metric m) { _metric = m; PersistPrefs(); }

    // Advance to the next metric (DPS → HPS → Taken → DPS). Wired to the combatmeter.mode hotkey.
    private void CycleMetric() => SelectMetric((Metric)(((int)_metric + 1) % MetricDrop.Length));
    private void SelectScope(FilterMode f) { _filter = f; PersistPrefs(); }

    // Invoke the game's OWN ChangeTeamMemberType through IPartyControl (Lua bridge) — never a hand-built
    // packet; the game validates (leader + 20-player unlock) and may reject. The grid follows the resulting
    // server broadcast. No-op when the requested size is already active.
    private void RequestPartySize(PartyType size)
    {
        if (_services.PartySnapshot.PartyType == size) return;
        _services.PartyControl.SetMemberType(size);
    }
    private void ToggleViewMode()
    {
        CaptureModeGeometry();   // remember the view we're leaving
        _viewMode = _viewMode == ViewMode.List ? ViewMode.PartyFocus : ViewMode.List;
        PersistPrefs();
        ApplyModeSize();         // restore the view we're entering
    }
    private void TogglePause() { _paused = !_paused; }
    private void ManualArchiveFromMenu() { ManualArchive(); }

    private void ToggleHistory()
    {
        if (_historyWindow.IsShown) CloseHistory();
        else _historyWindow.SetVisible(true);
    }

    private void CloseHistory()
    {
        _historyWindow.SetVisible(false);
        _historyIndex = -1;
        CloseSkillBreakdown();
    }
}
