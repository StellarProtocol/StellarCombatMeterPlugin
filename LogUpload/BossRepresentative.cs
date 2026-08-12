using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>
/// Multi-boss per battle (Task 6): derives the additive <c>encounter.Bosses[]</c> array and the
/// scalar <c>BossId</c>/<c>BossKilled</c> representative from a segment's archived stage-boss
/// snapshot. Extracted out of <see cref="CombatLogAssembler"/> (that file was pushing the 500 LoC
/// guardrail) — pure/static, so it is directly unit-testable (<c>LogUploadTests</c>) without an
/// <c>IPluginServices</c> fake, mirroring how <c>Replay.BossPicker</c> sits beside
/// <see cref="CombatLogAssembler"/>'s other boss resolution.
/// </summary>
internal static class BossRepresentative
{
    /// <summary>
    /// Reads <paramref name="stageBosses"/> — the caller's <c>entry.StageBosses</c>, itself
    /// <c>StageBossSet.MembersSnapshot()</c> taken at archive time (Plugin.History.cs
    /// BuildHistoryEntry) — and NEVER the live <c>_stageBosses</c>: a manual re-upload of an old entry
    /// can run long after the set has drained or moved on to a different stage/run, so a live read
    /// would silently mislabel the wrong fight's bosses onto this one.
    ///
    /// The representative is the FIRST-ADMITTED member (index 0) — deterministic, and byte-identical
    /// to what the single latch used to report for a single-boss stage. Amendment 4 (2026-08-12
    /// review): NO plugin-side raid-roster preference here — master data for that lives
    /// server/site-side, and the worker already prefers <c>bosses[]</c> when present.
    ///
    /// When the segment tracked NO stage boss at all (boss-phase detection OFF for this content, or a
    /// genuinely bossless trash segment), falls back to <paramref name="fallbackBossConfigId"/> — the
    /// entry's <c>FallbackBossConfigId</c>, itself the archive-time snapshot of the STANDALONE boss-HP
    /// replay heuristic (<c>_bossMonsterInfo?.Id ?? 0</c>, Plugin.Replay.cs's
    /// <c>ResolveBossEntity</c>), which runs independently of <c>_autoArchive.BossEnabled</c> — gated
    /// only on the run being instanced. This restores the pre-Task-6 (commit a3cb7fa) behavior, where
    /// that same heuristic's id rode as <c>encounter.BossId</c> whenever Boss-phase was OFF (protected
    /// invariant 5 / "Boss phase = OFF -> bossId still recorded",
    /// docs/recon/combatmeter-archive-flow.md) — a regression introduced when 957c12f dropped the
    /// <c>_bossMonsterInfo?.Id ?? 0</c> argument from the two <c>Assemble</c> call sites without
    /// replacing it. <c>Bosses</c> stays <c>null</c> and <c>BossKilled</c> stays <c>false</c> in the
    /// fallback case: the heuristic carries no kill-state signal of its own, which matches EXACTLY what
    /// a3cb7fa shipped for this shape — its <c>entry.BossKilled</c> scalar was populated only from the
    /// same boss-phase-gated stage set (<c>_segmentBossKilled</c>, set only via <c>BossStatus()</c>'s
    /// first-admitted-member mirror), so it was always <c>false</c> whenever that set was empty.
    ///
    /// Returns <c>(0, false, null)</c> when BOTH are absent (default <paramref
    /// name="fallbackBossConfigId"/> of 0, i.e. the heuristic never resolved a boss either) — the
    /// caller (Assemble) then falls back further to its own (dead-cache) resolution, exactly as the
    /// pre-multi-boss per-segment scalar did.
    /// </summary>
    internal static (int BossConfigId, bool BossKilled, IReadOnlyList<BossRec>? Bosses) ResolveStageBosses(
        IReadOnlyList<(EntityId Id, int ConfigId, bool Killed)> stageBosses, int fallbackBossConfigId = 0)
    {
        // Non-empty StageBosses ALWAYS wins over the fallback, regardless of fallbackBossConfigId's
        // value — the real admitted-set representative below is never second-guessed by the heuristic.
        if (stageBosses.Count == 0) return (fallbackBossConfigId, false, null);

        var bosses = new List<BossRec>(stageBosses.Count);
        foreach (var m in stageBosses) bosses.Add(new BossRec(m.ConfigId, m.Killed));

        var rep = stageBosses[0];   // first-admitted (amendment 4) — matches entry.StageBosses' order,
                                    // itself StageBossSet's admission order (Admit appends).
        return (rep.ConfigId, rep.Killed, bosses);
    }
}
