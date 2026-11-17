using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.CombatMeter.LogUpload;

namespace Stellar.CombatMeter;

// Settings › Uploads (approved redesign §3). Left: UPLOADS TO STELLARLOGS — one row per content kind carrying BOTH
// artifacts under a shared CONTENT / RUN STATS / REPLAY header (design 3d73f7a: 35 pills → 10 dropdowns), indented
// difficulty chips for the kinds that have that axis, the Master level floor under Dungeons, and the footer feedback +
// "Refresh content list". Right: DISCORD WEBHOOK and (testing-channel builds only) TESTING SERVER.
// Alignment (option A, approved 2026-11-17): no "Tiers" word — the chips and the "Master level" row start ChildIndent in
// under the content name and the chips use the full column width (all four raid chips on one line in English); the
// RUN STATS / REPLAY captions are centred over their dropdowns; "Refresh content list" spans exactly both dropdown
// columns (the row's right edge = the dropdowns' right edge, so equal width puts both edges flush).
//
// Control per axis (unchanged): auto/manual/off is mutually exclusive → a pill dropdown; tiers are genuinely
// MULTI-select → chips; the master level is ordinal 1..20 → a slider with an "N+" readout.
// Run stats are TRI-state; Replay is TWO-state (auto/off) — owner ruling 2026-07-28: a `manual` replay cell cannot upload
// on any path (the retained re-upload payload takes its positions from the archive-time doc, null under `manual`).
public sealed partial class Plugin
{
    private const float PolicyStatsW  = 90f;
    private const float PolicyReplayW = 84f;
    private const float PolicyColGap  = 6f;    // SettingRow's gap between the two dropdowns
    private const float UploadsSubLabelW = 80f;   // "Post for" label column (Discord chips)
    private const float MasterLevelLabelW = 108f; // "Master level" in the longest locale (fil "Antas ng Master" ≈ 105 regular-14)

    private static readonly UploadPolicyState[] StatsStates  = { UploadPolicyState.Auto, UploadPolicyState.Manual, UploadPolicyState.Off };
    private static readonly UploadPolicyState[] ReplayStates = { UploadPolicyState.Auto, UploadPolicyState.Off };

    private HudElement BuildUploadsTab() => SettingsTabColumn(TwoColumns(
        new[] { UploadsPolicyPanel() },
        new[] { DiscordPanel(), UploadTargetSection() }));

    // ── UPLOADS TO STELLARLOGS ────────────────────────────────────────────────────────────────────────────────────
    private HudElement UploadsPolicyPanel()
    {
        // Option labels are built here, in the window's language: the window is rebuilt on a language change.
        var statsOptions  = PolicyOptionLabels(StatsStates);
        var replayOptions = PolicyOptionLabels(ReplayStates);
        var rows = new List<HudElement>
        {
            // Sticky "update the plugin" line — only while this build is below the server upload floor
            // (Plugin.UploadCompat.cs). Amber: an action is needed; it persists until the launcher updates the plugin.
            new ConditionalElement(() => UploadBelowFloor,
                FixedLine(new TextElement(() => UploadFloorNoticeLine, Color: () => CautionTextOnPanel()) { MaxLines = 2 }, 36f)),
            // The column header names both axes ONCE.
            new RowElement(new HudElement[]
            {
                new SpacerElement(0f, 16f),
                new CellElement(UploadsColumnCaption("cfg.uploads.col.content", 0f), Weight: 1f),
                UploadsColumnCaption("cfg.uploads.col.runStats", PolicyStatsW, TextAlign.Center),
                UploadsColumnCaption("cfg.uploads.col.replay", PolicyReplayW, TextAlign.Center),
            }, Gap: PolicyColGap),
        };
        foreach (var kind in UploadPolicyTable.Kinds)
        {
            var k = kind;
            rows.Add(SettingRow(() => _loc.T(UploadPolicy.Label(k)), false,
                PolicyDropdown(k, UploadArtifact.Stats, statsOptions), PolicyDropdown(k, UploadArtifact.Replay, replayOptions)));
            // The difficulty axis exists only for the kinds that HAVE one (TiersFor is that authority).
            if (UploadTierFilter.TiersFor.TryGetValue(k, out var tiers)) rows.Add(TierChips(k, tiers));
            // Master is a dungeon-only tier, so its level floor belongs under the dungeon chips.
            if (k == ContentKind.Dungeon) rows.Add(MasterLevelRow());
        }
        // The content list (which maps count as dungeon/raid/world boss) is fetched ONCE and cached, re-fetched only when
        // the plugin updates — every request to the site's Worker is billed, so no polling (owner ruling 2026-07-28).
        // This button is the escape hatch for a content patch that lands without a plugin release.
        rows.Add(new RowElement(new HudElement[]
        {
            new SpacerElement(0f, PillHeight),
            new CellElement(new TextElement(UploadsNoteText, Color: () => UploadsNoteColor(), NoWrap: true) { Ellipsize = true }, Weight: 1f),
            // Total width = both dropdown columns + their gap (PillButton = labelW + 2 × PillPadX); both rows end at the
            // panel's inner right edge (no trailing "?" on the policy rows), so the button's edges sit flush with them.
            NeutralButton(() => _loc.T("upload.refreshContent"), OnRefreshContentClicked,
                PolicyStatsW + PolicyColGap + PolicyReplayW - 2f * PillPadX),
        }, Gap: 8f));
        return SectionPanelWithTrailing(() => _loc.T("cfg.uploads.title"), () => _loc.T("cfg.uploads.hint"),
            HelpDot("uploads.policy", "cfg.uploads.title", "cfg.uploads.title", "help.uploads.policy"), rows.ToArray());
    }

    private HudElement UploadsColumnCaption(string key, float width, TextAlign align = TextAlign.Left)
        => new TextElement(() => _loc.T(key), Color: () => MutedColor(), Width: width, Align: align, NoWrap: true, FontSize: 11) { Bold = true };

    private string[] PolicyOptionLabels(UploadPolicyState[] states)
    {
        var labels = new string[states.Length];
        for (var i = 0; i < states.Length; i++)
            labels[i] = _loc.T(states[i] switch
            {
                UploadPolicyState.Manual => "cfg.policy.manual",
                UploadPolicyState.Off    => "cfg.policy.off",
                _                        => "cfg.policy.auto",
            });
        return labels;
    }

    // `Selected` is read LIVE on every poll — never captured at build time — so the closed dropdown shows the value
    // actually in effect.
    private HudElement PolicyDropdown(ContentKind kind, UploadArtifact artifact, string[] options)
    {
        var states = artifact == UploadArtifact.Replay ? ReplayStates : StatsStates;
        return PillDropdown(
            () => IndexOfState(states, UploadPolicyFor(kind, artifact)),
            () => options,
            i => { if (i >= 0 && i < states.Length) SetUploadPolicy(kind, artifact, states[i]); },
            artifact == UploadArtifact.Replay ? PolicyReplayW : PolicyStatsW);
    }

    // Replay has no `manual`, so a persisted Manual would not appear in ReplayStates. InitUploadPolicy's
    // NormalizeReplayManualToOff rewrites it at load, which makes the fallback unreachable rather than a misreport.
    private static int IndexOfState(UploadPolicyState[] states, UploadPolicyState state)
    {
        for (var i = 0; i < states.Length; i++) if (states[i] == state) return i;
        return 0;
    }

    // Multi-select difficulty chips for one kind (indented under the content name, no label, wrapping). A raid shows the game's own names
    // (Clash! / Brutal! / Purge! / Backtrack!) for the same stored tiers as a dungeon's Normal / Hard.
    private HudElement TierChips(ContentKind kind, ContentTier[] tiers)
    {
        var chips = new List<HudElement>(tiers.Length);
        var labels = new List<string>(tiers.Length);
        foreach (var t in tiers)
        {
            var tier = t;   // capture per iteration — the closures are read live, long after this loop
            string key = TierLabelKey(kind, tier);
            labels.Add(_loc.T(key));
            chips.Add(ChoiceChip(() => _loc.T(key), () => TierEnabled(kind, tier),
                () => SetTierEnabled(kind, tier, !TierEnabled(kind, tier))));
        }
        return ChipFlow(ChildIndent, null, 0f, chips, labels, SettingsColInnerW);
    }

    private static string TierLabelKey(ContentKind kind, ContentTier tier) => (kind, tier) switch
    {
        (ContentKind.Raid, ContentTier.Normal)    => "cfg.tier.raid.normal",
        (ContentKind.Raid, ContentTier.Hard)      => "cfg.tier.raid.hard",
        (ContentKind.Raid, ContentTier.Purge)     => "cfg.tier.raid.purge",
        (ContentKind.Raid, ContentTier.Backtrack) => "cfg.tier.raid.backtrack",
        (_, ContentTier.Hard)                     => "cfg.tier.hard",
        (_, ContentTier.Master)                   => "cfg.tier.master",
        _                                         => "cfg.tier.normal",
    };

    // Master-level floor (dungeon-only): "Master level" + a round-knob slider + bold "N+" ("≥" is missing from the game
    // font and drew as nothing, so "≥ 1" read "1").
    private readonly FmtCache _masterLevelFmt = new();
    // Column inner width − [indent 6 + label + "N+" 36 + 4 gaps × 6] → the slider takes exactly the rest of the line.
    private const float MasterSliderW = SettingsColInnerW - (ChildIndent - 6f) - MasterLevelLabelW - 36f - 4f * 6f;

    private HudElement MasterLevelRow() => new RowElement(new HudElement[]
    {
        new SpacerElement(0f, SettingRowH),
        new SpacerElement(ChildIndent - 6f, 1f),
        new TextElement(() => _loc.T("cfg.uploads.masterLevel"), Color: () => MutedColor(), Width: MasterLevelLabelW, NoWrap: true) { Ellipsize = true },
        new CellElement(RoundSlider(() => MinMasterLevel, v => SetMinMasterLevel((int)MathF.Round(v)),
            UploadTierFilter.MinMasterLevelFloor, UploadTierFilter.MaxMasterLevel) with { Width = MasterSliderW }, Width: MasterSliderW),
        new TextElement(() => _masterLevelFmt.Get("{0}+", MinMasterLevel), Color: () => TextColor(), Width: 36f,
            Align: TextAlign.Right, NoWrap: true, FontSize: 13) { Bold = true },
    }, Gap: 6f);

    // ── content-list refresh feedback (rule 13: the answer appears next to the button) ──────────────────────────
    private string _uploadsNote = "";
    private bool   _uploadsNoteOk = true, _uploadsNotePending;
    private double _uploadsNoteAt = double.NegativeInfinity;
    private const float UploadsNoteDurS = 3.0f;

    private void OnRefreshContentClicked()
    {
        _uploadsNote = _loc.T("cfg.uploads.checking");
        _uploadsNoteOk = true; _uploadsNotePending = true;
        _uploadsNoteAt = _services.Framework.TimeNow;
        RefreshContentKindsNow();
    }

    // Main thread (DrainContentKindsNotice, Plugin.UploadPolicy.cs): the fetch result for a user-requested refresh.
    private void ShowUploadsNote(string text, bool ok)
    {
        _uploadsNote = text; _uploadsNoteOk = ok; _uploadsNotePending = false;
        _uploadsNoteAt = _services.Framework.TimeNow;
    }

    private bool UploadsNoteShowing() => _uploadsNotePending || _services.Framework.TimeNow - _uploadsNoteAt < UploadsNoteDurS;
    private string UploadsNoteText() => UploadsNoteShowing() ? _uploadsNote : "";
    private ColorRgba UploadsNoteColor() => _uploadsNotePending ? MutedColor() : _uploadsNoteOk ? SuccessText() : CautionTextOnPanel();

    // ── DISCORD WEBHOOK ───────────────────────────────────────────────────────────────────────────────────────────
    // Master toggle + webhook URL + per-content chips + test buttons (spec 2026-08-15-combatmeter-discord-webhook-share).
    // The URL persists live (OnChange) so a pasted URL is usable by the test button without pressing Enter first.
    private HudElement DiscordPanel()
    {
        var kinds = (ContentKind[])Enum.GetValues(typeof(ContentKind));
        var chips = new List<HudElement>(kinds.Length);
        var labels = new List<string>(kinds.Length);
        foreach (var kind in kinds)
        {
            var k = kind;
            string key = k == ContentKind.Vault ? "cfg.discord.vault" : UploadPolicy.Label(k);
            labels.Add(_loc.T(key));
            chips.Add(ChoiceChip(() => _loc.T(key), () => DiscordContentFor(k), () => SetDiscordContent(k, !DiscordContentFor(k)),
                () => DiscordEnabled));
        }
        const float testLabelW = SettingsColInnerW / 2f - 4f - 2f * PillPadX;
        return SectionPanelWithTrailing(() => _loc.T("discord.section"), () => _loc.T("cfg.discord.hint"),
            HelpDot("discord", "discord.section", "discord.section", "help.discord"),
            SettingRow(() => _loc.T("common.enabled"), false, ToggleChip(() => DiscordEnabled, v => DiscordEnabled = v)),
            new RowElement(new HudElement[]
            {
                new SpacerElement(0f, SettingRowH),
                new InputElement(() => DiscordWebhookUrl, s => DiscordWebhookUrl = s, SettingsColInnerW - IconButtonSize - 2f * 6f,
                    OnChange: s => DiscordWebhookUrl = s) { SingleLine = true, Placeholder = () => _loc.T("cfg.discord.urlPlaceholder") },
                IconButton(() => "✕", () => DiscordWebhookUrl = "", remove: true),
            }, Gap: 6f),
            FixedLine(new TextElement(DiscordUrlStatus, Color: () => DiscordUrlStatusColor(), NoWrap: true) { Ellipsize = true }, 20f),
            ChipFlow(0f, () => _loc.T("cfg.discord.postFor"), UploadsSubLabelW, chips, labels, SettingsColInnerW),
            PillPair(NeutralButton(() => _loc.T("discord.testMessage"), SendDiscordTest, testLabelW),
                     NeutralButton(() => _loc.T("discord.testCard"), SendDiscordCardTest, testLabelW)),
            FixedLine(new TextElement(() => DiscordTestResult, Color: () => DiscordTestOk ? SuccessText() : MutedColor(), NoWrap: true)
                { Ellipsize = true }, 20f));
    }

    // Validity of the stored URL, re-checked only when the string changes (IsValidWebhookUrl parses a Uri).
    private string? _discordUrlChecked;
    private bool _discordUrlValid;

    private bool DiscordUrlValid()
    {
        var url = DiscordWebhookUrl;
        if (!ReferenceEquals(url, _discordUrlChecked))
        {
            _discordUrlChecked = url;
            _discordUrlValid = !string.IsNullOrWhiteSpace(url) && DiscordLink.IsValidWebhookUrl(url);
        }
        return _discordUrlValid;
    }

    private string DiscordUrlStatus()
        => string.IsNullOrWhiteSpace(DiscordWebhookUrl) ? _loc.T("cfg.discord.url.empty")
         : DiscordUrlValid() ? _loc.T("cfg.discord.url.valid") : _loc.T("cfg.discord.url.invalid");

    private ColorRgba DiscordUrlStatusColor()
        => string.IsNullOrWhiteSpace(DiscordWebhookUrl) ? MutedColor() : DiscordUrlValid() ? SuccessText() : ErrorText();

    // ── TESTING SERVER (testing-channel builds only; Plugin.UploadTarget.cs) ─────────────────────────────────────
    private HudElement UploadTargetSection()
        => BuildUploadTargetSection(_isTestingBuildGate,
            SectionPanel(() => _loc.T("cfg.testing.title"), () => _loc.T("cfg.testing.hint"),
                SettingRow(() => _loc.T("cfg.testing.toggle"), false,
                    ToggleChip(() => UploadTargetIsTesting, SetUploadTargetTesting),
                    HelpDot("testing", "cfg.testing.title", "cfg.testing.toggle", "settings.uploadTarget.hint")),
                // Always present (no jump): muted "applies after a relaunch", amber once a change is saved.
                FixedLine(new TextElement(() => _loc.T(_uploadTargetPendingRelaunch ? "settings.uploadTarget.relaunch" : "cfg.testing.note"),
                    Color: () => _uploadTargetPendingRelaunch ? CautionTextOnPanel() : MutedColor(), NoWrap: true) { Ellipsize = true }, 20f)));
}
