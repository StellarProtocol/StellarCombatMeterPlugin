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
    /// Returns <c>(0, false, null)</c> when the segment tracked no stage boss at all (boss-phase
    /// detection off for this content, or a genuinely bossless trash segment) — the caller falls back
    /// to its own (dead-cache) resolution in that case, exactly as the pre-multi-boss per-segment
    /// scalar did.
    /// </summary>
    internal static (int BossConfigId, bool BossKilled, IReadOnlyList<BossRec>? Bosses) ResolveStageBosses(
        IReadOnlyList<(EntityId Id, int ConfigId, bool Killed)> stageBosses)
    {
        if (stageBosses.Count == 0) return (0, false, null);

        var bosses = new List<BossRec>(stageBosses.Count);
        foreach (var m in stageBosses) bosses.Add(new BossRec(m.ConfigId, m.Killed));

        var rep = stageBosses[0];   // first-admitted (amendment 4) — matches entry.StageBosses' order,
                                    // itself StageBossSet's admission order (Admit appends).
        return (rep.ConfigId, rep.Killed, bosses);
    }
}
