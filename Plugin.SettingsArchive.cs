using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.CombatMeter.LogUpload;

namespace Stellar.CombatMeter;

// Dedicated Settings pane (gear icon, Plugin.Header.cs) — auto-archive config + uploads. Moved out
// of the Appearance panel (Plugin.Settings.cs, Task 5) so element-visibility toggles and archive/
// upload policy aren't crammed into one scroll. All engine policy lives in AutoArchiveEngine; this
// partial only reads/writes the Plugin.AutoArchive.cs accessors wired in Tasks 1-4.
public sealed partial class Plugin
{
    private IWindowControl _archiveSettingsWindow = null!;

    // Cached gear icon for the header "Settings" button (real embedded PNG, replacing the old unicode
    // gear-glyph label the game font dropped). Loaded once at construction (Plugin.cs) via
    // LoadSettingsGearPng — never throws; a missing/corrupt resource degrades to null and the button
    // just shows text, same as before this fix.
    private byte[]? _settingsGearPng;

    // Loads Resources/settings-gear.png (packed via the csproj EmbeddedResource entry) for
    // ButtonElement.Icon. Mirrors Stellar.StatInspector.StatIconAtlas.GearPng's try/catch shape.
    private static byte[]? LoadSettingsGearPng()
    {
        try
        {
            using var s = typeof(Plugin).Assembly.GetManifestResourceStream("Stellar.CombatMeter.settings-gear.png");
            if (s == null) return null;
            using var ms = new System.IO.MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

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
    {
        var rows = new List<HudElement>
        {
            new TextElement(() => "Auto archive", Emphasis: true),
            ToggleRow("Auto-archive (off = manual only)", () => AutoArchiveEnabled, v => AutoArchiveEnabled = v),
            new TextElement(LastArchiveLabel, MutedCol),
            PillRow("Min gap",   () => AutoArchiveCooldownS,      v => AutoArchiveCooldownS = v, 5, 10, 30, 60),
            PillRow("Settle",    () => AutoArchiveSettleS,        v => AutoArchiveSettleS   = v, 0, 1, 2, 5),
            new SeparatorElement(),

            ToggleRow("Team wipe", () => AutoArchiveWipe, v => AutoArchiveWipe = v),
            new TextElement(() => "   Archives when everyone (or you, solo) goes down.", MutedCol),
            PillRow("Revive grace", () => AutoArchiveWipeGraceS, v => AutoArchiveWipeGraceS = v, new[] { 0, 2, 5 }, () => AutoArchiveWipe),
            ToggleRow("Ignore when solo", () => AutoArchiveWipeIgnoreSolo, v => AutoArchiveWipeIgnoreSolo = v, () => AutoArchiveWipe, indent: true),

            ToggleRow("Boss phase", () => AutoArchiveBoss, v => AutoArchiveBoss = v),
            new TextElement(() => "   Cuts a fresh segment when a boss fight starts, and archives the fight when the boss dies.", MutedCol),
            PillRow("Keep before", () => AutoArchiveKeepBeforeS, v => AutoArchiveKeepBeforeS = v, new[] { 0, 3, 5 }, () => AutoArchiveBoss),

            ToggleRow("Combat idle", () => AutoArchiveIdle, v => AutoArchiveIdle = v),
            new TextElement(() => "   Archives after no combat for a while.", MutedCol),
            PillRow("Idle timeout", () => AutoArchiveIdleTimeoutS, v => AutoArchiveIdleTimeoutS = v, new[] { 30, 60, 120, 300 }, () => AutoArchiveIdle),

            ToggleRow("Dungeon stage change", () => AutoArchiveStage, v => AutoArchiveStage = v),
            new TextElement(() => "   Archives when the dungeon advances (floor clear / settlement).", MutedCol),

            new SeparatorElement(),
            new TextElement(() => "Uploads", Emphasis: true),
        };
        rows.AddRange(UploadsSection());
        // Scroll the whole pane. MEASURED (tools/run-ui-sandbox.sh combatmeter-settings-full-window-ugui):
        // after the Uploads section grew from 2 toggle rows to 8 pill rows + 3 headers, the pane's content
        // is 810px in an 833px window — against a DefaultRect height of 620f, so ~213px would have been
        // unreachable. Raising DefaultRect alone would NOT have fixed it: every existing install already
        // has a persisted 620f rect, and this window is not resizable, so they would still be clipped.
        // A scroll viewport is reachable at any persisted height.
        return new ScrollElement(new ColumnElement(rows, Gap: 4f), SettingsScrollHeight);
    }

    // Viewport height for the settings pane's scroll. Sized to sit inside the 620f DefaultRect with room
    // for the GlassMenu title bar and the pane's own 11/12px vertical padding (measured).
    private const float SettingsScrollHeight = 540f;

    // ---- Uploads section: the per-content grid (spec § 2.5) ----
    // Eight cells — four content kinds × {run stats, replay position track} — laid out as two labelled
    // four-row groups rather than one 4 × 2 block of tri-states. Same eight cells either way, but this
    // shape lets every cell reuse PillRow's exact geometry (label Width 96f, pill Width 48f, Gap 6f), so
    // the section matches the option-row idiom the rest of this pane already uses and needs no widening
    // of the 380f window. Geometry confirmed by tools/run-ui-sandbox.sh measurement, not by eye.
    //
    // Run stats are TRI-state (auto/manual/off). Replay is TWO-state (auto/off) — owner ruling
    // 2026-07-28: a `manual` replay cell cannot upload on any path, because the retained re-upload
    // payload takes its positions from the archive-time doc, which is null under `manual`; offering it
    // would be a control that silently does nothing while still paying the 2 Hz position probe. Spec
    // § 2.2 already reasoned this way when it seeded legacy uploadReplay=false to `off`, not `manual`.
    //
    // The "(dungeon/raid)" parenthetical the old replay toggle carried is GONE: it misdescribed
    // behaviour. World Dominator (7150/7151/7152) is SceneType 2 / SubType 5 — the same instanced
    // classification as dungeons and raids — so world-boss replays already capture and upload.
    private HudElement[] UploadsSection()
    {
        var rows = new List<HudElement>
        {
            new TextElement(() => "   auto = uploads itself · manual = only when you press upload · off = never.", MutedCol),
            new TextElement(() => "Run stats", Emphasis: true),
        };
        foreach (var kind in UploadPolicyTable.Kinds) rows.Add(PolicyRow(kind, UploadArtifact.Stats));
        rows.Add(new TextElement(() => "Replay position track", Emphasis: true));
        foreach (var kind in UploadPolicyTable.Kinds) rows.Add(PolicyRow(kind, UploadArtifact.Replay));
        // The content list (which mapIds count as dungeon/raid/world boss) is fetched ONCE and cached,
        // then re-fetched only when the plugin updates — every request to the site's Worker is billed, so
        // there is no polling (owner ruling 2026-07-28). This button is the escape hatch for a content
        // patch that lands without a plugin release. User-initiated, so it cannot run away.
        rows.Add(new RowElement(new HudElement[]
        {
            new SpacerElement(Width: 8f),
            new ButtonElement(() => "Refresh content list", RefreshContentKindsNow, Width: 150f),
        }, Gap: 6f));
        return rows.ToArray();
    }

    // Tri-state states for run stats; two-state for replay (see UploadsSection). Mirrors PillRow's
    // geometry exactly. `Active` is read LIVE on every poll — never captured at build time — so the
    // highlighted pill tracks the value actually in effect, the same contract PillRow documents.
    private static readonly UploadPolicyState[] StatsStates =
        { UploadPolicyState.Auto, UploadPolicyState.Manual, UploadPolicyState.Off };

    private static readonly UploadPolicyState[] ReplayStates =
        { UploadPolicyState.Auto, UploadPolicyState.Off };

    // WIDER than PillRow's 48f, which was sized for "30s"/"120s". MEASURED: at 48f the word "manual"
    // wraps to two lines inside its pill ("manu/al") — caught by the sandbox render, invisible in the
    // measurement JSON. 60f holds it on one line and the row still fits: 8 (spacer) + 96 (label)
    // + 3 x 60 (pills) + 4 x 6 (gaps) = 308px against 366px of inner pane width.
    private const float PolicyPillWidth = 60f;

    private HudElement PolicyRow(ContentKind kind, UploadArtifact artifact)
    {
        var kids = new List<HudElement>
        {
            new SpacerElement(Width: 8f),
            new TextElement(() => UploadPolicy.Label(kind), MutedCol, Width: 96f),
        };
        var states = artifact == UploadArtifact.Replay ? ReplayStates : StatsStates;
        for (var i = 0; i < states.Length; i++)
        {
            // Replay has no `manual`, so hold that middle column open with a spacer: `off` then lands
            // under the stats rows' `off` and the two groups read as one grid instead of two ragged
            // lists. This is the layout the owner approved on 2026-07-28.
            if (artifact == UploadArtifact.Replay && i == 1) kids.Add(new SpacerElement(Width: PolicyPillWidth));
            var s = states[i];
            kids.Add(new ButtonElement(() => UploadPolicy.Format(s), () => SetUploadPolicy(kind, artifact, s),
                Active: () => UploadPolicyFor(kind, artifact) == s, Width: PolicyPillWidth));
        }
        return new RowElement(kids.ToArray(), Gap: 6f);
    }

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
