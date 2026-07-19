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

        // Suppress the "0s · 1p" junk. Every AUTO trigger (scene-enter, dungeon flow-state bump,
        // false-start wipe, boss, idle) fires while a stray taken-hit / single instant hit sits in
        // _stats — content the old _stats.Count>0 gate wrongly treated as an encounter. Skip the
        // history push + upload when there's no real combat span, but keep every side effect
        // (shared-cooldown/latch bookkeeping via OnArchived, meter reset via Clear) identical to a
        // real archive, so nothing else changes. A MANUAL (button/hotkey) archive is never suppressed.
        var spanMs = ComputeDurationMs();
        if (ShouldSuppressAutoArchive(reason, spanMs, CarriesRunResult()))
        {
            LogArchiveOutcome(reason, "suppressed", _stats.Count, spanMs);
            _autoArchive.OnArchived(_services.CombatSnapshot.ServerNowMs, reason);
            Clear();
            return;
        }

        var entry = BuildHistoryEntry(reason);
        _history.Add(entry);
        foreach (var evicted in TrimToCapacity(_history)) _uploadStatus.Forget(evicted);   // unroot evicted runs
        SaveHistory();   // persist on every archive + eviction (a user/scene event, not a hot-path frame)

        // SP1 + Replay R1 + P2 courtesy: assemble the replay doc FIRST (capture reset must happen
        // at archive regardless), then let the summary upload's verdict decide whether the
        // positions POST is needed (havePositions) — the callback owns the doc when a summary
        // upload fires; otherwise (auto-upload off / no events) upload immediately as before.
        // The position replay is ONE continuous track per dungeon RUN. Only a run-TERMINAL archive
        // assembles + uploads + resets it (ShouldFinalizeReplay); a mid-run non-kill stage/boss/idle
        // archive banks its DAMAGE segment but leaves the replay accumulating, so the whole-run track
        // survives to the terminal upload. (Previously EVERY archive reset the replay, truncating the
        // dungeon to just its final slice — the uploaded replay was shorter than the game's clear time.)
        var replayDoc = ShouldFinalizeReplay(reason, entry.Result == "kill") ? PrepareReplayDoc(entry) : null;
        var summaryFired = MaybeUploadLog(entry, replayDoc);
        if (!summaryFired && replayDoc is not null) UploadReplayDoc(replayDoc);
        LogArchiveOutcome(reason, summaryFired ? "banked+upload" : "banked", entry.Stats.Count, entry.CombatDurationMs);

        _autoArchive.OnArchived(_services.CombatSnapshot.ServerNowMs, reason);
        Clear();
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

    // Minimum real combat SPAN (dealt-damage FirstHit→LastHit, via ComputeDurationMs) for an AUTO
    // archive to be worth a history entry + upload. The old eligibility gate (_stats.Count > 0)
    // counted a taken-only phantom row — a player who took a hit but dealt nothing — as an encounter,
    // so a stray hit caught by a hub scene-enter, a dungeon flow-state bump, or a false-start wipe
    // banked a "0s · 1p" junk entry (and uploaded it). ComputeDurationMs is ~0 for such content
    // (taken-only rows never set FirstHit/LastHit; a single instant hit gives First==Last) but tens
    // of seconds for a real run, so this span gate cleanly separates junk (all 0s) from real runs
    // (5s+). Applies to EVERY auto trigger (scene/stage/wipe/boss/idle); a MANUAL button/hotkey
    // archive is always honored.
    internal const long MinAutoArchiveMs = 3_000;

    /// <summary>True when an AUTO-triggered archive should be skipped for lack of a real combat span.
    /// Every reason except <see cref="AutoArchive.ArchiveReason.Manual"/> is subject to the span gate;
    /// a manual (button/hotkey) archive is always kept. EXEMPTION: an archive that carries the run
    /// RESULT (<paramref name="carriesRunResult"/> — a fresh kill/settlement, a failed outcome, or a
    /// terminal dungeon flow state) is never junk and is kept regardless of span, so the dungeon-FINISH
    /// entry survives even when the user manually archived most of the fight just before it (owner
    /// report 2026-07-19: a 159 ms finish slice was wrongly discarded, losing the kill/clear-time).</summary>
    internal static bool ShouldSuppressAutoArchive(AutoArchive.ArchiveReason reason, long durationMs, bool carriesRunResult)
        => reason != AutoArchive.ArchiveReason.Manual && durationMs < MinAutoArchiveMs && !carriesRunResult;

    /// <summary>
    /// True when <paramref name="current"/> is evidence of a kill genuinely earned by THIS
    /// encounter: non-null AND different from <paramref name="baseline"/> (the settlement already
    /// on record when this encounter's combat started). IDungeonState.LastSettlement is sticky for
    /// the whole dungeon run — unchanged since baseline means it belongs to an earlier segment of
    /// the same run, not this one, so a manual archive mid-fight must not report "kill".
    /// </summary>
    internal static bool IsFreshKill(DungeonSettlementInfo? current, DungeonSettlementInfo? baseline)
        => current is not null && !current.Equals(baseline);

    // True when THIS archive is banking the dungeon's RESULT — a fresh kill/settlement, a failed
    // outcome (wipe), or a terminal dungeon flow state (End/Settlement). Such an archive is the
    // run finish and must never be junk-suppressed for a short span (the dungeon-finish slice is
    // tiny when the user manually archived the fight moments earlier). Reads the sticky dungeon
    // state on the main thread (volatile reads; no IL2CPP), so it is safe at archive time.
    private bool CarriesRunResult()
        => IsFreshKill(_services.Dungeon.LastSettlement, _settlementAtCombatStart)
        || _services.Dungeon.LastOutcome == DungeonOutcome.Failed
        || _services.Dungeon.CurrentFlowState is DungeonFlowState.End or DungeonFlowState.Settlement;

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
