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
            // Caption corrected: the old "Archives when the dungeon advances (floor clear / settlement)"
            // misdescribed it — only run-END states ever arm this, never a mid-run advance.
            new TextElement(() => "   Cuts an archive when the run ends. Pick which stages.", MutedCol),
            StageChipRow(),

            new SeparatorElement(),
            new TextElement(() => "Uploads", Emphasis: true),
        };
        rows.AddRange(UploadsSection());
        // Scroll the whole pane. MEASURED (tools/run-ui-sandbox.sh combatmeter-settings-full-window-ugui):
        // the pane's content still overflows a 620f window even after the dense Uploads rewrite cut ~160px
        // off it, so the viewport stays required. Raising DefaultRect alone would NOT have fixed it either:
        // every existing install already
        // has a persisted 620f rect, and this window is not resizable, so they would still be clipped.
        // A scroll viewport is reachable at any persisted height.
        return new ScrollElement(new ColumnElement(rows, Gap: 4f), SettingsScrollHeight);
    }

    // Viewport height for the settings pane's scroll. Sized to sit inside the 620f DefaultRect with room
    // for the GlassMenu title bar and the pane's own 11/12px vertical padding (measured).
    private const float SettingsScrollHeight = 540f;

    // ---- Uploads section: the per-content grid (spec § 2.5) + the difficulty axis (§ 8.5) ----
    // ONE row per content kind carrying BOTH artifacts under a shared two-column header, plus an indented
    // chip row for the kinds that have a difficulty axis. This replaced two labelled five-row pill groups
    // (design 3d73f7a): controls 35 -> 18, rows 13 + 2 headers -> 10. Geometry confirmed by
    // tools/run-ui-sandbox.sh measurement, not by eye — the raid chip row is the widest at 15px of slack
    // inside the 366px content box.
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
            // Column header names both axes ONCE, which is what let the duplicated "Replay position track"
            // section (5 rows + its own header) be deleted outright — design 3d73f7a.
            new RowElement(new HudElement[]
            {
                new SpacerElement(Width: 106f),
                new TextElement(() => "run stats", MutedCol, Width: PolicyDropdownWidth),
                new TextElement(() => "replay", MutedCol, Width: PolicyDropdownWidth),
            }, Gap: 6f),
        };
        foreach (var kind in UploadPolicyTable.Kinds)
        {
            rows.Add(KindRow(kind));
            // The difficulty axis exists only for the kinds that HAVE one; TiersFor is that authority, so
            // world boss / vaults / other get no chip row rather than an empty one.
            if (UploadTierFilter.TiersFor.TryGetValue(kind, out var tiers)) rows.Add(TierRow(kind, tiers));
            // Master is a dungeon-only tier, so its level floor belongs under the dungeon chips.
            if (kind == ContentKind.Dungeon) rows.Add(MasterLevelRow());
        }
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

    // Owner rejected the pill grid outright — "I really don't like the ui that have tons of button like
    // this" — because it put ~35 buttons in one pane. The replacement (design 3d73f7a) picks the control
    // that MATCHES each axis's semantics instead of using pills for all three:
    //   * auto/manual/off is MUTUALLY EXCLUSIVE -> DropdownElement, whose own doc calls it "a reusable
    //     replacement for a click-to-cycle button". 25 pills collapse to 10 triggers.
    //   * the tier axis is genuinely MULTI-SELECT -> chips stay. A dropdown cannot express
    //     "hard + master, not normal" without inventing a bogus "custom" entry.
    //   * the master level is ORDINAL over 1..20 -> SliderElement with a ">= N" readout, replacing five
    //     coarse ">=N" pills.
    // No new UI component was needed; DropdownElement and SliderElement already existed.
    private const float PolicyDropdownWidth = 78f;

    // Option labels come from UploadPolicy.Format over the SAME state arrays the dropdown indexes into,
    // so a label can never drift from the state it selects.
    private static readonly IReadOnlyList<string> StatsOptions = OptionsFor(StatsStates);
    private static readonly IReadOnlyList<string> ReplayOptions = OptionsFor(ReplayStates);

    private static string[] OptionsFor(UploadPolicyState[] states)
    {
        var labels = new string[states.Length];
        for (var i = 0; i < states.Length; i++) labels[i] = UploadPolicy.Format(states[i]);
        return labels;
    }

    /// <summary>One row per content kind: name + both artifact selectors, under the shared column header.</summary>
    private HudElement KindRow(ContentKind kind)
        => new RowElement(new HudElement[]
        {
            new SpacerElement(Width: 8f),
            new TextElement(() => UploadPolicy.Label(kind), MutedCol, Width: 92f),
            PolicyDropdown(kind, UploadArtifact.Stats),
            PolicyDropdown(kind, UploadArtifact.Replay),
        }, Gap: 6f);

    // `Selected` is read LIVE on every poll — never captured at build time — so the closed dropdown shows
    // the value actually in effect, the same contract the pills documented.
    private HudElement PolicyDropdown(ContentKind kind, UploadArtifact artifact)
    {
        var states = artifact == UploadArtifact.Replay ? ReplayStates : StatsStates;
        var options = artifact == UploadArtifact.Replay ? ReplayOptions : StatsOptions;
        return new DropdownElement(
            () => IndexOfState(states, UploadPolicyFor(kind, artifact)),
            () => options,
            i => { if (i >= 0 && i < states.Length) SetUploadPolicy(kind, artifact, states[i]); },
            Width: PolicyDropdownWidth);
    }

    // Replay has no `manual`, so a persisted Manual would not appear in ReplayStates. InitUploadPolicy's
    // NormalizeReplayManualToOff rewrites it at load, which is what makes the fallback unreachable rather
    // than a silent misreport.
    private static int IndexOfState(UploadPolicyState[] states, UploadPolicyState state)
    {
        for (var i = 0; i < states.Length; i++) if (states[i] == state) return i;
        return 0;
    }

    /// <summary>Multi-select difficulty chips for the one kind being rendered. Labels come from
    /// <see cref="UploadTierFilter.TierLabel"/>, so a raid shows the game's own names (Clash!/Brutal!/…)
    /// over the tier the site actually served.</summary>
    private HudElement TierRow(ContentKind kind, ContentTier[] tiers)
    {
        var kids = new List<HudElement>
        {
            new SpacerElement(Width: 16f),
            new TextElement(() => "tiers", MutedCol, Width: 44f),
        };
        foreach (var t in tiers)
        {
            var tier = t;   // capture per iteration — the closures are read live, long after this loop
            kids.Add(new ButtonElement(
                () => UploadTierFilter.TierLabel(kind, tier),
                () => SetTierEnabled(kind, tier, !TierEnabled(kind, tier)),
                Active: () => TierEnabled(kind, tier),
                Width: TierChipWidth(kind, tier)));
        }
        return new RowElement(kids.ToArray(), Gap: 6f);
    }

    // Per-LABEL widths, not one uniform value: "Backtrack!" is nearly twice "hard", and the raid row is
    // already the widest in the pane. MEASURED with tools/run-ui-sandbox.sh, not eyeballed.
    private static float TierChipWidth(ContentKind kind, ContentTier tier) => (kind, tier) switch
    {
        (ContentKind.Dungeon, ContentTier.Normal) => 62f,
        (ContentKind.Dungeon, ContentTier.Hard) => 52f,
        (ContentKind.Dungeon, ContentTier.Master) => 62f,
        (ContentKind.Raid, ContentTier.Normal) => 58f,      // "Clash!"
        (ContentKind.Raid, ContentTier.Hard) => 62f,        // "Brutal!"
        (ContentKind.Raid, ContentTier.Purge) => 58f,       // "Purge!"
        (ContentKind.Raid, ContentTier.Backtrack) => 84f,   // "Backtrack!"
        _ => 62f,
    };

    /// <summary>Master-level floor. Dungeon-only, because Master is a dungeon-only tier.</summary>
    private HudElement MasterLevelRow()
        => new RowElement(new HudElement[]
        {
            new SpacerElement(Width: 16f),
            new TextElement(() => "level", MutedCol, Width: 44f),
            new SliderElement(() => MinMasterLevel, v => SetMinMasterLevel((int)v),
                              UploadTierFilter.MinMasterLevelFloor, UploadTierFilter.MaxMasterLevel)
                // SquareHandle: Unity's Slider drives the handle's cross-axis anchors to full stretch, so
                // HandleSize ADDS to the row height instead of setting the knob's height — without this the
                // knob renders as a 13x29 capsule in this 16px row (measured 2026-07-30), which reads as
                // oversized. Opt-in per slider, so no other plugin's sliders change (verified: 0 differing
                // pixels on a non-opted slider). Needs Abstractions >= 1.18.0, which the csproj's local
                // framework-source reference provides.
                { Width = 150f, SquareHandle = true },
            new TextElement(() => $"≥ {MinMasterLevel}", MutedCol, Width: 40f),
        }, Gap: 6f);

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
    /// <summary>Multi-select chips for WHICH run-end stages cut an archive. A game run steps
    /// <c>End -> Settlement -> Vote</c> and every one of them used to arm the trigger, so a single run end
    /// cut 1-3 archives depending on whether the Min-gap cooldown happened to have expired between them
    /// (owner report 2026-07-30). Selecting stages explicitly makes that count deterministic.
    ///
    /// <para>Chips rather than a dropdown for the same reason the upload tier axis uses them: the choice is
    /// genuinely MULTI-select. Gated on the parent toggle, matching how PillRow's option rows gate.</para>
    /// </summary>
    private HudElement StageChipRow()
    {
        var kids = new List<HudElement>
        {
            new SpacerElement(Width: 16f),
            new TextElement(() => "stages", MutedCol, Width: 44f),
        };
        foreach (var s in AutoArchive.AutoArchiveEngine.SelectableStages)
        {
            var stage = s;   // capture per iteration — these closures are read live, long after this loop
            kids.Add(new ButtonElement(
                () => stage.ToString(),
                () => SetAutoArchiveStageState(stage, !AutoArchiveStageState(stage)),
                Active: () => AutoArchiveStageState(stage),
                Enabled: () => AutoArchiveStage,
                Width: StageChipWidth(stage)));
        }
        return new RowElement(kids.ToArray(), Gap: 6f);
    }

    // Per-LABEL widths, mirroring the upload tier chips. MEASURED via tools/run-ui-sandbox.sh: the row
    // spans x 65..299 with 104px of slack inside the 366px content box.
    private static float StageChipWidth(DungeonFlowState stage) => stage switch
    {
        DungeonFlowState.End        => 46f,
        DungeonFlowState.Settlement => 78f,
        DungeonFlowState.Vote       => 50f,
        _                           => 56f,
    };

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
