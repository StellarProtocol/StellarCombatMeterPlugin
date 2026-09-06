using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

// Appearance settings panel — per-mode element-visibility toggles for the meter row (List vs Party-focus).
// Opened from the ≡ menu (Plugin.Header.cs). Mutates the live MeterElementToggles instances (ListToggles /
// PartyToggles in Plugin.List.cs) and persists to the "combatmeter" config section; BuildRowData reads them
// on the next refresh tick, so changes apply without a reload.
public sealed partial class Plugin
{
    private const float SettingsScrollH = 500f;
    private int _settingsTab;   // 0 = List, 1 = Party (5), 2 = Party (20)

    private IWindowControl BuildAndRegisterSettings()
        => _services.Windows.Register(new WindowRegistration(
            new WindowSpec(
                Id:          "combatmeter.settings",
                Title:       _loc.T("settings.appearance.title"),   // baked at registration; rebuilt on LanguageChanged
                DefaultRect: new WindowRect(900f, 120f, 380f, 640f),
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            { StartVisible = false, Closable = true, Draggable = true,
              ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                && (_services.ClientState.UiState & GameUIState.Loading) == 0 },
            BuildSettingsRoot(),
            OnClose: () => _settingsWindow.SetVisible(false)));

    private void ToggleAppearance()
    {
        if (!_settingsWindow.IsShown)
            _settingsTab = _viewMode == ViewMode.List ? 0 : IsRaid20View ? 2 : 1;
        _settingsWindow.SetVisible(!_settingsWindow.IsShown);
    }

    private string ActiveModeLabel()
        => _viewMode == ViewMode.List ? _loc.T("header.mode.list") : IsRaid20View ? _loc.T("settings.mode.party20") : _loc.T("settings.mode.party5");

    private HudElement BuildSettingsRoot()
        => new ColumnElement(new HudElement[]
        {
            new TextElement(() => _loc.T("settings.appearance.subtitle"), Emphasis: true),
            new TextElement(() => _loc.TFormat("settings.appearance.active", ActiveModeLabel()), MutedCol),
            new SeparatorElement(),
            new RowElement(new HudElement[]
            {
                new ButtonElement(() => _loc.T("header.mode.list"),       () => _settingsTab = 0, Active: () => _settingsTab == 0, Width: 96f),
                new ButtonElement(() => _loc.T("settings.mode.party5"),  () => _settingsTab = 1, Active: () => _settingsTab == 1, Width: 96f),
                new ButtonElement(() => _loc.T("settings.mode.party20"), () => _settingsTab = 2, Active: () => _settingsTab == 2, Width: 96f),
            }, Gap: 6f),
            new SeparatorElement(),
            new ScrollElement(
                new ConditionalElement(
                    () => _settingsTab == 0,
                    ToggleGroup(ListToggles),
                    new ConditionalElement(
                        () => _settingsTab == 1,
                        ToggleGroup(Party5Toggles),
                        ToggleGroup(Party20Toggles))),
                SettingsScrollH),
        }, Gap: 4f);

    private HudElement ToggleGroup(MeterElementToggles t)
        => new ColumnElement(new HudElement[]
        {
            SectionLabel("settings.section.identity"),
            ToggleRow("settings.toggle.rank",        () => t.Rank,      v => t.Rank = v),
            ToggleRow("settings.toggle.crest", () => t.Crest,     v => t.Crest = v),
            ToggleRow("settings.toggle.spec",   () => t.Spec,      v => t.Spec = v),
            ToggleRow("settings.toggle.className",  () => t.ClassName, v => t.ClassName = v),

            new SpacerElement(Height: 10f),
            SectionLabel("settings.section.bars"),
            MainBarRow(t),
            VerticalBarRow(t),
            SpineWidthRow(t),

            new SpacerElement(Height: 10f),
            SectionLabel("settings.section.metrics"),
            ToggleRow("settings.toggle.perSecond", () => t.Primary, v => t.Primary = v),
            ToggleRow("settings.toggle.total",      () => t.Total,   v => t.Total = v),
            ToggleRow("settings.toggle.sharePct",    () => t.Share,   v => t.Share = v),

            new SpacerElement(Height: 10f),
            SectionLabel("settings.section.battleImagine"),
            ImagineShowRow(t),
            ImagineSizeRow(t),
            ImaginePositionRow(t),

            new SpacerElement(Height: 10f),
            SectionLabel("settings.section.other"),
            ToggleRow("settings.toggle.leaderFlag",        () => t.LeaderFlag,    v => t.LeaderFlag = v),
            ToggleRow("settings.toggle.abilityScore",      () => t.AbilityScore,  v => t.AbilityScore = v),
            ToggleRow("settings.toggle.illusionBreak", () => t.IllusionBreak, v => t.IllusionBreak = v),
            ToggleRow("settings.toggle.voiceIcon",         () => t.VoiceIcon,     v => t.VoiceIcon = v),

            new SpacerElement(Height: 10f),
            new TextElement(() => _loc.T("settings.appearance.autoStyled"), MutedCol),
        }, Gap: 3f);

    // text is a catalog key (resolved live so the section header switches language in place).
    private HudElement SectionLabel(string text)
        => new TextElement(() => _loc.T(text), MutedCol);

    // indent=true prepends a small spacer so the toggle (checkbox + label) nests visually under a
    // parent trigger row — the same left inset PillRow uses — for a sub-option like "Ignore when
    // solo" (under Team wipe).
    private HudElement ToggleRow(string label, Func<bool> get, Action<bool> set, Func<bool>? enabled = null, bool indent = false)
    {
        var toggle = new ToggleElement(() => "", get, v => { set(v); PersistToggles(); }, enabled);
        var text   = new TextElement(() => _loc.T(label));   // label is a catalog key
        return indent
            ? new RowElement(new HudElement[] { new SpacerElement(Width: 8f), toggle, text }, Gap: 8f)
            : new RowElement(new HudElement[] { toggle, text }, Gap: 8f);
    }

    private HudElement ImagineShowRow(MeterElementToggles t)
        => new RowElement(new HudElement[]
        {
            new ToggleElement(() => "", () => t.Imagine,         v => { t.Imagine = v;         PersistToggles(); }),
            new TextElement(() => _loc.T("settings.imagine.show")),
            new ToggleElement(() => "", () => t.ImagineCooldown, v => { t.ImagineCooldown = v; PersistToggles(); }, () => t.Imagine),
            new TextElement(() => _loc.T("settings.imagine.cooldown")),
        }, Gap: 8f);

    private HudElement ImagineSizeRow(MeterElementToggles t)
        => new RowElement(new HudElement[]
        {
            new TextElement(() => _loc.T("settings.imagine.size"), MutedCol, Width: 80f),
            new ButtonElement(() => _loc.T("common.small"), () => { t.ImagineSize = ImagineSize.Small; PersistToggles(); },
                Active: () => t.ImagineSize == ImagineSize.Small, Width: 72f),
            new ButtonElement(() => _loc.T("common.large"), () => { t.ImagineSize = ImagineSize.Large; PersistToggles(); },
                Active: () => t.ImagineSize == ImagineSize.Large, Width: 72f),
        }, Gap: 6f);

    private HudElement ImaginePositionRow(MeterElementToggles t)
        => new RowElement(new HudElement[]
        {
            new TextElement(() => _loc.T("settings.imagine.position"), MutedCol, Width: 80f),
            PosBtn(t, "settings.pos.topRight", ImaginePosition.TopRight),
            PosBtn(t, "settings.pos.right", ImaginePosition.RightColumn),
            PosBtn(t, "settings.pos.left",  ImaginePosition.Left),
        }, Gap: 6f);

    private HudElement PosBtn(MeterElementToggles t, string label, ImaginePosition pos)
        => new ButtonElement(() => _loc.T(label), () => { t.ImaginePosition = pos; PersistToggles(); },
            Active: () => t.ImaginePosition == pos, Width: 72f);

    private HudElement VerticalBarRow(MeterElementToggles t)
        => new RowElement(new HudElement[]
        {
            new TextElement(() => _loc.T("settings.bar.spineBar"), Width: 80f),
            new ButtonElement(() => _loc.T("common.off"), () => { t.VerticalBar = VerticalBarMode.Off; PersistToggles(); },
                Active: () => t.VerticalBar == VerticalBarMode.Off, Width: 72f),
            new ButtonElement(() => _loc.T("list.metric.dps"), () => { t.VerticalBar = VerticalBarMode.Dps; PersistToggles(); },
                Active: () => t.VerticalBar == VerticalBarMode.Dps, Width: 72f),
            new ButtonElement(() => _loc.T("common.hp"),  () => { t.VerticalBar = VerticalBarMode.Hp;  PersistToggles(); },
                Active: () => t.VerticalBar == VerticalBarMode.Hp,  Width: 72f),
        }, Gap: 6f);

    private HudElement SpineWidthRow(MeterElementToggles t)
        => new RowElement(new HudElement[]
        {
            new TextElement(() => _loc.T("settings.bar.spineWidth"), MutedCol, Width: 80f),
            new ButtonElement(() => _loc.T("settings.width.thin"),   () => { t.SpineWidth = 3f; PersistToggles(); }, Active: () => t.SpineWidth <= 3f,            Width: 72f),
            new ButtonElement(() => _loc.T("settings.width.normal"), () => { t.SpineWidth = 5f; PersistToggles(); }, Active: () => t.SpineWidth is > 3f and <= 5f, Width: 72f),
            new ButtonElement(() => _loc.T("settings.width.wide"),   () => { t.SpineWidth = 8f; PersistToggles(); }, Active: () => t.SpineWidth > 5f,              Width: 72f),
        }, Gap: 6f);

    private HudElement MainBarRow(MeterElementToggles t)
        => new RowElement(new HudElement[]
        {
            new TextElement(() => _loc.T("settings.bar.mainBar"), Width: 80f),
            new ButtonElement(() => _loc.T("list.metric.dps"), () => { t.MainBarIsHp = false; PersistToggles(); },
                Active: () => !t.MainBarIsHp, Width: 72f),
            new ButtonElement(() => _loc.T("common.hp"),  () => { t.MainBarIsHp = true;  PersistToggles(); },
                Active: () => t.MainBarIsHp,  Width: 72f),
        }, Gap: 6f);

    private void PersistToggles()
    {
        ListToggles.Save(_prefs, "list");
        Party5Toggles.Save(_prefs, "party5");
        Party20Toggles.Save(_prefs, "party20");
    }
}
