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
    private readonly List<EncounterHistoryEntry> _history = new();
    private string? _lastSceneName;

    internal sealed partial class EncounterHistoryEntry   // BossBuckets/EliteBuckets: Plugin.BucketStats.cs
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
        // Per-class loadouts captured so far this run (Task 2's LoadoutCapture.Snapshot()), frozen
        // HERE at archive time — never read live at upload-assemble time, so a post-run town
        // class-swap cannot pollute an already-archived run (per-class-loadout plan, Task 3).
        public IReadOnlyList<CapturedLoadout> Loadouts = Array.Empty<CapturedLoadout>();
        public PartyType PartyType;
        public int       MemberCount;
        public long      LevelUuid;        // run id latched at THIS run's combat start (_lastRunId); live CurrentRunId is a fallback only — a deferred run-end archive sees CurrentRunId already advanced to the NEXT floor
        public long      PartyId;          // party id (GrpcTeam team_id) latched at run-start; 0 = solo/unformed
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
        // Multi-boss per battle (Task 6): every boss the stage set had ADMITTED when this segment
        // archived — via ResolveCurrentStageBosses() (Plugin.BossDetection.cs: live set, or the sticky
        // latch when the live set already drained/reset — final review, Critical 1), snapshotted HERE
        // (additive; NOT persisted to history JSON — read synchronously at archive-time upload only, so
        // no history-format change / rollback risk per process-rules §6).
        // NEVER re-read the live _stageBosses at upload time: an upload (a manual re-upload from
        // history, or even the same-tick assemble call) must describe THIS segment's set as it stood at
        // archive, not whatever the live set has become since (drained by the next stage, or reset by a
        // scene change). Empty for a bossless segment / boss-phase detection off. Replaces the retired
        // SegmentBossConfigId/BossKilled scalars (Task 2 stopgap) — CombatLogAssembler derives the
        // scalar representative (first-admitted, index 0) and the whole list becomes Encounter.Bosses.
        public IReadOnlyList<(EntityId Id, int ConfigId, bool Killed)> StageBosses =
            Array.Empty<(EntityId Id, int ConfigId, bool Killed)>();
        // Boss-phase-OFF fallback (fix 2026-08-13, invariant 5 regression from 957c12f): archive-time
        // snapshot of the standalone boss-HP heuristic's pick (_bossMonsterInfo?.Id ?? 0,
        // Plugin.Replay.cs), consumed by BossRepresentative.ResolveStageBosses (full rationale there)
        // only when StageBosses above is empty. 0 when the heuristic found nothing either.
        public int FallbackBossConfigId;
        // ELITE CAPTURE channel — CAPTURE ONLY, mirrors StageBosses' shape (see Plugin.EliteDetection.cs).
        public IReadOnlyList<(EntityId Id, int ConfigId, bool Killed)> Elites =
            Array.Empty<(EntityId Id, int ConfigId, bool Killed)>();
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
        var isFirstObservation = _lastSceneName is null;
        // MINOR 8 fix (final review): log BEFORE the reset below clears _stageBosses, so a scene-sourced
        // boundary line can carry bosses= too (see LogRunBoundary's doc, Plugin.Diagnostics.cs).
        if (!isFirstObservation)
            LogRunBoundary("scene", _lastRunId, _services.Dungeon.CurrentRunId, _stats.Count);
        // 3fd7559 ran these resets (incl. RecomputeUploadPolicyCache) UNCONDITIONALLY, before the guard
        // below (review fix, rb-task-2 finding 2 — a prior extraction had silently skipped the cache
        // recompute on the very first scene observation); only the archive half stays conditional.
        ResetRunScopedTrackers();
        if (isFirstObservation)
        {
            _lastSceneName = newScene;
            return;
        }

        // Capture pre-archive state for the diagnostics line below (ManualArchive may reset the
        // capture when the outgoing scene had combat).
        var archived = _stats.Count > 0;
        var samplesAtReset = _replay?.TotalSamples ?? 0;

        // Auto-archive on scene change — the archive half of the shared bank+reset block (the reset +
        // boundary log already ran above); RunBoundaryCore composes both for the poll-driven commit.
        BankRunBoundary(AutoArchive.ArchiveReason.SceneChange);
        // The poll-driven tracker must adopt this already-handled boundary's new id, or its next Observe
        // would see the same runId change and double-commit (invariant 6: one entry per boundary).
        _runBoundary.NotifySceneBoundaryHandled(_services.Dungeon.CurrentRunId);

        // Scene-boundary replay reset — now CONDITIONAL (spec 2026-07-19): the provisional
        // candidate->candidate hop (raid lobby -> boss room before the run-id latches) keeps the buffer
        // so the lobby movement survives into the run's replay. Every other boundary resets, preserving
        // the 93:53 cross-scene-carryover protection. When the outgoing scene HAD combat, ManualArchive
        // above already uploaded + reset — this is then a harmless no-op either way.
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
    internal void ManualArchive(AutoArchive.ArchiveReason reason,
                                long replayUpperCapServerMs = ReplayUpperCapUnset)
    {
        // Any archive that actually enters this method — the manual button/hotkey, a scene change,
        // OR the deferred AUTO fire itself — supersedes a still-pending settle-delayed auto archive
        // (Plugin.AutoArchive.cs). Clearing here means a manual/scene archive during the ~1 s wait
        // wins outright and a stale deferred StageChange can never double-fire on already-cleared
        // stats afterward. The deferred fire calls ManualArchive too, so it self-clears the slot.
        _pendingArchiveReason = null;

        // A run-end (scene/stage) auto-archive can legitimately carry ZERO stat rows: the boss-kill
        // archive already banked + Clear()ed the fight, and the game's clear/settlement packet only
        // lands ~1 s LATER — after that Clear() (measured: run sea/xHC0xrYY8r, settlement arrived
        // ~953 ms after the bosskill archive committed). Such an archive still carries the FRESH clear
        // result, and dropping it as skip-empty is what left a quick single-boss run reading "partial"
        // though the boss died. Owner ruling 2026-08-05 (Option B): bank it as a small CLEAR marker so
        // the run reads as a kill. A genuinely empty archive with NO fresh result is still dropped.
        if (_stats.Count == 0)
        {
            var late = _services.Dungeon.LastSettlement;
            var fresh = IsFreshKill(late, _settlementAtCombatStart) ? late : null;
            // Drive the marker off the run-scoped clear LATCH (_clearedThisRun), not the momentary live
            // settlement: a fast single-boss floor in a multi-floor dungeon can have the framework WIPE
            // LastOutcome/LastSettlement before this empty run-end archive fires (vault-floor P0), which
            // would otherwise leave the verdict blank and drop the clear as skip-empty.
            var verdict = ResolveVerdict(fresh, _services.Dungeon.LastOutcome, _clearedThisRun);
            if (!ShouldBankEmptyClearMarker(reason, verdict, _clearMarkerBanked))
            {
                LogArchiveOutcome(reason, "skip-empty", 0, 0);
                return;
            }
            // The fight's Clear() reset _combatStartMs to 0; anchor the marker at "now" so its OWN
            // elapsed span is 0 and the merged run stays fight-start -> clear (the site's mergeSegments
            // takes the terminal segment's endMs as the run end). BuildHistoryEntry below injects the
            // roster 0/0/0 rows + stamps the kill verdict + passTime; the junk-suppression check is a
            // no-op here (a fresh clear always saves). _clearMarkerBanked is set once the entry banks
            // below (gated on the kill verdict), guarding a dungeon exit's several run-end archives.
            _combatStartMs = _services.CombatSnapshot.ServerNowMs;
            LogArchiveOutcome(reason, "clear-marker", 0, 0);
        }

        // Content-based junk suppression (owner ruling 2026-07-19, verbatim: "junk = when nothing
        // happen DPS=0, HPS=0, TAKEN=0. and even I do nothing and all other player keep having
        // DPS/HPS/TAKEN update it's not junk too"). Bin an AUTO archive ONLY when it carries no fresh
        // run result AND every stat row is all-zero. ANY nonzero activity — even a lone single-
        // participant instant hit — BANKS as its own entry (no participant-count / span floor); a
        // fresh kill/settlement tail ALWAYS saves (the destroyed-kill-tail bug this guards). A MANUAL
        // (button/hotkey) archive is never suppressed. carriesFreshResult uses IsFreshKill (baseline-
        // relative — a stale run-level result from an earlier segment does NOT count).
        // The run-scoped clear latch (_clearedThisRun) counts as a fresh result too: after the framework
        // wipes LastOutcome/LastSettlement (next-floor run-id), IsFreshKill reads false, but a floor that
        // genuinely cleared this run must NOT have its late clear-marker suppressed as all-zero junk
        // (vault-floor P0). A run that never cleared leaves the latch false, so junk is unaffected.
        var carriesFreshResult = _clearedThisRun
            || IsFreshKill(_services.Dungeon.LastSettlement, _settlementAtCombatStart);
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
        // Bank at most ONE clear per run: whichever archive first carries the kill (the run-end stage
        // archive at End, or the empty clear marker) latches this, so a dungeon exit's later run-end
        // archives (Settlement/Vote → scene → loading → town) — which still see the sticky clear
        // settlement — don't re-bank a duplicate. Reset on the next encounter's combat start.
        if (entry.Result == "kill") _clearMarkerBanked = true;
        _history.Add(entry);
        foreach (var evicted in TrimToCapacity(_history, HistoryRetention)) { _uploadStatus.Forget(evicted); ForgetReUpload(evicted); DeleteHistoryFile(evicted); }   // unroot + delete evicted runs
        WriteHistoryFile(entry);   // persist THIS run's per-run file (a user/scene event, not a hot-path frame)

        var summaryFired = FinalizeAndMaybeUploadReplay(entry, replayUpperCapServerMs);
        LogArchiveOutcome(reason, summaryFired ? "banked+upload" : "banked", entry.Stats.Count, entry.CombatDurationMs,
                          entryBosses: entry.StageBosses);
        if (reason == AutoArchive.ArchiveReason.Manual) NotifyManualArchived(entry.CombatDurationMs);

        _autoArchive.OnArchived(_services.CombatSnapshot.ServerNowMs, reason);
        NoteLastArchive(reason, entry.ArchivedAtMs);
        Clear();
    }

    // Replay delta-window upload wiring (owner design 2026-07-19), extracted so ManualArchive stays
    // under the 50-LoC cap. EVERY banked archive ships the window (watermark, now]: there is no
    // ShouldFinalizeReplay gate (retired — the recorder never stops, so no run-terminal concept) and
    // no sub-3s fragment gate (retired — contiguous windows stitch on the site, short tails are safe).
    // PrepareReplayDoc returns null for an off / no-level / EMPTY window, in which case the watermark
    // holds. On a successful hand-off to the upload queue the watermark advances and the window's
    // samples are freed; a failed hand-off (or no doc at all) keeps them so they merge into the next
    // window (at-least-once, owner default 2). Returns whether a SUMMARY upload fired.
    private bool FinalizeAndMaybeUploadReplay(EncounterHistoryEntry entry,
                                              long replayUpperCapServerMs = ReplayUpperCapUnset)
    {
        var replayDoc = PrepareReplayDoc(entry, replayUpperCapServerMs);
        // D1 (2026-07-28): the summary upload is INDEPENDENT of the replay doc. 9d03cfe returned early
        // here when replayDoc was null, which silently suppressed the run-stats upload whenever replay
        // upload was off / the run had no level id / the window was empty. MaybeUploadLog already
        // accepts a null doc (its replayDoc parameter defaults to null and both callback legs
        // null-check it), so pass it straight through.
        var summaryFired = MaybeUploadLog(entry, replayDoc);
        // summaryFired → the summary callback OWNS + uploads the doc (synchronous hand-off complete);
        // otherwise upload it directly here. Only a genuine hand-off advances the watermark — a null
        // doc means nothing was serialized, so the samples must stay for the next window.
        // Send vs store. `off` withholds the SEND only (owner ruling 2026-07-29) — the doc is already
        // retained by the stats path (PersistReUpload / RetainWithoutUpload both carry it), so custody
        // IS transferred and the watermark may advance exactly as on a successful upload. Advancing is
        // what keeps windows non-overlapping; the samples are durable on disk either way.
        var replaySendAllowed = ReplayAutoUploadAllowed(_contentKinds, _uploadPolicy, entry);
        var directHandedOff = replayDoc is not null && !summaryFired
            && (replaySendAllowed ? UploadReplayDoc(replayDoc) : true);
        if (ShouldAdvanceWatermark(replayDoc is not null, summaryFired, directHandedOff)) AdvanceReplayWatermark();
        return summaryFired;
    }

    /// <summary>Pure decision seam for the delta-window watermark (P0: replay must cover dungeon entry →
    /// run end). The watermark advances ONLY when a serialized window was genuinely handed off to an
    /// uploader — never when no doc existed (nothing to hand off) and never on a failed hand-off, so
    /// unshipped samples merge into the next window (at-least-once).</summary>
    internal static bool ShouldAdvanceWatermark(bool replayDocPresent, bool summaryFired, bool directUploadHandedOff)
        => replayDocPresent && (summaryFired || directUploadHandedOff);

    /// <summary>Resolves the party id (GrpcTeam team_id) an archived entry carries: the value LATCHED
    /// at combat start (<paramref name="latched"/>, Plugin.Capture.cs's <c>_lastTeamId</c>) wins
    /// outright when non-zero, so a mid-run/post-run party change (member leaves, party disbands,
    /// re-forms) never retroactively relabels an already-in-progress or already-archived encounter —
    /// the server keys a run's identity on the party (docs/superpowers/specs/
    /// 2026-08-04-run-identity-party-teamkey-design.md), so this id must stay stable for the whole
    /// run. <paramref name="live"/> (a fresh <c>PartySnapshot.PartyId</c> read) is only a fallback for
    /// the solo-at-combat-start edge case (latch == 0). SAME preference order as <c>LevelUuid</c>
    /// (both prefer the mid-run LATCHED value; live is only the latch==0 fallback): a DEFERRED
    /// run-end archive sees live values that have already advanced to the NEXT floor's run id (or a
    /// changed party), so the value latched during THIS run's combat is the correct one. 0 =
    /// solo/unformed at both.</summary>
    internal static long LatchTeamId(long latched, long live) => latched != 0 ? latched : live;

    /// <summary>Resolves the per-run dungeon-start timestamp an archived entry carries as
    /// <c>DungeonStartMs</c>: the value LATCHED once at the run's first combat start
    /// (<paramref name="latched"/>, Plugin.Capture.cs's <c>_lastRunStartMs</c>) wins outright when
    /// non-zero; <paramref name="live"/> (a fresh <c>Dungeon.RunTimerStartMs</c> read) is only the
    /// never-latched fallback (open-world has no dungeon timer → both are 0). SAME preference order and
    /// pure shape as <see cref="LatchTeamId"/> / the <c>LevelUuid</c> latch (all prefer the mid-run
    /// LATCHED value; live is only the latch==0 fallback). This exists because the game RE-STAMPS
    /// <c>RunTimerStartMs</c> at run end (Victory/settlement, measured 680000 → 802000 on prod
    /// sea/YvLLO3YSc8 + sea/yVfTrPylk7): a post-kill tail segment read live would carry a bogus start and
    /// the server (keying identity on <c>&lt;levelUuid&gt;-&lt;dungeonStartMs/1000&gt;</c>) would split ONE
    /// run into two pages. The latch is set ONCE per run and never re-latched per combat start (see
    /// <c>_lastRunStartMs</c>'s doc in Plugin.cs), so the tail keeps the run's original start. This is the
    /// READ side; the once-per-run SET side (0-retry) is the identically-shaped latch-else-live applied in
    /// <c>EnsureCombatStarted</c>.</summary>
    internal static long LatchRunStartMs(long latched, long live) => latched != 0 ? latched : live;

    /// <summary>Owner request 2026-08-05: the run page / meter must list EVERY party member IN THIS RUN,
    /// not only those who dealt or took damage in the archived window — a short archive can miss members
    /// who simply hadn't acted yet, so the roster looked incomplete. Ensures each in-instance party
    /// member has a stats row so an otherwise-silent member still archives as a 0/0/0 actor (its name +
    /// class are resolved live at snapshot from the roster/AOI). Never OVERWRITES an active row (kept via
    /// ContainsKey).
    ///
    /// SCOPED to the local player's scene: a member whose fast-sync <see cref="PartyMember.SceneId"/>
    /// differs from self's is out-of-instance (another floor / town / loading — see the SceneId doc) and
    /// must NOT be injected, or it would pollute this run's roster and the site's per-dungeon
    /// distinct-player counts. Self is sorted first (<see cref="IPartyRoster.Members"/>); we prefer the
    /// IsSelf member and fall back to members[0]. An empty roster — a true solo run with no party or bots
    /// — adds nothing, so solo runs stay byte-identical. Pure seam over (members, stats); the caller
    /// feeds the live roster and <c>_stats</c>. Run at archive time only, never on the per-frame hot
    /// path.</summary>
    internal static void EnsurePartyMembersTracked(
        IReadOnlyList<PartyMember> members, IDictionary<EntityId, SourceStats> stats)
    {
        if (members.Count == 0) return;                       // solo: no-op (byte-identical)
        var localScene = members[0].SceneId;                 // roster is self-first…
        foreach (var m in members) if (m.IsSelf) { localScene = m.SceneId; break; }   // …but be explicit
        foreach (var m in members)
            if (m.CharId > 0 && m.SceneId == localScene && !stats.ContainsKey(m.EntityId))
                stats[m.EntityId] = new SourceStats();
    }

    // Entry assembly, extracted so ManualArchive stays under the 50-LoC cap. The run-identity
    // snapshot rationale (sticky LastSettlement vs fresh-kill baseline) is documented on
    // IsFreshKill below and _settlementAtCombatStart's declaration.
    private EncounterHistoryEntry BuildHistoryEntry(AutoArchive.ArchiveReason reason)
    {
        // Owner 2026-08-05: list EVERY party member (0/0/0 rows for the silent ones), not just those
        // active in this — possibly brief — archived window. Runs after the junk-suppression check
        // (ManualArchive) so injected zeros can't un-suppress an all-zero junk archive, and before the
        // DeepCopyStats/SnapshotEntities capture below so both the stats copy and the entity snapshot
        // include them. The post-archive Clear() drops these rows, so live meter state stays clean.
        EnsurePartyMembersTracked(_services.PartyRoster.Members, _stats);
        var settlement = _services.Dungeon.LastSettlement;
        var freshSettlement = IsFreshKill(settlement, _settlementAtCombatStart) ? settlement : null;
        // Run-scoped clear latch (vault-floor P0, run sea/qyvCSXteqC): the framework can WIPE
        // LastOutcome/LastSettlement (next floor's run-id) before this always-firing run-end archive banks
        // the outgoing floor. Prefer the LIVE fresh settlement; fall back to the latched one so the clear's
        // pass-time/score still ship, and let the latch drive the verdict (freshSettlement stays live so a
        // never-cleared run is unaffected). _clearedSettlement is only ever set together with
        // _clearedThisRun, so the fallback can never invent a clear for a partial run.
        var clearSettlement = freshSettlement ?? _clearedSettlement;
        var entry = new EncounterHistoryEntry
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
            Loadouts         = LoadoutSnapshot(),
            PartyType        = _services.PartySnapshot.PartyType,
            MemberCount      = _stats.Count,
            LevelUuid        = _lastRunId != 0 ? _lastRunId : _services.Dungeon.CurrentRunId,
            PartyId          = AutoArchive.RelaunchMarker.ResolvePartyId(
                                   _lastTeamId, _services.PartySnapshot.PartyId, _relaunchPartyFallback),
            PassTime         = clearSettlement?.PassTimeSeconds ?? 0,
            MasterModeScore  = clearSettlement?.MasterModeScore ?? 0,
            TotalScore       = clearSettlement?.TotalScore ?? 0,
            DifficultyLevel  = Math.Max(_difficultyAtCombatStart, _services.Dungeon.CurrentDifficulty),
            DungeonStartMs   = LatchRunStartMs(_lastRunStartMs, _services.Dungeon.RunTimerStartMs),
            Result           = ResolveVerdict(freshSettlement, _services.Dungeon.LastOutcome, _clearedThisRun),
            Defeated         = _services.Dungeon.LastDefeatedCount,
            Trigger          = ResolveTriggerTag(reason),
            StageBosses      = ResolveCurrentStageBosses(),
            FallbackBossConfigId = _bossMonsterInfo?.Id ?? 0,
            Elites           = ResolveCurrentElites(),
        };
        // Post-build appliers, one per feature partial (last: Spec B buckets, Plugin.BucketStats.cs).
        ApplyAttrRanges(entry); ApplyClassSpans(entry); ApplySpecs(entry); ApplyBucketStats(entry);
        LogArchiveIdentity(entry);   // diagnostics: the exact uploaded run-identity (levelUuid/start/party)
        return entry;
    }

    internal static string ArchiveReasonTag(AutoArchive.ArchiveReason r) => r switch
    {
        AutoArchive.ArchiveReason.SceneChange => "scene",
        AutoArchive.ArchiveReason.Wipe        => "wipe",
        AutoArchive.ArchiveReason.BossPhase   => "boss",
        AutoArchive.ArchiveReason.Idle        => "idle",
        AutoArchive.ArchiveReason.StageChange => "stage",
        AutoArchive.ArchiveReason.BossKill    => "bosskill",
        AutoArchive.ArchiveReason.RunBoundary => "boundary",
        _                                     => "manual",
    };

    /// <summary>The stored <c>Trigger</c> tag for an archive. Identical to <see cref="ArchiveReasonTag"/>
    /// for every reason EXCEPT <see cref="AutoArchive.ArchiveReason.BossPhase"/>: that reason banks the
    /// pre-boss segment cut at the FIRST boss hit, so the banked archive is everything BEFORE boss
    /// combat (the first boss hit lands in the NEXT segment) — it contains no boss damage and must not
    /// read "boss" (owner 2026-08-03). Name it for its CONTENT via <see cref="PreBossPhaseTag"/>:
    /// "clear" when the party fought (dealt damage), "prepare" when only healing happened.</summary>
    private string ResolveTriggerTag(AutoArchive.ArchiveReason reason)
    {
        if (reason != AutoArchive.ArchiveReason.BossPhase) return ArchiveReasonTag(reason);
        long dmg = 0, heal = 0;
        foreach (var s in _stats.Values) { dmg += s.TotalDamage; heal += s.TotalHealing; }
        return PreBossPhaseTag(dmg, heal);
    }

    /// <summary>Content-based tag for the pre-boss archive: "clear" when any damage was dealt (a trash
    /// fight), "prepare" when no damage but healing happened (a heal-up before the pull), "clear" as
    /// the defensive fallback (all-zero archives never reach here — they are suppressed upstream).
    /// Pure so it pins headless (<c>HistoryTriggerFieldTests</c>).</summary>
    internal static string PreBossPhaseTag(long totalDamage, long totalHealing)
        => totalDamage > 0 ? "clear" : (totalHealing > 0 ? "prepare" : "clear");

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

    /// <summary>Whether an auto-archive with NO stat rows (<c>_stats.Count == 0</c>) should be banked as
    /// a small CLEAR marker rather than dropped as "skip-empty". True only for a NON-manual archive that
    /// resolves to a genuine <c>kill</c> and hasn't been banked yet this run. This is the run-end
    /// (scene/stage) archive that fires ~1 s AFTER the boss-kill archive already banked + <c>Clear()</c>ed
    /// the fight, once the game's late settlement/clear packet lands — banking it as its own terminal
    /// marker is what makes a quick single-boss clear read as a <c>kill</c> instead of <c>partial</c>
    /// (owner ruling 2026-08-05, "Option B"). Restricting to <c>kill</c> keeps a bare <c>pass=0</c>
    /// re-delivery on exit (verdict <c>partial</c>) from banking a junk marker; the one-per-run flag keeps
    /// the several run-end archives of a single exit from each re-marking. A genuinely empty archive with
    /// no clear, and any manual click with nothing to save, still skip-empty. Pure so it pins headless.</summary>
    internal static bool ShouldBankEmptyClearMarker(
        AutoArchive.ArchiveReason reason, string verdict, bool alreadyBankedThisRun)
        => reason != AutoArchive.ArchiveReason.Manual
        && verdict == "kill"
        && !alreadyBankedThisRun;

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
    //
    // clearedThisRun is the PLUGIN's run-scoped clear latch (vault-floor P0, run sea/qyvCSXteqC): a
    // multi-floor floor's clear is observed while IDungeonState.LastOutcome/LastSettlement are still
    // fresh, but the framework WIPES both when the next floor's run-id latches — BEFORE the outgoing
    // floor's always-firing run-end (scene) archive banks. When set, the run genuinely cleared earlier
    // this run, so the verdict is "kill" even though the live outcome/settlement now read blank. It is
    // reset at the next encounter's combat start, so it never promotes a run that never cleared (default
    // false keeps the 2-arg live-verdict callers, e.g. HasFreshClear, unchanged). Fail precedence still
    // wins above it. See UpdateClearLatch + the _clearedThisRun field doc.
    internal static string ResolveVerdict(
        DungeonSettlementInfo? freshSettlement, DungeonOutcome outcome, bool clearedThisRun = false)
    {
        if (outcome == DungeonOutcome.Failed) return "fail";
        if (outcome == DungeonOutcome.Success) return "kill";
        if (freshSettlement is { PassTimeSeconds: > 0 } or { MasterModeScore: > 0 }) return "kill";
        if (clearedThisRun) return "kill";
        return "partial";
    }

    /// <summary>Pure per-tick update of the run-scoped CLEAR latch (vault-floor P0, run sea/qyvCSXteqC).
    /// Driven every tick from <see cref="BuildAutoArchiveInputs"/> with the LIVE <c>hasFreshClear</c>
    /// signal: once a fresh clear is seen the latch STICKS <c>true</c> (so it survives the framework's
    /// next-floor <c>LastOutcome</c>/<c>LastSettlement</c> wipe) and captures the live settlement for its
    /// pass-time/score — but overwrites the captured settlement ONLY when the live one is non-null, so a
    /// bare outcome-only clear keeps whatever settlement it already banked. A tick with no fresh clear
    /// carries the prior latch UNCHANGED; the flag is cleared only by the encounter reset
    /// (<c>EnsureCombatStarted</c>), never here. Pure so it pins headless.</summary>
    internal static (bool cleared, DungeonSettlementInfo? settlement) UpdateClearLatch(
        bool wasCleared, DungeonSettlementInfo? latchedSettlement,
        bool hasFreshClear, DungeonSettlementInfo? liveSettlement)
        => hasFreshClear
            ? (true, liveSettlement ?? latchedSettlement)
            : (wasCleared, latchedSettlement);

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
