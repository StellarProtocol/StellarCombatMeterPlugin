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
    private HudElement LookRowPanel() => SectionPanel(() => _loc.T("cfg.look.row"), () => _loc.T("cfg.look.row.hint"),
        LookToggleRow("rank", "settings.toggle.rank", () => LookT.Rank, v => LookT.Rank = v),
        LookToggleRow("crest", "settings.toggle.crest", () => LookT.Crest, v => LookT.Crest = v),
        // "Name line" merges the two old toggles (Spec name, Class name) — no config migration: Spec = spec only,
        // Class = class only, Both = both. A config with both off (possible before) simply shows no chip lit.
        SettingRow(() => _loc.T("cfg.look.nameLine"), false,
            ChoiceChip(() => _loc.T("cfg.look.nameLine.spec"),  () => LookT.Spec && !LookT.ClassName, () => SetNameLine(true, false)),
            ChoiceChip(() => _loc.T("cfg.look.nameLine.class"), () => !LookT.Spec && LookT.ClassName, () => SetNameLine(false, true)),
            ChoiceChip(() => _loc.T("cfg.look.nameLine.both"),  () => LookT.Spec && LookT.ClassName,  () => SetNameLine(true, true)),
            LookHelp("nameLine", "cfg.look.nameLine")),
        LookToggleRow("abilityScore", "settings.toggle.abilityScore", () => LookT.AbilityScore, v => LookT.AbilityScore = v),
        // Indented under Ability score as in the prototype, but NOT recessed when it is off: the row shows Illusion-Break
        // on its own too (Plugin.List.cs ShowAbilityScore = AbilityScore || IllusionBreak), so gating it would change
        // behaviour for anyone using it alone.
        SettingRow(() => _loc.T("cfg.look.illusionBreak"), true,
            ToggleChip(() => LookT.IllusionBreak, v => { LookT.IllusionBreak = v; PersistToggles(); }),
            LookHelp("illusionBreak", "cfg.look.illusionBreak")),
        LookToggleRow("leaderFlag", "settings.toggle.leaderFlag", () => LookT.LeaderFlag, v => LookT.LeaderFlag = v),
        LookToggleRow("voiceIcon", "settings.toggle.voiceIcon", () => LookT.VoiceIcon, v => LookT.VoiceIcon = v),
        // Party grids only (List rows are single-height): shown on the List view too, RECESSED, so switching the
        // EDITING view never changes the panel's height (rule 14).
        SettingRow(() => _loc.T("settings.tile.size"), false,
            LookChoice("common.small",  () => LookT.TileSize == TileSize.Small,  () => LookT.TileSize = TileSize.Small,  () => _lookView != 0),
            LookChoice("common.medium", () => LookT.TileSize == TileSize.Medium, () => LookT.TileSize = TileSize.Medium, () => _lookView != 0),
            LookChoice("common.large",  () => LookT.TileSize == TileSize.Large,  () => LookT.TileSize = TileSize.Large,  () => _lookView != 0),
            LookHelp("tileSize", "settings.tile.size")));

    private void SetNameLine(bool spec, bool cls) { LookT.Spec = spec; LookT.ClassName = cls; PersistToggles(); }

    // ── BATTLE IMAGINE ────────────────────────────────────────────────────────────────────────────────────────────
    private HudElement LookImaginePanel() => SectionPanel(() => _loc.T("settings.section.battleImagine"), null,
        SettingRow(() => _loc.T("settings.imagine.show"), false,
            ToggleChip(() => LookT.Imagine, v => { LookT.Imagine = v; PersistToggles(); }),
            ImagineHelp("show", "settings.imagine.show")),
        SettingRow(() => _loc.T("settings.imagine.cooldown"), false,
            ToggleChip(() => LookT.ImagineCooldown, v => { LookT.ImagineCooldown = v; PersistToggles(); }, () => LookT.Imagine),
            ImagineHelp("cooldown", "settings.imagine.cooldown")),
        SettingRow(() => _loc.T("settings.imagine.size"), false,
            LookChoice("common.small", () => LookT.ImagineSize == ImagineSize.Small, () => LookT.ImagineSize = ImagineSize.Small),
            LookChoice("common.large", () => LookT.ImagineSize == ImagineSize.Large, () => LookT.ImagineSize = ImagineSize.Large),
            ImagineHelp("size", "settings.imagine.size")),
        SettingRow(() => _loc.T("settings.imagine.position"), false,
            LookChoice("settings.pos.topRight", () => LookT.ImaginePosition == ImaginePosition.TopRight,    () => LookT.ImaginePosition = ImaginePosition.TopRight),
            LookChoice("settings.pos.right",    () => LookT.ImaginePosition == ImaginePosition.RightColumn, () => LookT.ImaginePosition = ImaginePosition.RightColumn),
            LookChoice("settings.pos.left",     () => LookT.ImaginePosition == ImaginePosition.Left,        () => LookT.ImaginePosition = ImaginePosition.Left),
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
        SettingRow(() => _loc.T("settings.bar.spineBar"), false,
            BarsChoice("common.off",      () => LookT.VerticalBar == VerticalBarMode.Off, () => LookT.VerticalBar = VerticalBarMode.Off),
            BarsChoice("list.metric.dps", () => LookT.VerticalBar == VerticalBarMode.Dps, () => LookT.VerticalBar = VerticalBarMode.Dps),
            BarsChoice("common.hp",       () => LookT.VerticalBar == VerticalBarMode.Hp,  () => LookT.VerticalBar = VerticalBarMode.Hp),
            BarsHelp("spineBar", "settings.bar.spineBar")),
        SettingRow(() => _loc.T("settings.bar.spineWidth"), false,
            BarsChoice("settings.width.thin",   () => LookT.SpineWidth <= 3f,            () => LookT.SpineWidth = 3f),
            BarsChoice("settings.width.normal", () => LookT.SpineWidth is > 3f and <= 5f, () => LookT.SpineWidth = 5f),
            BarsChoice("settings.width.wide",   () => LookT.SpineWidth > 5f,             () => LookT.SpineWidth = 8f),
            BarsHelp("spineWidth", "settings.bar.spineWidth")));

    // ── NUMBERS ───────────────────────────────────────────────────────────────────────────────────────────────────
    private HudElement LookNumbersPanel() => SectionPanel(() => _loc.T("cfg.look.numbers"), null,
        NumbersToggleRow("perSecond", "cfg.look.perSecond", () => LookT.Primary, v => LookT.Primary = v),
        NumbersToggleRow("total",     "settings.toggle.total", () => LookT.Total, v => LookT.Total = v),
        NumbersToggleRow("share",     "settings.toggle.sharePct", () => LookT.Share, v => LookT.Share = v));

    // ── METER WINDOW ──────────────────────────────────────────────────────────────────────────────────────────────
    // NEW (D3): "Background" 0–100 %, default 0 — a rounded theme panel behind the meter (drawn by the meter HUD, which
    // reads MeterBackgroundOpacity). ONE value for the meter window, not per view. "Colours → Edit colours…" is not
    // here: the role / HP / self colours are framework theme slots (IColorRegistry) and the SDK has no way for a plugin
    // to open the theme editor, so the row was dropped as the approved spec allows.
    private readonly FmtCache _bgPctFmt = new();

    private HudElement LookMeterWindowPanel() => SectionPanel(() => _loc.T("cfg.look.meterWindow"), null,
        SettingRow(() => _loc.T("cfg.look.background"), false,
            new CellElement(RoundSlider(() => MathF.Round(MeterBackgroundOpacity * 100f), v => SetMeterBackground(v / 100f), 0f, 100f)
                with { Width = 150f }, Width: 150f),
            new TextElement(() => _bgPctFmt.Get("{0}%", (long)MathF.Round(MeterBackgroundOpacity * 100f)),
                Color: () => TextColor(), Width: 38f, Align: TextAlign.Right, NoWrap: true, FontSize: 13) { Bold = true },
            HelpDot("look.background", "cfg.look.meterWindow", "cfg.look.background", "help.look.background")));

    // ── row helpers ───────────────────────────────────────────────────────────────────────────────────────────────
    private HudElement LookToggleRow(string id, string labelKey, Func<bool> get, Action<bool> set)
        => SettingRow(() => _loc.T(labelKey), false, ToggleChip(get, v => { set(v); PersistToggles(); }), LookHelp(id, labelKey));

    private HudElement NumbersToggleRow(string id, string labelKey, Func<bool> get, Action<bool> set)
        => SettingRow(() => _loc.T(labelKey), false, ToggleChip(get, v => { set(v); PersistToggles(); }),
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
