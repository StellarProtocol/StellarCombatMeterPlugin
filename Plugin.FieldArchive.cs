using System;
using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.CombatMeter.Replay;

namespace Stellar.CombatMeter;

// FIELD-ARCHIVE identity scrub (owner "yes fix both", 2026-09-29 — "Map #9" on the Master Seal tab,
// levelUuid 21701554918653952 / session BYAo7hMspJ).
//
// The framework KEEPS the previous dungeon's LastOutcome / LastSettlement / CurrentDifficulty /
// RunTimerStartMs across the drop-to-0 into a field or town (DungeonStateService.SetCurrentRun: cleared
// only when the NEXT non-zero run id latches). A fight in the open world therefore started with those
// stale values in view, and its run-end (scene) archive — which fires as the player enters the NEXT
// dungeon, when CurrentRunId has already advanced — stamped: the NEXT dungeon's id (the latch==0
// CurrentRunId fallback), the stale Master level, the PREVIOUS dungeon's start (→ the server's 570 s
// duration for a 92 s fight), and `kill` (TrackClearLatch re-latched _clearedThisRun from the stale
// LastOutcome=Success). Measured on maps 7/8/9/11/91/95, plugin builds 2.4.0–2.14.0.
//
// A FIELD archive = NO run id latched at its own combat start (_lastRunId == 0 — the framework's run id is
// SceneType-gated at the wire, so a field/town always reads 0) AND a scene the scene table POSITIVELY
// classifies as non-instanced (SceneType != 2). Both terms are required: an unknown scene (table not
// loaded / non-numeric name) or ANY latched run id keeps today's dungeon stamping byte-for-byte, so a
// missed SceneChanged (stale _lastSceneName) can never strip a real dungeon's identity. World Dominator
// (7150-7152), raids and dungeons are all SceneType 2 — never field.
//
// A field archive carries its OWN identity, which is none: LevelUuid 0 (so MaybeUploadLog's existing
// "Field fight (no run id) — not uploaded" refusal applies, exactly as for every field fight whose
// archive fired while CurrentRunId was still 0), no difficulty, no dungeon start, no defeated count, and a
// verdict/settlement from a settlement FRESH to this fight only (IsFreshKill, baseline-relative) — never
// the sticky LastOutcome, never the run-scoped clear latch (a field is not a run and cannot clear).
// Banking decisions (junk suppression, skip-empty) are untouched except the empty-CLEAR-marker gate,
// which reads this same verdict so a carried-over clear cannot bank a marker in the field either.
public sealed partial class Plugin
{
    /// <summary>The run-identity fields an archive is stamped with, resolved in ONE place so the
    /// dungeon and field branches can't drift. <see cref="Settlement"/> feeds PassTime/MasterModeScore/
    /// TotalScore.</summary>
    internal readonly record struct ArchiveStamp(
        long LevelUuid, int DifficultyLevel, long DungeonStartMs, int Defeated,
        DungeonSettlementInfo? Settlement, string Verdict);

    /// <summary>Everything <see cref="ResolveArchiveStamp"/> reads, snapshotted from the plugin latches
    /// (<c>Latched*</c>, <see cref="ClearedThisRun"/>, <see cref="ClearedSettlement"/>) and the live
    /// <c>IDungeonState</c> (<c>Live*</c>, <see cref="Outcome"/>). <see cref="SceneKind"/> = the archive
    /// scene's <c>SceneTable.SceneType</c>, null when unknown.</summary>
    internal readonly record struct ArchiveStampInputs(
        long LatchedRunId, long LiveRunId, int LatchedDifficulty, int LiveDifficulty,
        long LatchedRunStartMs, long LiveRunStartMs, int LiveDefeated,
        DungeonSettlementInfo? FreshSettlement, DungeonSettlementInfo? ClearedSettlement,
        DungeonOutcome Outcome, bool ClearedThisRun, int? SceneKind);

    /// <summary>True when an archive is a FIELD (open-world / town) archive: no run id latched at its own
    /// combat start AND a scene positively known to be non-instanced. Unknown scene → false (dungeon
    /// stamping kept). Pure so it pins headless.</summary>
    internal static bool IsFieldArchive(long latchedRunId, int? sceneKind)
        => latchedRunId == 0 && sceneKind is int kind && !ReplayCaptureGate.IsCandidateScene(kind);

    /// <summary>The single run-identity resolver. The dungeon branch is the pre-2026-09-29 stamping,
    /// expression for expression (invariants 1-3: latched id first, clear latch drives the verdict). The
    /// field branch keeps only what this fight earned itself. Pure so it pins headless.
    /// <para>Run-scoped clear latch (vault-floor P0, run sea/qyvCSXteqC — moved here from
    /// BuildHistoryEntry): the framework can WIPE LastOutcome/LastSettlement (next floor's run-id) before
    /// the always-firing run-end archive banks the outgoing floor. Prefer the LIVE fresh settlement; fall
    /// back to the latched one so the clear's pass-time/score still ship, and let the latch drive the
    /// verdict (freshSettlement stays live so a never-cleared run is unaffected). _clearedSettlement is
    /// only ever set together with _clearedThisRun, so the fallback can never invent a clear for a partial
    /// run.</para></summary>
    internal static ArchiveStamp ResolveArchiveStamp(in ArchiveStampInputs i)
    {
        if (IsFieldArchive(i.LatchedRunId, i.SceneKind))
            return new ArchiveStamp(0, 0, 0, 0, i.FreshSettlement,
                ResolveVerdict(i.FreshSettlement, DungeonOutcome.None, clearedThisRun: false));
        return new ArchiveStamp(
            i.LatchedRunId != 0 ? i.LatchedRunId : i.LiveRunId,
            Math.Max(i.LatchedDifficulty, i.LiveDifficulty),
            LatchRunStartMs(i.LatchedRunStartMs, i.LiveRunStartMs),
            i.LiveDefeated,
            i.FreshSettlement ?? i.ClearedSettlement,
            ResolveVerdict(i.FreshSettlement, i.Outcome, i.ClearedThisRun));
    }

    // Snapshot of the live latches for ResolveArchiveStamp. freshSettlement is the caller's
    // IsFreshKill-filtered LastSettlement (null when stale), shared with BuildHistoryEntry's other reads.
    private ArchiveStamp CurrentArchiveStamp(DungeonSettlementInfo? freshSettlement)
    {
        var d = _services.Dungeon;
        return ResolveArchiveStamp(new ArchiveStampInputs(
            _lastRunId, d.CurrentRunId, _difficultyAtCombatStart, d.CurrentDifficulty,
            _lastRunStartMs, d.RunTimerStartMs, d.LastDefeatedCount,
            freshSettlement, _clearedSettlement, d.LastOutcome, _clearedThisRun,
            SceneKindOf(_lastSceneName)));
    }

    // The archive scene's SceneTable.SceneType, or null when the name doesn't parse or the row is missing.
    // Same parse as ResolveSceneCandidate / CombatLogAssembler's MapId. Archive-time only (not a hot path).
    private int? SceneKindOf(string? sceneName)
    {
        if (!int.TryParse(sceneName, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sceneId))
            return null;
        var info = _services.GameData.World.GetScene(sceneId);
        return info.HasValue ? info.Value.SceneKind : null;
    }
}
