using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>
/// ELITE CAPTURE channel (owner ruling 2026-08-13): derives the additive <c>encounter.elites[]</c> array
/// from a segment's archived elite snapshot. Mirrors <see cref="BossRepresentative"/>'s shape and its
/// "never read the live set" contract, but is a PLAIN MAP — elites have no scalar representative to
/// derive (nothing consumes an <c>EliteId</c>/<c>EliteKilled</c> pair the way old readers consume
/// <c>BossId</c>/<c>BossKilled</c>), so there is no fallback-heuristic term to carry either.
/// </summary>
internal static class EliteRepresentative
{
    /// <summary>
    /// Reads <paramref name="elites"/> — the caller's <c>entry.Elites</c>, itself
    /// <c>EliteSet.MembersSnapshot()</c> taken at archive time (Plugin.History.cs BuildHistoryEntry) —
    /// and NEVER the live <c>_eliteSet</c>: a manual re-upload of an old entry can run long after the set
    /// has moved on to a different run, so a live read would silently mislabel the wrong fight's elites
    /// onto this one. Returns <c>null</c> when the segment captured no elite at all (bossless/eliteless
    /// trash, or a segment archived before any elite combat event landed).
    /// </summary>
    internal static IReadOnlyList<EliteRec>? ResolveElites(
        IReadOnlyList<(EntityId Id, int ConfigId, bool Killed)> elites)
    {
        if (elites.Count == 0) return null;
        var list = new List<EliteRec>(elites.Count);
        foreach (var e in elites) list.Add(new EliteRec(e.ConfigId, e.Killed));
        return list;
    }
}
