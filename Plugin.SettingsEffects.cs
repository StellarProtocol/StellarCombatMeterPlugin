using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

// Settings › Effects (the old "Configure buffs & debuffs" window, approved redesign §3). PARTY EFFECTS (Show buffs &
// debuffs, Max shown — per view, the view picked on the Look tab's EDITING strip), then the pick-list panel: kind chips
// Debuffs (red) / Buffs (green) + "shown / total" hint; mode chips "All except ticked" / "Only ticked"; Unnamed on/off;
// search + round ✕; a 10-row VirtualList of 26 px tiles (16 px tick box, 20 px icon, name, muted ×N); footer feedback +
// Caution "Untick all" with an inline confirm. The list is the CooldownBar picker (Plugin.Settings.Rows.cs /
// .Feedback.cs) recipe: a click flashes the row green with "Hidden" / "Shown" for ~1 s (keyed by kind + the group's
// representative id + time, so scrolling / re-filtering keeps it on the right row) and the footer says
// "✓ Hidden “X” · N hidden" for ~3 s. Rows never re-sort under the cursor.
// Model (catalog, filter, kind / mode / show-hidden switches): Plugin.DebuffConfig.cs.
// Per-refresh cost: every Func is O(1) — a slot's ticked state, name and ×N text are cached per slot and recomputed only
// when the slot shows another row or _dcSelVer moved; the ticked count is recomputed only when _dcSelVer moved.
public sealed partial class Plugin
{
    private const int   EffectsListRows   = 10;
    private const float EffectsRightColW  = 80f;   // "hidden" / bold flash word / ×N (id "tersembunyi" ≈ 78 plain)
    private const float EffectsCheckBox   = 16f;
    private const float EffectsIconSize   = 20f;
    private const float EffectsFlashDurS  = 1.0f;
    private const float EffectsNoteDurS   = 3.0f;
    private const float EffectsConfirmDurS = 4.0f;
    private const float FlashWashAlpha    = 0.26f;
    private static readonly ColorRgba DebuffKindColor = Rgb(0xff5959);   // the meter's debuff tile ring
    private static readonly ColorRgba BuffKindColor   = Rgb(0x59ff80);   // the meter's buff tile ring

    private bool DcExcludeMode => ActiveSel.Mode == DebuffTrackMode.ShowAllExceptSelected;

    // Called when the Effects tab is shown: the catalog (a ~10k-row table walk) is built and filtered only on a real
    // open — never in the hidden background prebuild (Plugin-Performance-Rules §7: lists backed by loaded data report 0
    // rows while hidden).
    private void OnEffectsTabShown()
    {
        EnsureEffectCatalog();
        ApplyDebuffCfgFilter(_dcFilter);
    }

    private HudElement BuildEffectsTab() => SettingsTabColumn(EffectsPartyPanel(), EffectsListPanel());

    // ── PARTY EFFECTS ─────────────────────────────────────────────────────────────────────────────────────────────
    private HudElement EffectsPartyPanel() => SectionPanel(() => _loc.T("cfg.effects.party"), EffectsViewHint,
        SettingRow(() => _loc.T("settings.toggle.debuffs"), false,
            ToggleChip(() => LookT.Debuffs, v => { LookT.Debuffs = v; PersistToggles(); }),
            HelpDot("effects.show", "cfg.effects.party", "settings.toggle.debuffs", "help.effects.show")),
        // Max cells per row: 4 (2×2) up to 12 (2×6) — the block grows in columns, keeping 2 rows (owner 2026-09-24).
        SettingRow(() => _loc.T("settings.debuff.max"), false,
            DebuffMaxChip(4), DebuffMaxChip(6), DebuffMaxChip(8), DebuffMaxChip(10), DebuffMaxChip(12),
            HelpDot("effects.max", "cfg.effects.party", "settings.debuff.max", "help.effects.max")));

    private HudElement DebuffMaxChip(int n)
    {
        string label = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return ChoiceChip(() => label, () => MeterElementToggles.DebuffMaxCells(LookT.DebuffMax) == n,
            () => { LookT.DebuffMax = n; PersistToggles(); }, () => LookT.Debuffs);
    }

    // "for the Party (5) view" — these two settings are per view, like the Look tab; cached per view + language.
    private int _effViewHintView = -1;
    private string? _effViewHintLang;
    private string _effViewHint = "";

    private string EffectsViewHint()
    {
        string lang = _loc.Language;
        if (_lookView != _effViewHintView || !ReferenceEquals(lang, _effViewHintLang))
        {
            _effViewHintView = _lookView; _effViewHintLang = lang;
            _effViewHint = _loc.TFormat("cfg.effects.forView", _loc.T(_lookView == 0 ? "header.mode.list"
                : _lookView == 1 ? "settings.mode.party5" : "settings.mode.party20"));
        }
        return _effViewHint;
    }

    // ── pick-list panel ───────────────────────────────────────────────────────────────────────────────────────────
    private readonly FmtCache _effCountFmt = new();

    private HudElement EffectsListPanel()
    {
        var pool = new HudElement[DebuffConfigPoolSize];
        for (int i = 0; i < pool.Length; i++) pool[i] = ListSlot(EffectRow(i));
        return PanelBox(new HudElement[]
        {
            new RowElement(new HudElement[]
            {
                new SpacerElement(0f, CaptionHeight),
                ChoiceChip(() => _loc.T("debuffcfg.tab.debuffs"), () => _dcTab == 0, () => SetEffectsKind(0), color: () => DebuffKindColor),
                ChoiceChip(() => _loc.T("debuffcfg.tab.buffs"),   () => _dcTab == 1, () => SetEffectsKind(1), color: () => BuffKindColor),
                new SpacerElement(0f),
                new TextElement(() => _effCountFmt.Get("{0} / {1}", _dcFiltCount, _dcTab == 1 ? _dcBuffTotal : _dcDebuffTotal),
                    Color: () => MutedColor(), NoWrap: true),
            }, Gap: 6f),
            new RowElement(new HudElement[]
            {
                new SpacerElement(0f, SettingRowH),
                ChoiceChip(() => _loc.T("cfg.effects.modeAll"),  () => DcExcludeMode,  () => SetEffectsMode(DebuffTrackMode.ShowAllExceptSelected)),
                ChoiceChip(() => _loc.T("cfg.effects.modeOnly"), () => !DcExcludeMode, () => SetEffectsMode(DebuffTrackMode.ShowOnlySelected)),
                new SpacerElement(0f),
                HelpDot("effects.mode", "cfg.effects.list", "cfg.effects.mode", "help.effects.mode"),
            }, Gap: 6f),
            SettingRow(() => _loc.T("cfg.effects.unnamed"), false,
                ToggleChip(() => ActiveSel.ShowHidden, SetDebuffShowHidden),
                HelpDot("effects.unnamed", "cfg.effects.list", "cfg.effects.unnamed", "help.effects.unnamed")),
            new RowElement(new HudElement[]
            {
                new SpacerElement(0f, SettingRowH),
                new InputElement(Get: () => _dcFilter, Submit: ApplyDebuffCfgFilter,
                    Width: SettingsFullInnerW - IconButtonSize - 2f * 6f, OnChange: ApplyDebuffCfgFilter)
                    { SingleLine = true, Placeholder = EffectsSearchPlaceholder },
                IconButton(() => "✕", () => ApplyDebuffCfgFilter("")),
            }, Gap: 6f),
            new VirtualListElement(
                Count:     () => _dcFiltCount,
                RowHeight: ListRowPitch, Pool: pool,
                OnWindow:  i => _dcOffset = i, Height: ListViewportHeight(EffectsListRows))
            { ResetScroll = () => { if (!_dcScrollReset) return false; _dcScrollReset = false; return true; } },
            EffectsFooter(),
        });
    }

    private string EffectsSearchPlaceholder() => _loc.T(_dcTab == 1 ? "cfg.effects.searchBuffs" : "cfg.effects.searchDebuffs");

    private void SetEffectsKind(int tab) { _dcConfirmAt = -1.0; SetDebuffConfigTab(tab); }
    private void SetEffectsMode(DebuffTrackMode mode) { _dcConfirmAt = -1.0; SetDebuffMode(mode); }

    // ── one row ───────────────────────────────────────────────────────────────────────────────────────────────────
    private HudElement EffectRow(int slot) => new BubbleElement(new SelectableElement(
        new RowElement(new HudElement[]
        {
            new SpacerElement(2f, ListTileHeight),   // height strut + (with the 6 px gap) the 8 px left inset
            EffectTickBox(slot),
            // Fixed cell + centring Row: a bare picture in a Cell is stretched sideways (the icon-squish gotcha).
            new CellElement(new RowElement(new HudElement[]
                {
                    new GameTextureElement(() => DcIcon(slot), (int)EffectsIconSize, (int)EffectsIconSize, () => _dcUv[slot]),
                }, Gap: 0.01f, Justify: RowJustify.Center), Width: EffectsIconSize),
            // Name: normal, or (a row the strip hides) muted + struck through — two complete texts, one fixed slot.
            new CellElement(new ConditionalElement(() => DcSlotHidden(slot),
                new TextElement(() => DcSlotName(slot), Color: () => MutedColor(), NoWrap: true) { Ellipsize = true, Strikethrough = true },
                new TextElement(() => DcSlotName(slot), Color: () => TextColor(), NoWrap: true) { Ellipsize = true })
                { Prebuild = true }, Weight: 1f),
            // Right column: muted ×N / "hidden", or for ~1 s after a click the bold green "Hidden" / "Shown".
            new CellElement(new ConditionalElement(() => DcSlotFlashing(slot),
                new TextElement(DcFlashLabel, Color: () => DcFlashTextColor(), Align: TextAlign.Right, NoWrap: true, FontSize: 13) { Bold = true },
                new TextElement(() => DcSlotRight(slot), Color: () => MutedColor(), Align: TextAlign.Right, NoWrap: true) { Ellipsize = true })
                { Prebuild = true }, Width: EffectsRightColW),
            new SpacerElement(2f, 1f),               // right inset
        }, Gap: 6f),
        OnClick: () => ToggleEffectSlot(slot))
        { Bare = true, OnHover = over => SetDcSlotHover(slot, over) })
    {
        FillColorFn = () => Fade(EffectTileFill(slot)), CornerRadius = TileRadius, PaddingX = 0f, PaddingY = 0f,
    };

    private ColorRgba EffectTileFill(int slot)
    {
        var fill = DcSlotFlashing(slot) ? Over(SuccessColor() with { A = FlashWashAlpha }, TileColor()) : TileColor();
        return DcSlotHovered(slot) ? HoverFill(fill) : fill;
    }

    // 16 px rounded tick box: ticked = solid accent + "✓" ("Only ticked") or solid red + "✕" ("All except ticked": the
    // row is hidden); unticked = an empty Neutral box. One element (live glyph + live fill) — ticking rebuilds nothing.
    // Text.Height pins the plain 14 px glyph's line box to 16, so the box stays exactly 16 × 16.
    private HudElement EffectTickBox(int slot) => new CellElement(new BubbleElement(new RowElement(new HudElement[]
        {
            new SpacerElement(0f, EffectsCheckBox),
            new TextElement(() => !DcSlotTicked(slot) ? string.Empty : DcExcludeMode ? "✕" : "✓",
                () => EnsureContrast(White, DcExcludeMode ? DangerColor() : AccentColor(), SolidMinContrast),
                Width: EffectsCheckBox, Align: TextAlign.Center, NoWrap: true) { Height = EffectsCheckBox },
        }, Gap: 0.01f, Justify: RowJustify.Center))
    {
        FillColorFn = () => Fade(!DcSlotTicked(slot) ? NeutralFill() : DcExcludeMode ? DangerColor() : AccentColor()),
        CornerRadius = 4f, PaddingX = 0f, PaddingY = 0f, MaxWidth = EffectsCheckBox,
    }, Width: EffectsCheckBox);

    // ── per-slot cache ────────────────────────────────────────────────────────────────────────────────────────────
    private readonly int[]    _dcSlotRow    = new int[DebuffConfigPoolSize];
    private readonly int[]    _dcSlotVer    = NewFilled(DebuffConfigPoolSize, -1);
    private readonly bool[]   _dcSlotTicked = new bool[DebuffConfigPoolSize];
    private readonly string[] _dcSlotName   = NewFilled(DebuffConfigPoolSize, "");
    private readonly string[] _dcSlotVar    = NewFilled(DebuffConfigPoolSize, "");

    private static T[] NewFilled<T>(int n, T v) { var a = new T[n]; Array.Fill(a, v); return a; }

    // Returns the filtered row index for `slot` (or -1), refreshing the slot's cached state on a row / version change.
    private int DcSlotRow(int slot)
    {
        int i = _dcOffset + slot;
        if (i < 0 || i >= _dcFiltCount) return -1;
        if (_dcSlotRow[slot] != i || _dcSlotVer[slot] != _dcSelVer)
        {
            _dcSlotRow[slot] = i; _dcSlotVer[slot] = _dcSelVer;
            var g = _dcGroups[i];
            bool all = true;
            foreach (var id in g.Ids) if (!ActiveSel.IsSelected(id)) { all = false; break; }
            _dcSlotTicked[slot] = all;
            _dcSlotName[slot] = string.IsNullOrEmpty(g.Name) ? "#" + g.RepId : g.Name;
            _dcSlotVar[slot]  = g.Count > 1 ? "×" + g.Count : "";   // "Cuisine ×136" → name + muted ×136
        }
        return i;
    }

    private bool   DcSlotTicked(int slot) => DcSlotRow(slot) >= 0 && _dcSlotTicked[slot];
    private bool   DcSlotHidden(int slot) => DcExcludeMode && DcSlotTicked(slot);   // the strip hides this effect
    private string DcSlotName(int slot)   => DcSlotRow(slot) >= 0 ? _dcSlotName[slot] : "";
    private string DcSlotRight(int slot)  => DcSlotRow(slot) < 0 ? "" : DcSlotHidden(slot) ? _loc.T("cfg.effects.hidden") : _dcSlotVar[slot];

    private object? DcIcon(int slot)
    {
        int i = _dcOffset + slot;
        if (i >= _dcFiltCount) { _dcUv[slot] = default; return null; }
        return _services.GameAssets.LoadBuffIcon(_dcGroups[i].RepId, out _dcUv[slot]);
    }

    // Per pool-slot hover keyed by the slot's LOGICAL row: a re-bound slot never inherits the previous row's hover.
    private readonly bool[] _dcSlotHover    = new bool[DebuffConfigPoolSize];
    private readonly int[]  _dcSlotHoverRow = new int[DebuffConfigPoolSize];

    private void SetDcSlotHover(int slot, bool over) { _dcSlotHover[slot] = over; _dcSlotHoverRow[slot] = _dcOffset + slot; }

    private bool DcSlotHovered(int slot)
    {
        if (_dcSlotHoverRow[slot] != _dcOffset + slot) { _dcSlotHoverRow[slot] = _dcOffset + slot; _dcSlotHover[slot] = false; }
        return _dcSlotHover[slot];
    }

    // ── click + feedback ──────────────────────────────────────────────────────────────────────────────────────────
    private int    _dcFlashTab = -1, _dcFlashId;
    private double _dcFlashAt = double.NegativeInfinity;
    private bool   _dcFlashHidden;                         // the click hid the effect (else showed it)
    private string _dcNote = "";                           // footer note, composed once per click
    private double _dcNoteAt = double.NegativeInfinity;
    private double _dcConfirmAt = -1.0;                    // ≥ 0 while the inline "Untick all" confirm is showing

    private void ToggleEffectSlot(int slot)
    {
        int i = DcSlotRow(slot);
        if (i < 0) return;
        bool on = !_dcSlotTicked[slot];
        string name = _dcSlotName[slot];
        int repId = _dcGroups[i].RepId;
        SetDcTracked(slot, on);   // Plugin.DebuffConfig.cs — saves + bumps _dcSelVer
        double now = _services.Framework.TimeNow;
        _dcFlashTab = _dcTab; _dcFlashId = repId; _dcFlashAt = now;
        _dcFlashHidden = DcExcludeMode == on;   // "All except": ticking hides; "Only": ticking shows
        _dcNote = _loc.TFormat(_dcFlashHidden ? "cfg.effects.didHide" : "cfg.effects.didShow", name) + " · " + DcCountText();
        _dcNoteAt = now;
        _dcConfirmAt = -1.0;      // any other action dismisses a pending confirm
    }

    private bool DcSlotFlashing(int slot)
    {
        if (_dcFlashTab != _dcTab || _services.Framework.TimeNow - _dcFlashAt >= EffectsFlashDurS) return false;
        int i = _dcOffset + slot;
        return i >= 0 && i < _dcFiltCount && _dcGroups[i].RepId == _dcFlashId;
    }

    private string DcFlashLabel() => _loc.T(_dcFlashHidden ? "cfg.effects.flash.hidden" : "cfg.effects.flash.shown");

    private ColorRgba DcFlashTextColor()
        => EnsureContrast(SuccessColor(), Over(SuccessColor() with { A = FlashWashAlpha }, TileColor()), SolidMinContrast);

    // ── ticked count (fully ticked name GROUPS of the active kind, as the list shows them) ───────────────────────
    private int _dcCountVer = -1, _dcTicked;
    private readonly FmtCache _dcCountFmt = new();

    private int DcTickedCount()
    {
        if (_dcCountVer == _dcSelVer) return _dcTicked;
        _dcCountVer = _dcSelVer;
        _dcTicked = 0;
        if (_effectCatalog.Count == 0) return 0;   // never builds the catalog itself (only an open of the tab does)
        FilterSeenEffects(_effectCatalog, "", ActiveSel.ShowHidden, wantBuff: _dcTab == 1, out var all, out var n);
        for (int g = 0; g < n; g++)
        {
            bool full = true;
            foreach (var id in all[g].Ids) if (!ActiveSel.IsSelected(id)) { full = false; break; }
            if (full) _dcTicked++;
        }
        return _dcTicked;
    }

    private string DcCountText()
        => _dcCountFmt.Get(_loc.T(DcExcludeMode ? "cfg.effects.countHidden" : "cfg.effects.countShown"), DcTickedCount());

    private bool DcNoteShowing() => _services.Framework.TimeNow - _dcNoteAt < EffectsNoteDurS;
    private bool DcConfirming() => _dcConfirmAt >= 0.0 && _services.Framework.TimeNow - _dcConfirmAt < EffectsConfirmDurS;

    // ── footer: feedback + Untick all (inline confirm, same 28 px height so nothing moves) ───────────────────────
    private const float UntickLabelW = 96f;   // longest "Untick all" in any locale (id "Hapus centang") + headroom
    private const float CancelLabelW = 72f;   // fil "Kanselahin" ≈ 69
    private readonly FmtCache _dcConfirmFmt = new();

    private HudElement EffectsFooter() => new ConditionalElement(DcConfirming,
        new RowElement(new HudElement[]
        {
            new SpacerElement(0f, PillHeight),
            new CellElement(new TextElement(() => _dcConfirmFmt.Get(_loc.T(_dcTab == 1 ? "cfg.effects.confirm.buffs"
                    : "cfg.effects.confirm.debuffs"), DcTickedCount()),
                Color: () => TextColor(), NoWrap: true, FontSize: 13) { Bold = true, Ellipsize = true }, Weight: 1f),
            CautionButton(() => _loc.T("cfg.effects.untickAll"), ConfirmUntickAll, UntickLabelW),
            NeutralButton(() => _loc.T("cfg.cancel"), () => _dcConfirmAt = -1.0, CancelLabelW),
        }, Gap: 8f),
        new RowElement(new HudElement[]
        {
            new SpacerElement(0f, PillHeight),
            new CellElement(new TextElement(() => DcNoteShowing() ? _dcNote : DcCountText(),
                Color: () => DcNoteShowing() ? SuccessText() : MutedColor(), NoWrap: true) { Ellipsize = true }, Weight: 1f),
            CautionButton(() => _loc.T("cfg.effects.untickAll"), () => _dcConfirmAt = _services.Framework.TimeNow,
                UntickLabelW, enabled: () => DcTickedCount() > 0),
        }, Gap: 8f))
    { Prebuild = true };

    // Clears the ACTIVE kind's whole selection (every id, so a partially ticked group is cleared too) and persists it.
    private void ConfirmUntickAll()
    {
        _dcConfirmAt = -1.0;
        ActiveSel.Clear();
        ActiveSel.Save(_prefs, ActiveSelPrefix);
        ApplyDebuffCfgFilter(_dcFilter);   // bumps _dcSelVer → slot caches + count recompute
        _dcFlashTab = -1;
        _dcNote = _loc.T(_dcTab == 1 ? "cfg.effects.cleared.buffs" : "cfg.effects.cleared.debuffs");
        _dcNoteAt = _services.Framework.TimeNow;
    }
}
