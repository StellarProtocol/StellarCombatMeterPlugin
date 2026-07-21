using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

// Dedicated Settings pane (gear icon, Plugin.Header.cs) — auto-archive config + uploads. Moved out
// of the Appearance panel (Plugin.Settings.cs, Task 5) so element-visibility toggles and archive/
// upload policy aren't crammed into one scroll. All engine policy lives in AutoArchiveEngine; this
// partial only reads/writes the Plugin.AutoArchive.cs accessors wired in Tasks 1-4.
public sealed partial class Plugin
{
    private IWindowControl _archiveSettingsWindow = null!;

    private IWindowControl BuildAndRegisterArchiveSettings()
        => _services.Windows.Register(new WindowRegistration(
            new WindowSpec(
                Id:          "combatmeter.archiveSettings",
                Title:       "CombatMeter Settings",
                DefaultRect: new WindowRect(900f, 120f, 380f, 620f),
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            { StartVisible = false, HideUntilInWorld = true, Closable = true, Draggable = true },
            BuildAutoArchiveSettingsRoot(),
            OnClose: () => _archiveSettingsWindow.SetVisible(false)));

    private void ToggleArchiveSettings() => _archiveSettingsWindow.SetVisible(!_archiveSettingsWindow.IsShown);

    private HudElement BuildAutoArchiveSettingsRoot()
        => new ColumnElement(new HudElement[]
        {
            new TextElement(() => "Auto archive", Emphasis: true),
            ToggleRow("Auto-archive (off = manual only)", () => AutoArchiveEnabled, v => AutoArchiveEnabled = v),
            new TextElement(LastArchiveLabel, MutedCol),
            PillRow("Min gap",   () => AutoArchiveCooldownS,      v => AutoArchiveCooldownS = v, 5, 10, 30, 60),
            PillRow("Settle",    () => AutoArchiveSettleS,        v => AutoArchiveSettleS   = v, 0, 1, 2, 5),
            new SeparatorElement(),

            ToggleRow("Team wipe", () => AutoArchiveWipe, v => AutoArchiveWipe = v),
            new TextElement(() => "   Banks when everyone (or you, solo) goes down.", MutedCol),
            PillRow("Revive grace", () => AutoArchiveWipeGraceS, v => AutoArchiveWipeGraceS = v, new[] { 0, 2, 5 }, () => AutoArchiveWipe),
            ToggleRow("   Ignore when solo", () => AutoArchiveWipeIgnoreSolo, v => AutoArchiveWipeIgnoreSolo = v, () => AutoArchiveWipe),

            ToggleRow("Boss phase", () => AutoArchiveBoss, v => AutoArchiveBoss = v),
            new TextElement(() => "   Cuts a fresh segment when a boss fight starts.", MutedCol),
            ToggleRow("   Re-cut if boss re-detected", () => AutoArchiveBossRecut, v => AutoArchiveBossRecut = v, () => AutoArchiveBoss),
            PillRow("Min boss seg", () => AutoArchiveMinBossSegmentS, v => AutoArchiveMinBossSegmentS = v, new[] { 0, 10, 30 }, () => AutoArchiveBoss),

            ToggleRow("Combat idle", () => AutoArchiveIdle, v => AutoArchiveIdle = v),
            new TextElement(() => "   Banks after no combat for a while.", MutedCol),
            PillRow("Idle timeout", () => AutoArchiveIdleTimeoutS, v => AutoArchiveIdleTimeoutS = v, new[] { 30, 60, 120, 300 }, () => AutoArchiveIdle),

            ToggleRow("Dungeon stage change", () => AutoArchiveStage, v => AutoArchiveStage = v),
            new TextElement(() => "   Banks when the dungeon advances (floor clear / settlement).", MutedCol),

            new SeparatorElement(),
            new TextElement(() => "Uploads", Emphasis: true),
            ToggleRow("Auto-upload runs", () => AutoUpload, v => AutoUpload = v),
            ToggleRow("Upload replay position track (dungeon/raid)", () => UploadReplay, v => UploadReplay = v),
        }, Gap: 4f);

    // "Last archive: {tag} · {n}s ago" readout — reads LastArchive (Plugin.AutoArchive.cs, set by
    // NoteLastArchive on every BANKED archive) and formats its reason via the real ArchiveReasonTag
    // (Plugin.History.cs, internal static — same class via this partial, no qualifier needed).
    private string LastArchiveLabel()
        => LastArchive is { } la
            ? $"Last archive: {ArchiveReasonTag(la.reason)} · {(_services.CombatSnapshot.ServerNowMs - la.ms) / 1000}s ago"
            : "Last archive: —";

    // A labelled row of second-value pills (generalises the old IdleTimeoutRow/IdleTimeoutBtn). `get`
    // is read LIVE on every poll (Active: () => get() == sec) — NOT a value captured at build time —
    // so the highlighted pill tracks whichever value is currently in effect, matching the
    // Active: () => AutoArchiveIdleTimeoutS == seconds pattern the old IdleTimeoutBtn used.
    // `enabled` (optional) gates the whole row (e.g. only editable while its parent trigger is on).
    private HudElement PillRow(string label, Func<int> get, Action<int> set, params int[] seconds)
        => PillRow(label, get, set, seconds, null);

    private HudElement PillRow(string label, Func<int> get, Action<int> set, int[] seconds, Func<bool>? enabled)
    {
        var kids = new List<HudElement> { new SpacerElement(Width: 8f), new TextElement(() => label, MutedCol, Width: 96f) };
        foreach (var s in seconds)
        {
            var sec = s;
            kids.Add(new ButtonElement(() => sec == 0 ? "off" : sec + "s", () => set(sec),
                Active: () => get() == sec, Enabled: enabled ?? (() => true), Width: 48f));
        }
        return new RowElement(kids.ToArray(), Gap: 6f);
    }
}
