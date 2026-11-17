using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.CombatMeter.LogUpload;

namespace Stellar.CombatMeter;

/// <summary>Tabs of the merged "CombatMeter" settings window.</summary>
internal enum SettingsTab { Look = 0, Effects = 1, Archive = 2, Uploads = 3, Account = 4 }

// The ONE settings window (approved redesign 2026-11-17, D2-A, _scratch\prototypes\combatmeter-redesign-APPROVED.md §3):
// the old Appearance (combatmeter.settings), Settings (combatmeter.archiveSettings) and Configure buffs & debuffs
// (combatmeter.debuffConfig) windows merged into one GlassMenu "CombatMeter" window, 720 wide, FIXED height on every tab:
//   ToolHeader (meter icon tile · "CombatMeter" · live status) / tab strip Look · Effects · Archive · Uploads · Account /
//   the tab body (Plugin.Settings{Look,Effects,Archive,Uploads}.cs, Plugin.Account.cs).
// A NEW window id ("combatmeter.config"): the old ids carry persisted 380-wide rects that would override the new size.
//
// Fixed height without content-fit (the framework Settings hub recipe, SettingsHub.cs): Row[1 px strut of BodyH, main
// column]; the five tab bodies are KeepAlive + Prebuild Conditionals alone in their own Column (Fill), so every tab is
// built in the background and parked when hidden — switching tabs never rebuilds or resizes anything — and
// WindowSpec.FixedHeight pins the window to DefaultRect.Height.
//
// Entry points (old method names kept, so the meter header is unchanged): ToggleAppearance → Look,
// ToggleArchiveSettings (meter gear) and the launcher tile → Archive, ToggleDebuffConfig ("Configure buffs & debuffs…") →
// Effects. The HUD's ⋯ › Settings card should call OpenSettings(tab, _mainWindow).
public sealed partial class Plugin
{
    private const float SettingsWindowW = 720f;
    private const float SettingsWindowH = 728f;
    // 728 − 32 title bar − 2 × 12 GlassMenu body padding.
    private const float SettingsBodyH = SettingsWindowH - 32f - 24f;
    // 720 − 24 padding − the 1 px height strut.
    private const float SettingsMainW = SettingsWindowW - 24f - 1f;
    private const float SettingsGap = 8f;
    // Two balanced columns (rule 17): FIXED widths — weighted cells only split equally when both prefer the same width.
    private const float SettingsColW = (SettingsMainW - SettingsGap) / 2f - 0.5f;
    // Width inside a column's panel (padding 10 each side) — used to lay out chip groups at build time.
    private const float SettingsColInnerW = SettingsColW - 2f * PanelPadX;
    private const float SettingsFullInnerW = SettingsMainW - 2f * PanelPadX;

    // Kept under its old name so the meter header (Appearance button: Active => _settingsWindow.IsShown) compiles as is.
    private IWindowControl _settingsWindow = null!;
    private WindowCenterer? _settingsCenterer;
    private SettingsTab _settingsTabSel = SettingsTab.Look;

    internal bool IsSettingsShown => _settingsWindow.IsShown;

    private IWindowControl BuildAndRegisterSettings()
    {
        var w = _services.Windows.Register(new WindowRegistration(
            new WindowSpec(
                Id:          "combatmeter.config",
                Title:       "CombatMeter",   // the plugin brand, identical in every catalog (launcher.settings.title)
                DefaultRect: new WindowRect(900f, 160f, SettingsWindowW, SettingsWindowH),
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            { StartVisible = false, Closable = true, Draggable = true, FixedHeight = true,
              TitleProvider = () => _loc.T("launcher.settings.title"),
              // Five KeepAlive tabs ≈ a few hundred parts: build them in the background after login instead of in the
              // click that first opens the window (Plugin-Performance-Rules §5). Value lambdas are hidden-safe.
              Prebuild = !CmDiag.NoSettingsPrebuild,   // crash-bisect switch (Plugin.Diag.cs)
              ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                && (_services.ClientState.UiState & GameUIState.Loading) == 0 },
            BuildSettingsWindowRoot(),
            OnClose: CloseSettings));
        _settingsCenterer?.Stop();   // a language-change rebuild replaces the window; drop the old one's centerer
        _settingsCenterer = new WindowCenterer(_services.Framework, w);
        return w;
    }

    /// <summary>Opens the settings window on <paramref name="tab"/>, centred on <paramref name="opener"/> (null = the
    /// screen centre) and in front. Already open → just switches tab and fronts it (no re-centre under the cursor).</summary>
    internal void OpenSettings(SettingsTab tab, IWindowControl? opener = null)
    {
        SelectSettingsTab(tab);
        if (_settingsWindow.IsShown) { _settingsWindow.BringToFront(); return; }
        _settingsCenterer?.OpenCentred(opener);
    }

    /// <summary>Toggle semantics for the old buttons: open on <paramref name="tab"/>; a second click on the SAME tab
    /// closes the window; a click for another tab switches to it.</summary>
    internal void ToggleSettings(SettingsTab tab, IWindowControl? opener = null)
    {
        if (_settingsWindow.IsShown && _settingsTabSel == tab) { CloseSettings(); return; }
        OpenSettings(tab, opener);
    }

    // Old entry points (meter menu Appearance / gear, launcher tile, Configure buffs & debuffs…).
    private void ToggleAppearance()
    {
        if (!_settingsWindow.IsShown || _settingsTabSel != SettingsTab.Look)
            _lookView = _viewMode == ViewMode.List ? 0 : IsRaid20View ? 2 : 1;   // edit the view the meter shows
        ToggleSettings(SettingsTab.Look, _mainWindow);
    }

    private void ToggleArchiveSettings() => ToggleSettings(SettingsTab.Archive, _mainWindow);
    private void ToggleDebuffConfig() => ToggleSettings(SettingsTab.Effects, _mainWindow);

    private void CloseSettings()
    {
        _settingsCenterer?.Stop();
        CloseHelp();
        FlushMeterBackgroundSave();
        _settingsWindow.SetVisible(false);
    }

    private void SelectSettingsTab(SettingsTab tab)
    {
        if (tab == SettingsTab.Effects) OnEffectsTabShown();   // fill the effect list only when it is actually wanted
        if (_settingsTabSel == tab) return;
        _settingsTabSel = tab;
        CloseHelp();   // a tip belongs to the tab it was opened from
    }

    // ── layout ────────────────────────────────────────────────────────────────────────────────────────────────────
    private HudElement BuildSettingsWindowRoot()
    {
        var tabs = new[] { SettingsTab.Look, SettingsTab.Effects, SettingsTab.Archive, SettingsTab.Uploads, SettingsTab.Account };
        var bodies = new List<HudElement>(tabs.Length);
        foreach (var t in tabs)
        {
            var tab = t;
            bodies.Add(new ConditionalElement(() => _settingsTabSel == tab, BuildSettingsTabBody(tab), Fill: true)
                { Prebuild = !CmDiag.NoSettingsPrebuild, KeepAlive = true, PrebuildName = "combatmeter.config." + tab.ToString().ToLowerInvariant() });
        }
        // The whole content sits in the window's ConfirmHostElement: an open confirm (Plugin.Confirm.cs, e.g. Effects
        // "Untick all") scrims all of it. The host lays its content out exactly as without it (fixed height kept).
        return new ConfirmHostElement(new RowElement(new HudElement[]
        {
            new SpacerElement(1f, SettingsBodyH),
            new CellElement(new ColumnElement(new HudElement[]
            {
                ToolHeader(MeterIconTile(), () => _loc.T("launcher.settings.title"), SettingsStatusLine()),
                TabStrip(new Func<string>[]
                {
                    () => _loc.T("cfg.tab.look"), () => _loc.T("cfg.tab.effects"), () => _loc.T("cfg.tab.archive"),
                    () => _loc.T("cfg.tab.uploads"), () => _loc.T("cfg.tab.account"),
                }, () => (int)_settingsTabSel, i => SelectSettingsTab((SettingsTab)i)),
                new ColumnElement(bodies, Gap: 0.01f),
            }, Gap: SettingsGap), Weight: 1f),
        }, Gap: 0.01f), SettingsConfirm) { ConfirmLabelWidth = SettingsConfirmLabelW };
    }

    private HudElement BuildSettingsTabBody(SettingsTab tab) => tab switch
    {
        SettingsTab.Look    => BuildLookTab(),
        SettingsTab.Effects => BuildEffectsTab(),
        SettingsTab.Archive => BuildArchiveTab(),
        SettingsTab.Uploads => BuildUploadsTab(),
        _                   => BuildAccountTab(),
    };

    // Two balanced columns, top-aligned: each column ends in a vertical filler (flexible height), so the Row stretches
    // both to the taller one and the panels sit at the top instead of being centred (the framework hub's VerticalFill).
    private static HudElement TwoColumns(HudElement[] left, HudElement[] right) => new RowElement(new HudElement[]
    {
        new CellElement(SettingsColumn(left),  Width: SettingsColW),
        new CellElement(SettingsColumn(right), Width: SettingsColW),
    }, Gap: SettingsGap);

    private static HudElement SettingsColumn(HudElement[] panels)
    {
        var items = new List<HudElement>(panels.Length + 1);
        items.AddRange(panels);
        items.Add(VerticalFill());
        return new ColumnElement(items, Gap: SettingsGap);
    }

    // A 1 px filler that takes the slack height (Conditional Fill = flexibleHeight), framework StellarStyle.VerticalFill.
    private static HudElement VerticalFill() => new ConditionalElement(() => true, new SpacerElement(1f, 1f), Fill: true);

    // A tab body: its rows top-aligned, then the filler (the KeepAlive slot stretches the body to the slot height).
    private static HudElement SettingsTabColumn(params HudElement[] rows) => SettingsColumn(rows);

    // ── header status ─────────────────────────────────────────────────────────────────────────────────────────────
    // "● Recording · Party-focus (5) · auto-archive on · uploads auto" — ONE builder (the HUD's ⋯ card can reuse it).
    // Re-composed only when one of its inputs or the language changes (an int key), never per refresh.
    private int _statusKey = -1;
    private string? _statusLang;
    private string _statusText = "";

    private HudElement SettingsStatusLine() => new RowElement(new HudElement[]
    {
        StatusDot(() => _paused ? CautionColor() : SuccessColor()),
        new CellElement(new TextElement(SettingsStatusText, Color: () => MutedColor(), NoWrap: true) { Ellipsize = true }, Weight: 1f),
    }, Gap: 6f);

    internal string SettingsStatusText()
    {
        int view = _viewMode == ViewMode.List ? 0 : IsRaid20View ? 2 : 1;
        int uploads = UploadsSummary();
        int key = (_paused ? 1 : 0) | view << 1 | (AutoArchiveEnabled ? 1 : 0) << 3 | uploads << 4;
        string lang = _loc.Language;
        if (key == _statusKey && ReferenceEquals(lang, _statusLang)) return _statusText;
        _statusKey = key; _statusLang = lang;
        _statusText = string.Join(" · ",
            _loc.T(_paused ? "cfg.status.paused" : "cfg.status.recording"),
            _loc.T(view == 0 ? "cfg.status.list" : view == 1 ? "cfg.status.party5" : "cfg.status.party20"),
            _loc.T(AutoArchiveEnabled ? "cfg.status.autoArchiveOn" : "cfg.status.autoArchiveOff"),
            _loc.T(uploads switch { 0 => "cfg.status.uploadsAuto", 1 => "cfg.status.uploadsManual",
                                    2 => "cfg.status.uploadsOff", _ => "cfg.status.uploadsMixed" }));
        return _statusText;
    }

    // 0 = every content's run stats auto, 1 = all manual, 2 = all off, 3 = mixed. Five array reads, no allocation.
    private int UploadsSummary()
    {
        var first = UploadPolicyFor(UploadPolicyTable.Kinds[0], UploadArtifact.Stats);
        foreach (var k in UploadPolicyTable.Kinds)
            if (UploadPolicyFor(k, UploadArtifact.Stats) != first) return 3;
        return first switch { UploadPolicyState.Auto => 0, UploadPolicyState.Manual => 1, _ => 2 };
    }
}
