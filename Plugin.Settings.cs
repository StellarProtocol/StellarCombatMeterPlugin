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
                Title:       "CombatMeter Appearance",
                DefaultRect: new WindowRect(900f, 120f, 380f, 640f),
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            { StartVisible = false, HideUntilInWorld = true, Closable = true, Draggable = true },
            BuildSettingsRoot(),
            OnClose: () => _settingsWindow.SetVisible(false)));

    private void ToggleAppearance()
    {
        if (!_settingsWindow.IsShown)
            _settingsTab = _viewMode == ViewMode.List ? 0 : IsRaid20View ? 2 : 1;
        _settingsWindow.SetVisible(!_settingsWindow.IsShown);
    }

    private string ActiveModeLabel()
        => _viewMode == ViewMode.List ? "List" : IsRaid20View ? "Party (20)" : "Party (5)";

    private HudElement BuildSettingsRoot()
        => new ColumnElement(new HudElement[]
        {
            new TextElement(() => "Appearance — show/hide row elements", Emphasis: true),
            new TextElement(() => $"Active → {ActiveModeLabel()}", MutedCol),
            new SeparatorElement(),
            new RowElement(new HudElement[]
            {
                new ButtonElement(() => "List",       () => _settingsTab = 0, Active: () => _settingsTab == 0, Width: 96f),
                new ButtonElement(() => "Party (5)",  () => _settingsTab = 1, Active: () => _settingsTab == 1, Width: 96f),
                new ButtonElement(() => "Party (20)", () => _settingsTab = 2, Active: () => _settingsTab == 2, Width: 96f),
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
            new SeparatorElement(),
            new TextElement(() => "Uploads", Emphasis: true),
            ToggleRow("Auto-upload runs", () => AutoUpload, v => AutoUpload = v),
        }, Gap: 4f);

    private HudElement ToggleGroup(MeterElementToggles t)
        => new ColumnElement(new HudElement[]
        {
            SectionLabel("Identity"),
            ToggleRow("Rank",        () => t.Rank,      v => t.Rank = v),
            ToggleRow("Class crest", () => t.Crest,     v => t.Crest = v),
            ToggleRow("Spec name",   () => t.Spec,      v => t.Spec = v),
            ToggleRow("Class name",  () => t.ClassName, v => t.ClassName = v),

            new SpacerElement(Height: 10f),
            SectionLabel("Bars"),
            MainBarRow(t),
            VerticalBarRow(t),
            SpineWidthRow(t),

            new SpacerElement(Height: 10f),
            SectionLabel("Metrics"),
            ToggleRow("Per-second", () => t.Primary, v => t.Primary = v),
            ToggleRow("Total",      () => t.Total,   v => t.Total = v),
            ToggleRow("Share %",    () => t.Share,   v => t.Share = v),

            new SpacerElement(Height: 10f),
            SectionLabel("Battle Imagine"),
            ImagineShowRow(t),
            ImagineSizeRow(t),
            ImaginePositionRow(t),

            new SpacerElement(Height: 10f),
            SectionLabel("Other"),
            ToggleRow("Leader flag",   () => t.LeaderFlag,   v => t.LeaderFlag = v),
            ToggleRow("Ability score", () => t.AbilityScore, v => t.AbilityScore = v),
            ToggleRow("Voice icon",    () => t.VoiceIcon,    v => t.VoiceIcon = v),

            new SpacerElement(Height: 10f),
            new TextElement(() => "Self · leader · dead · offline states are styled automatically.", MutedCol),
        }, Gap: 3f);

    private HudElement SectionLabel(string text)
        => new TextElement(() => text, MutedCol);

    private HudElement ToggleRow(string label, Func<bool> get, Action<bool> set, Func<bool>? enabled = null)
        => new RowElement(new HudElement[]
        {
            new ToggleElement(() => "", get, v => { set(v); PersistToggles(); }, enabled),
            new TextElement(() => label),
        }, Gap: 8f);

    private HudElement ImagineShowRow(MeterElementToggles t)
        => new RowElement(new HudElement[]
        {
            new ToggleElement(() => "", () => t.Imagine,         v => { t.Imagine = v;         PersistToggles(); }),
            new TextElement(() => "Show"),
            new ToggleElement(() => "", () => t.ImagineCooldown, v => { t.ImagineCooldown = v; PersistToggles(); }, () => t.Imagine),
            new TextElement(() => "Cooldown"),
        }, Gap: 8f);

    private HudElement ImagineSizeRow(MeterElementToggles t)
        => new RowElement(new HudElement[]
        {
            new TextElement(() => "Size", MutedCol, Width: 80f),
            new ButtonElement(() => "Small", () => { t.ImagineSize = ImagineSize.Small; PersistToggles(); },
                Active: () => t.ImagineSize == ImagineSize.Small, Width: 72f),
            new ButtonElement(() => "Large", () => { t.ImagineSize = ImagineSize.Large; PersistToggles(); },
                Active: () => t.ImagineSize == ImagineSize.Large, Width: 72f),
        }, Gap: 6f);

    private HudElement ImaginePositionRow(MeterElementToggles t)
        => new RowElement(new HudElement[]
        {
            new TextElement(() => "Position", MutedCol, Width: 80f),
            PosBtn(t, "Top-R", ImaginePosition.TopRight),
            PosBtn(t, "Right", ImaginePosition.RightColumn),
            PosBtn(t, "Left",  ImaginePosition.Left),
        }, Gap: 6f);

    private HudElement PosBtn(MeterElementToggles t, string label, ImaginePosition pos)
        => new ButtonElement(() => label, () => { t.ImaginePosition = pos; PersistToggles(); },
            Active: () => t.ImaginePosition == pos, Width: 72f);

    private HudElement VerticalBarRow(MeterElementToggles t)
        => new RowElement(new HudElement[]
        {
            new TextElement(() => "Spine bar", Width: 80f),
            new ButtonElement(() => "Off", () => { t.VerticalBar = VerticalBarMode.Off; PersistToggles(); },
                Active: () => t.VerticalBar == VerticalBarMode.Off, Width: 72f),
            new ButtonElement(() => "DPS", () => { t.VerticalBar = VerticalBarMode.Dps; PersistToggles(); },
                Active: () => t.VerticalBar == VerticalBarMode.Dps, Width: 72f),
            new ButtonElement(() => "HP",  () => { t.VerticalBar = VerticalBarMode.Hp;  PersistToggles(); },
                Active: () => t.VerticalBar == VerticalBarMode.Hp,  Width: 72f),
        }, Gap: 6f);

    private HudElement SpineWidthRow(MeterElementToggles t)
        => new RowElement(new HudElement[]
        {
            new TextElement(() => "Spine width", MutedCol, Width: 80f),
            new ButtonElement(() => "Thin",   () => { t.SpineWidth = 3f; PersistToggles(); }, Active: () => t.SpineWidth <= 3f,            Width: 72f),
            new ButtonElement(() => "Normal", () => { t.SpineWidth = 5f; PersistToggles(); }, Active: () => t.SpineWidth is > 3f and <= 5f, Width: 72f),
            new ButtonElement(() => "Wide",   () => { t.SpineWidth = 8f; PersistToggles(); }, Active: () => t.SpineWidth > 5f,              Width: 72f),
        }, Gap: 6f);

    private HudElement MainBarRow(MeterElementToggles t)
        => new RowElement(new HudElement[]
        {
            new TextElement(() => "Main bar", Width: 80f),
            new ButtonElement(() => "DPS", () => { t.MainBarIsHp = false; PersistToggles(); },
                Active: () => !t.MainBarIsHp, Width: 72f),
            new ButtonElement(() => "HP",  () => { t.MainBarIsHp = true;  PersistToggles(); },
                Active: () => t.MainBarIsHp,  Width: 72f),
        }, Gap: 6f);

    private void PersistToggles()
    {
        ListToggles.Save(_prefs, "list");
        Party5Toggles.Save(_prefs, "party5");
        Party20Toggles.Save(_prefs, "party20");
    }
}
