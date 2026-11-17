using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

// Settings › Archive (approved redesign §3): left AUTO ARCHIVE (hint = the last archive, "last: Boss kill · 42 s ago";
// Auto-archive, Min gap, Settle) + HISTORY (Keep runs pill dropdown); right ARCHIVE WHEN (hint "each one splits a run"):
// Team wipe (+ Revive grace, Ignore when solo), Boss phase (+ Keep before), Combat idle (+ Idle timeout), Stage change
// (+ Stages). Children are indented + muted and RECESSED while their parent trigger is off (rule 22). All engine policy
// lives in AutoArchiveEngine; this partial only reads/writes the Plugin.AutoArchive.cs accessors.
public sealed partial class Plugin
{
    private HudElement BuildArchiveTab() => SettingsTabColumn(TwoColumns(
        new[]
        {
            SectionPanel(() => _loc.T("archive.section"), LastArchiveHint,
                SettingRow(() => _loc.T("cfg.archive.auto"), false,
                    ToggleChip(() => AutoArchiveEnabled, v => AutoArchiveEnabled = v),
                    ArchiveHelp("auto", "archive.section", "cfg.archive.auto")),
                SettingRow(() => _loc.T("archive.minGap"), false,
                    SecondsChips(() => AutoArchiveCooldownS, v => AutoArchiveCooldownS = v, null, 5, 10, 30, 60),
                    ArchiveHelp("minGap", "archive.section", "archive.minGap")),
                SettingRow(() => _loc.T("archive.settle"), false,
                    SecondsChips(() => AutoArchiveSettleS, v => AutoArchiveSettleS = v, null, 0, 1, 2, 5),
                    ArchiveHelp("settle", "archive.section", "archive.settle"))),
            SectionPanel(() => _loc.T("settings.section.history"), null,
                SettingRow(() => _loc.T("settings.history.keepRuns"), false,
                    PillDropdown(() => IndexOfRetention(HistoryRetention), () => RetentionOptionLabels,
                        i => { if (i >= 0 && i < RetentionOptions.Length) HistoryRetention = RetentionOptions[i]; }, 84f),
                    ArchiveHelp("keepRuns", "settings.section.history", "settings.history.keepRuns"))),
        },
        new[]
        {
            SectionPanel(() => _loc.T("cfg.archive.when"), () => _loc.T("cfg.archive.when.hint"),
                SettingRow(() => _loc.T("archive.teamWipe"), false,
                    ToggleChip(() => AutoArchiveWipe, v => AutoArchiveWipe = v), WhenHelp("wipe", "archive.teamWipe")),
                SettingRow(() => _loc.T("archive.reviveGrace"), true,
                    SecondsChips(() => AutoArchiveWipeGraceS, v => AutoArchiveWipeGraceS = v, () => AutoArchiveWipe, 0, 2, 5),
                    WhenHelp("reviveGrace", "archive.reviveGrace")),
                SettingRow(() => _loc.T("archive.ignoreSolo"), true,
                    ToggleChip(() => AutoArchiveWipeIgnoreSolo, v => AutoArchiveWipeIgnoreSolo = v, () => AutoArchiveWipe),
                    WhenHelp("ignoreSolo", "archive.ignoreSolo")),
                SettingRow(() => _loc.T("archive.bossPhase"), false,
                    ToggleChip(() => AutoArchiveBoss, v => AutoArchiveBoss = v), WhenHelp("boss", "archive.bossPhase")),
                SettingRow(() => _loc.T("archive.keepBefore"), true,
                    SecondsChips(() => AutoArchiveKeepBeforeS, v => AutoArchiveKeepBeforeS = v, () => AutoArchiveBoss, 0, 3, 5),
                    WhenHelp("keepBefore", "archive.keepBefore")),
                SettingRow(() => _loc.T("archive.combatIdle"), false,
                    ToggleChip(() => AutoArchiveIdle, v => AutoArchiveIdle = v), WhenHelp("idle", "archive.combatIdle")),
                SettingRow(() => _loc.T("archive.idleTimeout"), true,
                    SecondsChips(() => AutoArchiveIdleTimeoutS, v => AutoArchiveIdleTimeoutS = v, () => AutoArchiveIdle, 30, 60, 120, 300),
                    WhenHelp("idleTimeout", "archive.idleTimeout")),
                SettingRow(() => _loc.T("cfg.archive.stageChange"), false,
                    ToggleChip(() => AutoArchiveStage, v => AutoArchiveStage = v), WhenHelp("stage", "cfg.archive.stageChange")),
                // WHICH run-end stages cut an archive (multi-select): a run steps End → Settlement → Vote and every one
                // used to arm the trigger, so one run end cut 1–3 archives depending on the Min-gap cooldown (owner
                // report 2026-07-30). Selecting stages makes that count deterministic.
                SettingRow(() => _loc.T("cfg.archive.stages"), true, StageChips(), WhenHelp("stages", "cfg.archive.stages"))),
        }));

    private HudElement ArchiveHelp(string id, string sectionKey, string titleKey)
        => HelpDot("archive." + id, sectionKey, titleKey, "help.archive." + id);
    private HudElement WhenHelp(string id, string titleKey) => HelpDot("when." + id, "cfg.archive.when", titleKey, "help.when." + id);

    // A choice group of second values ("Off" for 0, "2m" from 120 up). `get` is read LIVE on every poll — never a value
    // captured at build time — so the lit chip tracks whichever value is in effect. `enabled` gates the whole group on
    // its parent trigger (recessed while off).
    private HudElement SecondsChips(Func<int> get, Action<int> set, Func<bool>? enabled, params int[] seconds)
    {
        var chips = new HudElement[seconds.Length];
        for (int i = 0; i < seconds.Length; i++)
        {
            int sec = seconds[i];
            chips[i] = ChoiceChip(() => SecondsLabel(sec), () => get() == sec, () => set(sec), enabled);
        }
        return new RowElement(chips, Gap: 6f);
    }

    // "5s" / "2m" / "Off" (localized). A chip's label only changes with the language, so it is formatted once per
    // (value, language) — never per refresh.
    private readonly Dictionary<int, (string Lang, string Text)> _secondsLabels = new();

    private string SecondsLabel(int sec)
    {
        string lang = _loc.Language;
        if (_secondsLabels.TryGetValue(sec, out var c) && ReferenceEquals(c.Lang, lang)) return c.Text;
        string text = sec == 0 ? _loc.T("common.off")
            : sec >= 120 && sec % 60 == 0 ? _loc.TFormat("cfg.minutesShort", sec / 60)
            : _loc.TFormat("cfg.secondsShort", sec);
        _secondsLabels[sec] = (lang, text);
        return text;
    }

    private HudElement StageChips()
    {
        var stages = AutoArchive.AutoArchiveEngine.SelectableStages;
        var chips = new HudElement[stages.Length];
        for (int i = 0; i < stages.Length; i++)
        {
            var stage = stages[i];   // capture per iteration — these closures are read live, long after this loop
            string key = stage switch
            {
                DungeonFlowState.End        => "cfg.stage.end",
                DungeonFlowState.Settlement => "cfg.stage.settlement",
                DungeonFlowState.Vote       => "cfg.stage.vote",
                _                           => "cfg.stage.other",
            };
            chips[i] = ChoiceChip(() => _loc.T(key), () => AutoArchiveStageState(stage),
                () => SetAutoArchiveStageState(stage, !AutoArchiveStageState(stage)), () => AutoArchiveStage);
        }
        return new RowElement(chips, Gap: 6f);
    }

    // "last: Boss kill · 42 s ago" — re-formatted only when the reason or the whole-second age changes (≤ 1 Hz).
    private AutoArchive.ArchiveReason? _lastHintReason;
    private long _lastHintSec = -1;
    private string? _lastHintLang;
    private string _lastHintText = "";

    private string LastArchiveHint()
    {
        string lang = _loc.Language;
        if (LastArchive is not { } la)
        {
            if (_lastHintReason == null && _lastHintSec == -2 && ReferenceEquals(lang, _lastHintLang)) return _lastHintText;
            _lastHintReason = null; _lastHintSec = -2; _lastHintLang = lang;
            return _lastHintText = _loc.T("cfg.archive.last.none");
        }
        long sec = Math.Max(0, (_services.CombatSnapshot.ServerNowMs - la.ms) / 1000);
        if (_lastHintReason == la.reason && sec == _lastHintSec && ReferenceEquals(lang, _lastHintLang)) return _lastHintText;
        _lastHintReason = la.reason; _lastHintSec = sec; _lastHintLang = lang;
        return _lastHintText = _loc.TFormat("cfg.archive.last", ArchiveReasonLabel(la.reason), sec);
    }

    /// <summary>Player-facing name of an archive reason ("Boss kill", "Wipe", …) — localized. The stored trigger tags
    /// (<see cref="ArchiveReasonTag"/>) stay the wire format; this is display only (History can reuse it).</summary>
    internal string ArchiveReasonLabel(AutoArchive.ArchiveReason r) => _loc.T("archive.reason." + ArchiveReasonTag(r));

    // Local-history retention (owner 2026-08-15): a DROPDOWN — five values (50..250) as pills clipped the row, and a
    // select fits any count in fixed width. Options are run COUNTS. A hand-edited value outside the presets snaps the
    // closed dropdown to its nearest.
    private static readonly int[] RetentionOptions = { MinRetention, 100, 150, 200, MaxRetention };
    private static readonly string[] RetentionOptionLabels = { "50", "100", "150", "200", "250" };

    // Closed-dropdown selection: the exact preset, else the nearest preset at or below the current value
    // (a hand-edited 130 shows as 100), never -1. Pure/static — pinned by HistorySearchAndRetentionTests.
    internal static int IndexOfRetention(int value)
    {
        var idx = 0;
        for (var i = 0; i < RetentionOptions.Length; i++)
            if (RetentionOptions[i] == value) return i;
            else if (RetentionOptions[i] <= value) idx = i;
        return idx;
    }
}
