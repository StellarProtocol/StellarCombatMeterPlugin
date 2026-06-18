using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

// Appearance settings panel — per-mode element-visibility toggles for the meter row (List vs Party-focus).
// Opened from the ≡ menu (Plugin.Header.cs). Mutates the live MeterElementToggles instances (ListToggles /
// PartyToggles in Plugin.List.cs) and persists to the "combatmeter" config section; BuildRowData reads them
// on the next refresh tick, so changes apply without a reload. Mirrors StatInspector's settings pattern.
public sealed partial class Plugin
{
    private const float SettingsScrollH = 420f;   // scroll viewport for the (tall) two-mode toggle list

    private IWindowControl BuildAndRegisterSettings()
        => _services.Windows.Register(new WindowRegistration(
            new WindowSpec(
                Id:          "combatmeter.settings",
                Title:       "CombatMeter Appearance",
                DefaultRect: new WindowRect(900f, 120f, 320f, 470f),
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            { StartVisible = false, HideUntilInWorld = true, Closable = true, Draggable = true },
            BuildSettingsRoot(),
            OnClose: () => _settingsWindow.SetVisible(false)));

    private void ToggleAppearance() => _settingsWindow.SetVisible(!_settingsWindow.IsShown);

    // The toggle group whose edits affect what's on screen RIGHT NOW (the meter uses List vs Party-5 vs
    // Party-20 by view mode + live party size). Surfaced so toggling a non-matching group isn't confusing.
    private string ActiveModeLabel()
        => _viewMode == ViewMode.List ? "List mode" : IsRaid20View ? "Party-focus (20)" : "Party-focus (5)";

    private HudElement BuildSettingsRoot()
        => new ColumnElement(new HudElement[]
        {
            new TextElement(() => "Appearance — show/hide row elements", Emphasis: true),
            new TextElement(() => $"Current view → {ActiveModeLabel()} (edit that group to change the meter now)", MutedCol),
            new SeparatorElement(),
            new ScrollElement(new ColumnElement(new HudElement[]
            {
                ToggleGroup("List mode", ListToggles),
                new SeparatorElement(),
                ToggleGroup("Party-focus (5)", Party5Toggles),
                new SeparatorElement(),
                ToggleGroup("Party-focus (20)", Party20Toggles),
                new SeparatorElement(),
                new TextElement(() => "States (self · leader · dead · offline) are styled automatically.", MutedCol),
            }, Gap: 4f), SettingsScrollH),
        }, Gap: 4f);

    // One per-mode group. Captures the live toggle instance `t` (a reference type) so the row lambdas mutate
    // the same object BuildRowData reads. ToggleGroup is built once at registration.
    private HudElement ToggleGroup(string title, MeterElementToggles t)
        => new ColumnElement(new HudElement[]
        {
            new TextElement(() => title, Emphasis: true),
            ToggleRow("Rank",           () => t.Rank,      v => t.Rank = v),
            ToggleRow("Class crest",    () => t.Crest,     v => t.Crest = v),
            ToggleRow("Spec name",      () => t.Spec,      v => t.Spec = v),
            ToggleRow("Class name",     () => t.ClassName, v => t.ClassName = v),
            ToggleRow("HP spine",       () => t.HpBar,     v => t.HpBar = v),
            ToggleRow("Per-second",     () => t.Primary,   v => t.Primary = v),
            ToggleRow("Total",          () => t.Total,     v => t.Total = v),
            ToggleRow("Share %",        () => t.Share,     v => t.Share = v),
            ToggleRow("Battle Imagine", () => t.Imagine,   v => t.Imagine = v),
            ToggleRow("  · cooldown",   () => t.ImagineCooldown, v => t.ImagineCooldown = v, enabled: () => t.Imagine),
            ImagineSizeRow(t),
            ImaginePositionRow(t),
            ToggleRow("Leader flag",    () => t.LeaderFlag, v => t.LeaderFlag = v),
            ToggleRow("Ability score",  () => t.AbilityScore, v => t.AbilityScore = v),
        }, Gap: 2f);

    // Pill toggle switch (same widget the framework Settings → Plugins panel uses) + label beside it.
    // ToggleElement renders only the 30px switch (its Label is ignored by BuildToggle), so the label is a
    // separate TextElement — putting the switch in a Row keeps it fixed-width instead of force-expanded.
    private HudElement ToggleRow(string label, Func<bool> get, Action<bool> set, Func<bool>? enabled = null)
        => new RowElement(new HudElement[]
        {
            new ToggleElement(() => "", get, v => { set(v); PersistToggles(); }, enabled),
            new TextElement(() => label),
        }, Gap: 8f);

    private HudElement ImagineSizeRow(MeterElementToggles t)
        => new RowElement(new HudElement[]
        {
            new TextElement(() => "  · size", MutedCol),
            new ButtonElement(() => "Small", () => { t.ImagineSize = ImagineSize.Small; PersistToggles(); },
                Enabled: () => t.Imagine, Active: () => t.ImagineSize == ImagineSize.Small, Width: 52f),
            new ButtonElement(() => "Large", () => { t.ImagineSize = ImagineSize.Large; PersistToggles(); },
                Enabled: () => t.Imagine, Active: () => t.ImagineSize == ImagineSize.Large, Width: 52f),
        }, Gap: 4f);

    private HudElement ImaginePositionRow(MeterElementToggles t)
        => new RowElement(new HudElement[]
        {
            new TextElement(() => "  · pos", MutedCol),
            PosBtn(t, "Top-R", ImaginePosition.TopRight),
            PosBtn(t, "Right", ImaginePosition.RightColumn),
            PosBtn(t, "Left",  ImaginePosition.Left),
        }, Gap: 4f);

    private HudElement PosBtn(MeterElementToggles t, string label, ImaginePosition pos)
        => new ButtonElement(() => label, () => { t.ImaginePosition = pos; PersistToggles(); },
            Enabled: () => t.Imagine, Active: () => t.ImaginePosition == pos, Width: 48f);

    private void PersistToggles()
    {
        ListToggles.Save(_prefs, "list");
        Party5Toggles.Save(_prefs, "party5");
        Party20Toggles.Save(_prefs, "party20");
    }
}
