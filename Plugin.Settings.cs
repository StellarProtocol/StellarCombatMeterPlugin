using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

// Settings › Look (the old Appearance window, approved redesign §3): per-view element toggles for the meter row.
// EDITING strip List / Party (5) / Party (20) picks which view's MeterElementToggles instance (ListToggles /
// Party5Toggles / Party20Toggles, Plugin.List.cs) the rows below edit — each view keeps its own look. Every row reads
// the CURRENT instance through LookT live, so one set of rows serves all three views (no rebuild on a switch).
// Changes persist to the "combatmeter" config section; BuildRowData reads them on the next refresh tick.
// Left column ROW + BATTLE IMAGINE, right column BARS + NUMBERS + METER WINDOW; every row ends in a "?".
// ⚠️ Adding/removing a row here → update the row counts in Plugin.SettingsWindow.cs (LookLeftRows / LookRightRows): the
// fixed window height is computed from them, and a column taller than that pushes the footer out of the window.
public sealed partial class Plugin
{
    private int _lookView;   // 0 = List, 1 = Party (5), 2 = Party (20) — the EDITING strip

    private MeterElementToggles LookT => _lookView == 0 ? ListToggles : _lookView == 1 ? Party5Toggles : Party20Toggles;

    private HudElement BuildLookTab() => SettingsTabColumn(
        LookEditingStrip(),
        TwoColumns(
            new[] { LookRowPanel(), LookImaginePanel() },
            new[] { LookBarsPanel(), LookNumbersPanel(), LookMeterWindowPanel() }),
        new RowElement(new HudElement[]
        {
            new SpacerElement(0f, PillHeight),
            new CellElement(new TextElement(() => _loc.T("cfg.look.autoStyled"), Color: () => MutedColor(), NoWrap: true)
                { Ellipsize = true }, Weight: 1f),
            NeutralButton(() => _loc.T("cfg.close"), CloseSettings, 56f),
        }, Gap: 8f));

    // EDITING  [List] [Party (5)] [Party (20)] ……… Each view keeps its own look · now showing Party (5)  (?)
    private HudElement LookEditingStrip() => new BubbleElement(new RowElement(new HudElement[]
    {
        new SpacerElement(0f, SettingRowH),
        new TextElement(() => _loc.T("cfg.look.editing"), Color: () => TextColor(), NoWrap: true, FontSize: 12) { Bold = true },
        new SpacerElement(2f, 1f),
        ChoiceChip(() => _loc.T("header.mode.list"),       () => _lookView == 0, () => _lookView = 0),
        ChoiceChip(() => _loc.T("settings.mode.party5"),   () => _lookView == 1, () => _lookView = 1),
        ChoiceChip(() => _loc.T("settings.mode.party20"),  () => _lookView == 2, () => _lookView = 2),
        new CellElement(new TextElement(LookShowingText, Color: () => MutedColor(), NoWrap: true, Align: TextAlign.Right)
            { Ellipsize = true }, Weight: 1f),
        HelpDot("look.editing", "cfg.look.editing", "cfg.look.editing", "help.look.editing"),
    }, Gap: 6f))
    {
        FillColorFn = () => Fade(PanelColor()), CornerRadius = 6f, PaddingX = PanelPadX, PaddingY = 6f,
    };

    // "Each view keeps its own look · now showing Party (5)" — the view the METER shows right now. Cached per view +
    // language (string building only on a change).
    private int _showingView = -1;
    private string? _showingLang;
    private string _showingText = "";

    private string LookShowingText()
    {
        int view = _viewMode == ViewMode.List ? 0 : IsRaid20View ? 2 : 1;
        string lang = _loc.Language;
        if (view != _showingView || !ReferenceEquals(lang, _showingLang))
        {
            _showingView = view; _showingLang = lang;
            _showingText = _loc.TFormat("cfg.look.showing", _loc.T(view == 0 ? "header.mode.list"
                : view == 1 ? "settings.mode.party5" : "settings.mode.party20"));
        }
        return _showingText;
    }

    // ── ROW ───────────────────────────────────────────────────────────────────────────────────────────────────────
    // Row size (approved 2026-11-17) leads the panel. Rows the chosen size doesn't draw go RECESSED (rule 22: disabled,
    // not clickable, values kept) with a muted inline hint — see SizeRow / Plugin.Settings.RowSize.cs.
    private HudElement LookRowPanel() => SectionPanel(() => _loc.T("cfg.look.row"), () => _loc.T("cfg.look.row.hint"),
        LookRowSizeRow(),
        LookToggleRow("rank", "settings.toggle.rank", () => LookT.Rank, v => LookT.Rank = v),
        LookToggleRow("crest", "settings.toggle.crest", () => LookT.Crest, v => LookT.Crest = v, DrawnNotMini),
        // "Class/Spec" merges the two old toggles (Spec name, Class name) — no config migration: Off = neither,
        // Spec = spec only, Class = class only, Both = both, so every saved combination lights exactly one chip.
        // Four chips + label + "?" crowd the 323 px column (id/fil/th at the SDL 7.5/12.5/8 px budget), so the group
        // uses a 6 px chip pad instead of 9; the chips still hug their (per-language, rebuild-stable) labels.
        SizeRow(() => _loc.T("cfg.look.nameLine"), false, DrawnNormalOnly,
            ChoiceChip(() => _loc.T("cfg.look.nameLine.off"),   () => !LookT.Spec && !LookT.ClassName, () => SetNameLine(false, false), IsNormalSize, padX: NameLineChipPad),
            ChoiceChip(() => _loc.T("cfg.look.nameLine.spec"),  () => LookT.Spec && !LookT.ClassName,  () => SetNameLine(true, false),  IsNormalSize, padX: NameLineChipPad),
            ChoiceChip(() => _loc.T("cfg.look.nameLine.class"), () => !LookT.Spec && LookT.ClassName,  () => SetNameLine(false, true),  IsNormalSize, padX: NameLineChipPad),
            ChoiceChip(() => _loc.T("cfg.look.nameLine.both"),  () => LookT.Spec && LookT.ClassName,   () => SetNameLine(true, true),   IsNormalSize, padX: NameLineChipPad),
            LookHelp("nameLine", "cfg.look.nameLine")),
        LookToggleRow("abilityScore", "settings.toggle.abilityScore", () => LookT.AbilityScore, v => LookT.AbilityScore = v, DrawnNormalOnly),
        // Indented under Ability score as in the prototype, but NOT recessed when it is off: the row shows Illusion-Break
        // on its own too (Plugin.List.cs ShowAbilityScore = AbilityScore || IllusionBreak), so gating it would change
        // behaviour for anyone using it alone. Recessed only by Row size (the score isn't drawn outside Normal).
        SizeRow(() => _loc.T("cfg.look.illusionBreak"), true, DrawnNormalOnly,
            ToggleChip(() => LookT.IllusionBreak, v => { LookT.IllusionBreak = v; PersistToggles(); }, IsNormalSize),
            LookHelp("illusionBreak", "cfg.look.illusionBreak")),
        LookToggleRow("leaderFlag", "settings.toggle.leaderFlag", () => LookT.LeaderFlag, v => LookT.LeaderFlag = v, DrawnNotMini),
        LookToggleRow("voiceIcon", "settings.toggle.voiceIcon", () => LookT.VoiceIcon, v => LookT.VoiceIcon = v, DrawnNormalOnly),
        // Party grids only (List rows are single-height): shown on the List view too, RECESSED, so switching the
        // EDITING view never changes the panel's height (rule 14). Normal only: it sizes the 2-row effect block and with it
        // the row height; Compact / Mini rows have a fixed height and Compact's effect tiles are a fixed 16 px.
        SizeRow(() => _loc.T("settings.tile.size"), false, DrawnNormalOnly,
            LookChoice("common.small",  () => LookT.TileSize == TileSize.Small,  () => LookT.TileSize = TileSize.Small,  TileSizeEnabled),
            LookChoice("common.medium", () => LookT.TileSize == TileSize.Medium, () => LookT.TileSize = TileSize.Medium, TileSizeEnabled),
            LookChoice("common.large",  () => LookT.TileSize == TileSize.Large,  () => LookT.TileSize = TileSize.Large,  TileSizeEnabled),
            LookHelp("tileSize", "settings.tile.size")));

    private bool TileSizeEnabled() => _lookView != 0 && IsNormalSize();

    private const float NameLineChipPad = 6f;

    private void SetNameLine(bool spec, bool cls) { LookT.Spec = spec; LookT.ClassName = cls; PersistToggles(); }

    // ── BATTLE IMAGINE ────────────────────────────────────────────────────────────────────────────────────────────
    // Imagines are drawn by Normal rows only → the whole section recesses for Compact / Mini.
    private HudElement LookImaginePanel() => SectionPanel(() => _loc.T("settings.section.battleImagine"), null,
        SizeRow(() => _loc.T("settings.imagine.show"), false, DrawnNormalOnly,
            ToggleChip(() => LookT.Imagine, v => { LookT.Imagine = v; PersistToggles(); }, IsNormalSize),
            ImagineHelp("show", "settings.imagine.show")),
        SizeRow(() => _loc.T("settings.imagine.cooldown"), false, DrawnNormalOnly,
            ToggleChip(() => LookT.ImagineCooldown, v => { LookT.ImagineCooldown = v; PersistToggles(); }, () => LookT.Imagine && IsNormalSize()),
            ImagineHelp("cooldown", "settings.imagine.cooldown")),
        SizeRow(() => _loc.T("settings.imagine.size"), false, DrawnNormalOnly,
            LookChoice("common.small", () => LookT.ImagineSize == ImagineSize.Small, () => LookT.ImagineSize = ImagineSize.Small, IsNormalSize),
            LookChoice("common.large", () => LookT.ImagineSize == ImagineSize.Large, () => LookT.ImagineSize = ImagineSize.Large, IsNormalSize),
            ImagineHelp("size", "settings.imagine.size")),
        SizeRow(() => _loc.T("settings.imagine.position"), false, DrawnNormalOnly,
            LookChoice("settings.pos.topRight", () => LookT.ImaginePosition == ImaginePosition.TopRight,    () => LookT.ImaginePosition = ImaginePosition.TopRight, IsNormalSize),
            LookChoice("settings.pos.right",    () => LookT.ImaginePosition == ImaginePosition.RightColumn, () => LookT.ImaginePosition = ImaginePosition.RightColumn, IsNormalSize),
            LookChoice("settings.pos.left",     () => LookT.ImaginePosition == ImaginePosition.Left,        () => LookT.ImaginePosition = ImaginePosition.Left, IsNormalSize),
            ImagineHelp("position", "settings.imagine.position")));

    // ── BARS ──────────────────────────────────────────────────────────────────────────────────────────────────────
    private HudElement LookBarsPanel() => SectionPanel(() => _loc.T("settings.section.bars"), null,
        SettingRow(() => _loc.T("settings.bar.mainBar"), false,
            BarsChoice("list.metric.dps", () => !LookT.MainBarIsHp, () => LookT.MainBarIsHp = false),
            BarsChoice("common.hp",       () => LookT.MainBarIsHp,  () => LookT.MainBarIsHp = true),
            BarsHelp("mainBar", "settings.bar.mainBar")),
        // Which hue the player gauges take: the theme's role slots (default) or the game's class crest colour.
        SettingRow(() => _loc.T("settings.bar.barColor"), false,
            BarsChoice("settings.barColor.role",  () => LookT.BarColor == BarColorMode.Role,  () => LookT.BarColor = BarColorMode.Role),
            BarsChoice("settings.barColor.class", () => LookT.BarColor == BarColorMode.Class, () => LookT.BarColor = BarColorMode.Class),
            BarsHelp("barColor", "settings.bar.barColor")),
        // How the values drawn ON a bar stay readable over the fill (MeterRowData.LabelStyle, framework 2.8.0).
        SettingRow(() => _loc.T("settings.bar.labelStyle"), false,
            BarsChoice("settings.labelStyle.plain",   () => LookT.BarLabelStyle == MeterLabelStyle.Plain,   () => LookT.BarLabelStyle = MeterLabelStyle.Plain),
            BarsChoice("settings.labelStyle.outline", () => LookT.BarLabelStyle == MeterLabelStyle.Outline, () => LookT.BarLabelStyle = MeterLabelStyle.Outline),
            BarsChoice("settings.labelStyle.shadow",  () => LookT.BarLabelStyle == MeterLabelStyle.Shadow,  () => LookT.BarLabelStyle = MeterLabelStyle.Shadow),
            BarsHelp("labelStyle", "settings.bar.labelStyle")),
        // The soft light sweeping the main bar (framework sheen, MeterRowData.HideSheen). Per view like the rest of
        // Look; default on. Off also skips the per-frame sweep + its clip for every row of that view.
        SizeRow(() => _loc.T("settings.bar.shine"), false, DrawnNotMini,
            ToggleChip(() => LookT.BarShine, v => { LookT.BarShine = v; PersistToggles(); }, IsNotMini),
            BarsHelp("shine", "settings.bar.shine")),
        SizeRow(() => _loc.T("settings.bar.spineBar"), false, DrawnNotMini,
            LookChoice("common.off",      () => LookT.VerticalBar == VerticalBarMode.Off, () => LookT.VerticalBar = VerticalBarMode.Off, IsNotMini),
            LookChoice("list.metric.dps", () => LookT.VerticalBar == VerticalBarMode.Dps, () => LookT.VerticalBar = VerticalBarMode.Dps, IsNotMini),
            LookChoice("common.hp",       () => LookT.VerticalBar == VerticalBarMode.Hp,  () => LookT.VerticalBar = VerticalBarMode.Hp, IsNotMini),
            BarsHelp("spineBar", "settings.bar.spineBar")),
        SizeRow(() => _loc.T("settings.bar.spineWidth"), false, DrawnNormalOnly,
            LookChoice("settings.width.thin",   () => LookT.SpineWidth <= 3f,            () => LookT.SpineWidth = 3f, IsNormalSize),
            LookChoice("settings.width.normal", () => LookT.SpineWidth is > 3f and <= 5f, () => LookT.SpineWidth = 5f, IsNormalSize),
            LookChoice("settings.width.wide",   () => LookT.SpineWidth > 5f,             () => LookT.SpineWidth = 8f, IsNormalSize),
            BarsHelp("spineWidth", "settings.bar.spineWidth")));

    // ── NUMBERS ───────────────────────────────────────────────────────────────────────────────────────────────────
    private HudElement LookNumbersPanel() => SectionPanel(() => _loc.T("cfg.look.numbers"), null,
        NumbersToggleRow("perSecond", "cfg.look.perSecond", () => LookT.Primary, v => LookT.Primary = v),
        NumbersToggleRow("total",     "settings.toggle.total", () => LookT.Total, v => LookT.Total = v),
        NumbersToggleRow("share",     "settings.toggle.sharePct", () => LookT.Share, v => LookT.Share = v, DrawnNormalOnly));

    // ── METER WINDOW ──────────────────────────────────────────────────────────────────────────────────────────────
    // NEW (D3): "Background" 0–100 %, default 0 — a rounded theme panel behind the meter (drawn by the meter HUD, which
    // reads MeterBackgroundOpacity). ONE value for the meter window, not per view. "Colours → Edit colours…" is not
    // here: the role / HP / self colours are framework theme slots (IColorRegistry) and the SDK has no way for a plugin
    // to open the theme editor, so the row was dropped as the approved spec allows.
    private readonly FmtCache _bgPctFmt = new();
    private readonly FmtCache _fgPctFmt = new();
    private readonly FmtCache _rowBgPctFmt = new();

    private HudElement LookMeterWindowPanel() => SectionPanel(() => _loc.T("cfg.look.meterWindow"), null,
        SettingRow(() => _loc.T("cfg.look.background"), false,
            new CellElement(RoundSlider(() => MathF.Round(MeterBackgroundOpacity * 100f), v => SetMeterBackground(v / 100f), 0f, 100f)
                with { Width = 150f }, Width: 150f),
            new TextElement(() => _bgPctFmt.Get("{0}%", (long)MathF.Round(MeterBackgroundOpacity * 100f)),
                Color: () => TextColor(), Width: 38f, Align: TextAlign.Right, NoWrap: true, FontSize: 13) { Bold = true },
            HelpDot("look.background", "cfg.look.meterWindow", "cfg.look.background", "help.look.background")),
        // "Row background" 0–100 %, default 60 %: fades only each row's dark backing (row tile, bar + spine tracks, empty
        // effect squares, the recessed empty slot) through the framework's MeterRowElement.BackingOpacity; bars, crest,
        // names, numbers, icons, filled effect tiles and the self wash/ring stay solid. 100 % = the look before 2.33.
        SettingRow(() => _loc.T("cfg.look.rowBackground"), false,
            new CellElement(RoundSlider(() => MathF.Round(MeterRowBackingOpacity * 100f), v => SetMeterRowBacking(v / 100f), 0f, 100f)
                with { Width = 150f }, Width: 150f),
            new TextElement(() => _rowBgPctFmt.Get("{0}%", (long)MathF.Round(MeterRowBackingOpacity * 100f)),
                Color: () => TextColor(), Width: 38f, Align: TextAlign.Right, NoWrap: true, FontSize: 13) { Bold = true },
            HelpDot("look.rowBackground", "cfg.look.meterWindow", "cfg.look.rowBackground", "help.look.rowBackground")),
        // "Foreground" 20–100 %, default 100 %: fades the whole meter content (header, icons, bars, names, numbers,
        // effect tiles) through the framework's WindowSpec.ContentOpacity. The Background panel sits outside that faded
        // group, so neither slider changes what the other one shows.
        SettingRow(() => _loc.T("cfg.look.foreground"), false,
            new CellElement(RoundSlider(() => MathF.Round(MeterForegroundOpacity * 100f), v => SetMeterForeground(v / 100f), 20f, 100f)
                with { Width = 150f }, Width: 150f),
            new TextElement(() => _fgPctFmt.Get("{0}%", (long)MathF.Round(MeterForegroundOpacity * 100f)),
                Color: () => TextColor(), Width: 38f, Align: TextAlign.Right, NoWrap: true, FontSize: 13) { Bold = true },
            HelpDot("look.foreground", "cfg.look.meterWindow", "cfg.look.foreground", "help.look.foreground")));

    // ── row helpers ───────────────────────────────────────────────────────────────────────────────────────────────
    private HudElement LookToggleRow(string id, string labelKey, Func<bool> get, Action<bool> set, SizeNeed need = SizeNeed.Any)
        => SizeRow(() => _loc.T(labelKey), false, need, ToggleChip(get, v => { set(v); PersistToggles(); }, NeedFn(need)),
            LookHelp(id, labelKey));

    private HudElement NumbersToggleRow(string id, string labelKey, Func<bool> get, Action<bool> set, SizeNeed need = SizeNeed.Any)
        => SizeRow(() => _loc.T(labelKey), false, need, ToggleChip(get, v => { set(v); PersistToggles(); }, NeedFn(need)),
            HelpDot("look." + id, "cfg.look.numbers", labelKey, "help.look." + id));

    private HudElement LookChoice(string labelKey, Func<bool> active, Action set, Func<bool>? enabled = null)
        => ChoiceChip(() => _loc.T(labelKey), active, () => { set(); PersistToggles(); }, enabled);

    private HudElement BarsChoice(string labelKey, Func<bool> active, Action set) => LookChoice(labelKey, active, set);

    private HudElement LookHelp(string id, string titleKey) => HelpDot("look." + id, "cfg.look.row", titleKey, "help.look." + id);
    private HudElement ImagineHelp(string id, string titleKey)
        => HelpDot("imagine." + id, "settings.section.battleImagine", titleKey, "help.imagine." + id);
    private HudElement BarsHelp(string id, string titleKey) => HelpDot("bars." + id, "settings.section.bars", titleKey, "help.bars." + id);

    private void PersistToggles()
    {
        ListToggles.Save(_prefs, "list");
        Party5Toggles.Save(_prefs, "party5");
        Party20Toggles.Save(_prefs, "party20");
    }

    // ── meter background (D3) ─────────────────────────────────────────────────────────────────────────────────────
    private const string PrefMeterBackground = "meter.background";
    private float? _meterBackground;
    private double _meterBgSavedAt = double.NegativeInfinity;
    private bool _meterBgDirty;

    /// <summary>Opacity (0..1) of the rounded theme panel the meter HUD draws behind itself. Default 0 = today's look.
    /// Config key <c>meter.background</c> (0..1) in the "combatmeter" section.</summary>
    internal float MeterBackgroundOpacity => _meterBackground ??= Math.Clamp(_prefs.Get(PrefMeterBackground, 0f), 0f, 1f);

    // The slider fires on every drag step: set the pref live, but write the file at most twice a second, and once more
    // when the window closes (FlushMeterBackgroundSave) — never a disk write per frame while dragging.
    private void SetMeterBackground(float v)
    {
        v = Math.Clamp(MathF.Round(v * 100f) / 100f, 0f, 1f);
        if (_meterBackground == v) return;
        _meterBackground = v;
        _prefs.Set(PrefMeterBackground, v);
        MarkMeterLookDirty();
    }

    // ── meter foreground ──────────────────────────────────────────────────────────────────────────────────────────
    private const string PrefMeterForeground = "meter.foreground";
    private float? _meterForeground;
    private Func<float>? _meterForegroundFn;

    /// <summary>Opacity (0.2..1) of the whole meter content, applied by the framework as WindowSpec.ContentOpacity (the
    /// Background panel is not faded by it). Default 1 = today's look; the 20 % floor stops the meter vanishing by
    /// accident. Config key <c>meter.foreground</c> (0.2..1) in the "combatmeter" section.</summary>
    internal float MeterForegroundOpacity => _meterForeground ??= Math.Clamp(_prefs.Get(PrefMeterForeground, 1f), 0.2f, 1f);

    // The WindowSpec.ContentOpacity delegate, built once: the framework polls it at the window refresh rate.
    private Func<float> MeterForegroundFn => _meterForegroundFn ??= () => MeterForegroundOpacity;

    private void SetMeterForeground(float v)
    {
        v = Math.Clamp(MathF.Round(v * 100f) / 100f, 0.2f, 1f);
        if (_meterForeground == v) return;
        _meterForeground = v;
        _prefs.Set(PrefMeterForeground, v);
        MarkMeterLookDirty();
    }

    // ── meter row background ──────────────────────────────────────────────────────────────────────────────────────
    private const string PrefMeterRowBackground = "meter.rowBackground";
    private const float DefaultMeterRowBackground = 0.6f;   // rows read "too solid" at 100 % (user 2026-11-17)
    private float? _meterRowBacking;
    private Func<float>? _meterRowBackingFn;

    /// <summary>Opacity (0..1) of each meter row's dark backing, applied by the framework as
    /// MeterRowElement.BackingOpacity (Rounded rows). Default 0.6; 1 = the look before the setting existed. Config key
    /// <c>meter.rowBackground</c> (0..1) in the "combatmeter" section. ONE value for every view.</summary>
    internal float MeterRowBackingOpacity
        => _meterRowBacking ??= Math.Clamp(_prefs.Get(PrefMeterRowBackground, DefaultMeterRowBackground), 0f, 1f);

    // The MeterRowElement.BackingOpacity delegate, built once and shared by every row (List + party grids); the
    // framework polls it with each row's refresh and repaints only on a change.
    private Func<float> MeterRowBackingFn => _meterRowBackingFn ??= () => MeterRowBackingOpacity;

    private void SetMeterRowBacking(float v)
    {
        v = Math.Clamp(MathF.Round(v * 100f) / 100f, 0f, 1f);
        if (_meterRowBacking == v) return;
        _meterRowBacking = v;
        _prefs.Set(PrefMeterRowBackground, v);
        MarkMeterLookDirty();
    }

    // Shared by the Background, Row background and Foreground sliders. A drag fires every frame, so the prefs file is
    // written at most twice a second, plus once when the Settings window closes (FlushMeterBackgroundSave) — never a
    // disk write per frame.
    private void MarkMeterLookDirty()
    {
        _meterBgDirty = true;
        double now = _services.Framework.TimeNow;
        if (now - _meterBgSavedAt >= 0.5) FlushMeterBackgroundSave();
    }

    private void FlushMeterBackgroundSave()
    {
        if (!_meterBgDirty) return;
        _meterBgDirty = false;
        _meterBgSavedAt = _services.Framework.TimeNow;
        _prefs.Save();
    }
}
