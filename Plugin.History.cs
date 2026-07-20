using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.CombatMeter.Replay;

namespace Stellar.CombatMeter;

/// <summary>Frozen per-source time-series (one archived encounter), one array per channel.</summary>
internal struct SourceSeries
{
    public int    BucketMs;
    public long[] Dealt;
    public long[] Healing;
    public long[] Taken;
}

/// <summary>A single killing-blow record: when (epoch ms), who died, and the attacker skill id.</summary>
internal readonly record struct DeathEntry(long Ms, EntityId Victim, int Skill);

/// <summary>One battle-imagine cast: when (epoch ms), who, and the BASE imagine skill id.</summary>
internal readonly record struct ImagineCastEntry(long Ms, EntityId Source, int Skill);

public sealed partial class Plugin
{
    private const int HistoryCapacity = 50;
    private readonly List<EncounterHistoryEntry> _history = new();
    private string? _lastSceneName;

    internal sealed class EncounterHistoryEntry
    {
        public string?  SceneName;
        public long     EnteredAtMs;
        public long     ArchivedAtMs;
        public long     CombatDurationMs;
        public Dictionary<EntityId, SourceStats> Stats = new();
        public Dictionary<EntityId, SourceSeries> Series = new();   // NEW
        public List<DeathEntry> DeathLog = new();   // complete killing-blow list (truncation-independent)
        public List<ImagineCastEntry> ImagineCasts = new();   // imagine casts w/ true ms (truncation-independent)
        public Dictionary<EntityId, EntitySnapshot> Entities = new();   // per-player frozen entity snapshot (issue #5)
        public PartyType PartyType;
        public int       MemberCount;
        public long      LevelUuid;        // snapshotted at archive (IDungeonState.CurrentRunId) for deferred upload
        public int       PassTime;         // settlement clear-time seconds at archive
        public int       MasterModeScore;  // settlement master-mode MAX/PAR score (master_mode_score) at archive
        public int       TotalScore;       // achieved DungeonScore.total_score at archive (numerator of "686/700")
        // Raw DungeonSceneInfo.difficulty (IDungeonState.CurrentDifficulty), snapshotted at archive.
        // Semantic UNCONFIRMED (1-20 challenge level vs. tier enum) — 0 when absent/not seen.
        public int       DifficultyLevel;
        // Server epoch ms when the in-game dungeon run-timer started (IDungeonState.RunTimerStartMs),
        // snapshotted at archive. 0 when unknown (no run timer seen / open world).
        public long      DungeonStartMs;
        public string    Result = "partial"; // "kill" once settled, else "partial"
        // IDungeonState.LastDefeatedCount snapshotted at archive — 0 until the attr feeding it is wired.
        public int       Defeated;
        // Why this segment was archived ("manual"|"scene"|"wipe"|"boss"|"idle"|"stage") — v10.
        public string   Trigger = "manual";
        // NOTE: per-entry upload state (phase + run URL) is NOT stored on the entry — it persists as a
        // SIDECAR "uploadStates" key in the history config section (Plugin.HistoryStore.cs), keyed by the
        // stable (LevelUuid, ArchivedAtMs) composite, so the entry JSON stays byte-identical to what older
        // builds wrote. That keeps a rollback to a prior (v10) DLL from reading these entries as malformed
        // and silently wiping the owner's irreplaceable history.
    }

    private void OnSceneChanged(string? newScene)
    {
        // Arm the replay-probe settle gate (Plugin.Replay.cs): a scene change = a mass entity
        // teardown/rebuild, during which probing a live transform can hit a freed IL2CPP model.
        _lastSceneChangeMs = _services.CombatSnapshot.ServerNowMs;
        if (_lastSceneName is null)
        {
            _lastSceneName = newScene;
            return;
        }

        // Capture pre-archive state for the diagnostics line below (ManualArchive may reset the
        // capture when the outgoing scene had combat).
        var archived = _stats.Count > 0;
        var samplesAtReset = _replay?.TotalSamples ?? 0;

        // Auto-archive on scene change. ManualArchive() is the single source of
        // truth for the snapshot-and-clear flow; the Archive button calls it too.
        ManualArchive(AutoArchive.ArchiveReason.SceneChange);

        // Scene-boundary replay reset — now CONDITIONAL (spec 2026-07-19): the provisional
        // candidate->candidate hop (raid lobby -> boss room before the run-id latches) keeps
        // the buffer so the lobby movement survives into the run's replay. Every other
        // boundary resets, preserving the 93:53 cross-scene-carryover protection: entering a
        // candidate from town starts fresh, leaving to town discards, and committed runs keep
        // per-segment archives. When the outgoing scene HAD combat, ManualArchive above
        // already uploaded + reset — this is then a harmless no-op either way.
        var incomingCandidate = ResolveSceneCandidate(newScene);
        var reset = ReplayCaptureGate.ShouldResetOnSceneChange(
            _services.Dungeon.CurrentRunId, _sceneIsCandidate, incomingCandidate);
        if (reset) ResetReplay();
        LogReplaySceneReset(_lastSceneName, newScene, samplesAtReset, archived, kept: !reset);
        _sceneIsCandidate = incomingCandidate;

        _lastSceneName = newScene;
    }

    internal void ManualArchive() => ManualArchive(AutoArchive.ArchiveReason.Manual);

    // Snapshot the active _stats into history and reset the live meter. No-op when there's
    // nothing to archive. Callers: OnSceneChanged (scene), the Archive button/hotkey (manual),
    // and TickAutoArchiveTriggers (wipe/boss/idle/stage). Every archive — whatever the path —
    // reports into the AutoArchiveEngine so the shared 10 s cooldown spans them all.
    internal void ManualArchive(AutoArchive.ArchiveReason reason)
    {
        // Any archive that actually enters this method — the manual button/hotkey, a scene change,
        // OR the deferred AUTO fire itself — supersedes a still-pending settle-delayed auto archive
        // (Plugin.AutoArchive.cs). Clearing here means a manual/scene archive during the ~1 s wait
        // wins outright and a stale deferred StageChange can never double-fire on already-cleared
        // stats afterward. The deferred fire calls ManualArchive too, so it self-clears the slot.
        _pendingArchiveReason = null;

        if (_stats.Count == 0) { LogArchiveOutcome(reason, "skip-empty", 0, 0); return; }

        // Content-based junk suppression (owner ruling 2026-07-19, verbatim: "junk = when nothing
        // happen DPS=0, HPS=0, TAKEN=0. and even I do nothing and all other player keep having
        // DPS/HPS/TAKEN update it's not junk too"). Bin an AUTO archive ONLY when it carries no fresh
        // run result AND every stat row is all-zero. ANY nonzero activity — even a lone single-
        // participant instant hit — BANKS as its own entry (no participant-count / span floor); a
        // fresh kill/settlement tail ALWAYS saves (the destroyed-kill-tail bug this guards). A MANUAL
        // (button/hotkey) archive is never suppressed. carriesFreshResult uses IsFreshKill (baseline-
        // relative — a stale run-level result from an earlier segment does NOT count).
        var carriesFreshResult = IsFreshKill(_services.Dungeon.LastSettlement, _settlementAtCombatStart);
        if (ShouldSuppressAutoArchive(reason, carriesFreshResult, AllRowsZero()))
        {
            // Suppression BINS the entry but is now a total no-op on state (owner ruling 2026-07-19,
            // run 206630597437685760): the old Clear() here erased accumulated state before the real
            // fight → the local player showed 0 damage for the whole run. Everything (rows/actors +
            // combat clocks + baselines) CARRIES forward unconditionally and folds into the next
            // banked entry (all-zero pre-fight actors then appear there — the owner's intent). Because
            // _combatActive stays true, EnsureCombatStarted's guard keeps _settlementAtCombatStart
            // anchored at the true combat start (no re-snapshot, no stale-kill misattribution). The
            // shared-cooldown OnArchived bookkeeping + the ungated outcome log still fire as before.
            LogArchiveOutcome(reason, "suppressed", _stats.Count, ComputeDurationMs());
            _autoArchive.OnArchived(_services.CombatSnapshot.ServerNowMs, reason);
            return;
        }

        var entry = BuildHistoryEntry(reason);
        _history.Add(entry);
        foreach (var evicted in TrimToCapacity(_history)) _uploadStatus.Forget(evicted);   // unroot evicted runs
        SaveHistory();   // persist on every archive + eviction (a user/scene event, not a hot-path frame)

        var summaryFired = FinalizeAndMaybeUploadReplay(entry);
        LogArchiveOutcome(reason, summaryFired ? "banked+upload" : "banked", entry.Stats.Count, entry.CombatDurationMs);
        if (reason == AutoArchive.ArchiveReason.Manual) NotifyManualArchived(entry.CombatDurationMs);

        _autoArchive.OnArchived(_services.CombatSnapshot.ServerNowMs, reason);
        Clear();
    }

    // Replay delta-window upload wiring (owner design 2026-07-19), extracted so ManualArchive stays
    // under the 50-LoC cap. EVERY banked archive ships the window (watermark, now]: there is no
    // ShouldFinalizeReplay gate (retired — the recorder never stops, so no run-terminal concept) and
    // no sub-3s fragment gate (retired — contiguous windows stitch on the site, short tails are safe).
    // PrepareReplayDoc returns null for an off / no-level / EMPTY window, in which case nothing uploads
    // and the watermark holds. On a successful hand-off to the upload queue the watermark advances and
    // the window's samples are freed; a failed hand-off keeps them so they merge into the next window
    // (at-least-once, owner default 2). Returns whether a SUMMARY upload fired.
    private bool FinalizeAndMaybeUploadReplay(EncounterHistoryEntry entry)
    {
        var replayDoc = PrepareReplayDoc(entry);
        if (replayDoc is null) return false;   // empty/off/no-level window — watermark unchanged
        var summaryFired = MaybeUploadLog(entry, replayDoc);
        // summaryFired → the summary callback OWNS + uploads the doc (synchronous hand-off complete);
        // otherwise upload it directly here. A non-throwing hand-off (either path) advances the watermark.
        var handedOff = summaryFired || UploadReplayDoc(replayDoc);
        if (handedOff) AdvanceReplayWatermark();
        return summaryFired;
    }

    // Entry assembly, extracted so ManualArchive stays under the 50-LoC cap. The run-identity
    // snapshot rationale (sticky LastSettlement vs fresh-kill baseline) is documented on
    // IsFreshKill below and _settlementAtCombatStart's declaration.
    private EncounterHistoryEntry BuildHistoryEntry(AutoArchive.ArchiveReason reason)
    {
        var settlement = _services.Dungeon.LastSettlement;
        var freshSettlement = IsFreshKill(settlement, _settlementAtCombatStart) ? settlement : null;
        return new EncounterHistoryEntry
        {
            SceneName        = _lastSceneName,
            EnteredAtMs      = _combatStartMs,
            ArchivedAtMs     = _services.CombatSnapshot.ServerNowMs,
            CombatDurationMs = ComputeDurationMs(),
            Stats            = DeepCopyStats(),
            Series           = FreezeTimelines(),
            DeathLog         = new List<DeathEntry>(_deaths),
            ImagineCasts     = new List<ImagineCastEntry>(_imagineCasts),
            Entities         = SnapshotEntities(),
            PartyType        = _services.PartySnapshot.PartyType,
            MemberCount      = _stats.Count,
            LevelUuid        = _services.Dungeon.CurrentRunId != 0 ? _services.Dungeon.CurrentRunId : _lastRunId,
            PassTime         = freshSettlement?.PassTimeSeconds ?? 0,
            MasterModeScore  = freshSettlement?.MasterModeScore ?? 0,
            TotalScore       = freshSettlement?.TotalScore ?? 0,
            DifficultyLevel  = Math.Max(_difficultyAtCombatStart, _services.Dungeon.CurrentDifficulty),
            DungeonStartMs   = _services.Dungeon.RunTimerStartMs,
            Result           = ResolveVerdict(freshSettlement, _services.Dungeon.LastOutcome),
            Defeated         = _services.Dungeon.LastDefeatedCount,
            Trigger          = ArchiveReasonTag(reason),
        };
    }

    internal static string ArchiveReasonTag(AutoArchive.ArchiveReason r) => r switch
    {
        AutoArchive.ArchiveReason.SceneChange => "scene",
        AutoArchive.ArchiveReason.Wipe        => "wipe",
        AutoArchive.ArchiveReason.BossPhase   => "boss",
        AutoArchive.ArchiveReason.Idle        => "idle",
        AutoArchive.ArchiveReason.StageChange => "stage",
        _                                     => "manual",
    };

    /// <summary>True when an AUTO-triggered archive is junk and should be skipped. Suppressed iff it
    /// is NOT a <see cref="AutoArchive.ArchiveReason.Manual"/> archive (manual is always kept),
    /// carries no fresh run result (<paramref name="carriesFreshResult"/> — a fresh kill/settlement
    /// earned by THIS encounter always saves), AND every stat row is 0/0/0
    /// (<paramref name="allRowsZero"/>). Junk is defined by CONTENT alone (owner ruling 2026-07-19,
    /// verbatim): "junk = when nothing happen DPS=0, HPS=0, TAKEN=0. and even I do nothing and all
    /// other player keep having DPS/HPS/TAKEN update it's not junk too." ANY nonzero row — even a
    /// single participant with a lone instant hit — is real activity and BANKS as its own entry
    /// (there is no participant-count or span floor). Combined with the suppressed-archives-never-
    /// wipe rule, an all-zero suppressed archive is a total no-op: its zero rows/actors carry
    /// untouched into the next banked entry.</summary>
    internal static bool ShouldSuppressAutoArchive(
        AutoArchive.ArchiveReason reason, bool carriesFreshResult, bool allRowsZero)
        => reason != AutoArchive.ArchiveReason.Manual
        && !carriesFreshResult
        && allRowsZero;

    // True when every archived stat row is empty — no damage dealt, no healing, no damage taken —
    // i.e. a genuinely empty encounter that must not be saved (owner: "shouldn't save empty into
    // history"). Only reached with _stats.Count > 0 (the skip-empty early-out handles the zero-row
    // case). A rare per-archive scan, not a hot-path frame.
    private bool AllRowsZero()
    {
        foreach (var s in _stats.Values)
            if (s.TotalDamage != 0 || s.TotalHealing != 0 || s.TotalTaken != 0) return false;
        return true;
    }

    /// <summary>
    /// True when <paramref name="current"/> is evidence of a kill genuinely earned by THIS
    /// encounter: non-null AND different from <paramref name="baseline"/> (the settlement already
    /// on record when this encounter's combat started). IDungeonState.LastSettlement is sticky for
    /// the whole dungeon run — unchanged since baseline means it belongs to an earlier segment of
    /// the same run, not this one, so a manual archive mid-fight must not report "kill".
    /// </summary>
    internal static bool IsFreshKill(DungeonSettlementInfo? current, DungeonSettlementInfo? baseline)
        => current is not null && !current.Equals(baseline);

    // 3-way run verdict. Fail wins outright (a wipe). A Success outcome = kill. A fresh settlement
    // counts as a kill ONLY when it carries a real CLEAR signal — pass_time (the settlement clear
    // time) or master_mode_score (the max/par, set on clear). A bare total_score does NOT: it is a
    // LIVE progress score the game sends mid-run and on partials too, so treating its mere presence
    // as "kill" false-promoted partial runs (regression from the 686/700 total_score capture).
    internal static string ResolveVerdict(DungeonSettlementInfo? freshSettlement, DungeonOutcome outcome)
    {
        if (outcome == DungeonOutcome.Failed) return "fail";
        if (outcome == DungeonOutcome.Success) return "kill";
        if (freshSettlement is { PassTimeSeconds: > 0 } or { MasterModeScore: > 0 }) return "kill";
        return "partial";
    }

    private long ComputeDurationMs()
    {
        long earliest = long.MaxValue, latest = 0;
        foreach (var s in _stats.Values)
        {
            if (s.FirstHitMs > 0 && s.FirstHitMs < earliest) earliest = s.FirstHitMs;
            if (s.LastHitMs  > latest)                       latest   = s.LastHitMs;
        }
        return earliest == long.MaxValue ? 0 : latest - earliest;
    }

    private Dictionary<EntityId, SourceStats> DeepCopyStats()
    {
        // Clone() is field-complete (MemberwiseClone + dict deep-copy) — a hand-listed
        // initializer here silently dropped newly added fields from every upload.
        var copy = new Dictionary<EntityId, SourceStats>(_stats.Count);
        foreach (var (id, src) in _stats) copy[id] = src.Clone();
        return copy;
    }

    private Dictionary<EntityId, SourceSeries> FreezeTimelines()
    {
        var copy = new Dictionary<EntityId, SourceSeries>(_timelines.Count);
        foreach (var (id, t) in _timelines)
            copy[id] = new SourceSeries
            {
                BucketMs = t.BucketMs,
                Dealt    = t.Freeze(TimelineChannel.Dealt),
                Healing  = t.Freeze(TimelineChannel.Healing),
                Taken    = t.Freeze(TimelineChannel.Taken),
            };
        return copy;
    }

    /// <summary>
    /// Active-uptime fraction for a source: how much of the encounter the source was
    /// dealing damage (FirstHit→LastHit span over the encounter duration, clamped 0..1).
    /// </summary>
    internal static float ComputeUptime(long firstHitMs, long lastHitMs, long durationMs)
    {
        if (durationMs <= 0 || lastHitMs <= firstHitMs) return 0f;
        var span = (float)(lastHitMs - firstHitMs);
        var frac = span / durationMs;
        return frac < 0f ? 0f : frac > 1f ? 1f : frac;
    }
}
