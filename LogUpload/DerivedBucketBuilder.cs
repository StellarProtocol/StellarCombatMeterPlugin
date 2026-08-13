// Spec B (docs/superpowers/specs/2026-08-14-per-boss-statistics-design.md §4.2): maps the archived
// per-(player, target-bucket) snapshots onto the `derived` block's six optional maps. Split out of
// DerivedBuilder so that file keeps doing one job (whole-fight aggregates).
//
// NO-LOSS CONTRACT (§7, owner ruling "make sure no data clipped/skip/throw"): this mapper only ever
// OMITS a cell that carries nothing at all (no total, no skill row, no series bucket). Every cell with
// any content is emitted, "other" included, so Σ emitted buckets per (uid, channel) always equals the
// whole-fight ActorAgg total — pinned by DerivedBucketsTests.

using System.Collections.Generic;
using System.Globalization;
using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter.LogUpload;

internal static class DerivedBucketBuilder
{
    /// <summary>JSON bucket key for <see cref="TargetBucketStats.OtherKey"/> — damage not attributed to
    /// a tracked boss/elite. A string (not 0) so the site never confuses it with a config id.</summary>
    internal const string OtherBucketKey = "other";

    private const int DefaultBucketMs = 1000;

    internal readonly record struct BucketMaps(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, BucketDealt>>? Dealt,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, BucketTaken>>? Taken,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, BucketSeries>>? Series);

    /// <summary>The derived block's single normalized bucket width: the max over the whole-fight
    /// per-actor timelines AND every bucket cell. Per-cell <see cref="SourceTimeline"/> instances
    /// coalesce INDEPENDENTLY of the whole-fight ones (each doubles its own BucketMs once a fight
    /// outruns its cap), so a cell can be coarser than the actor series — normalizing to the max keeps
    /// the site's per-bucket chart swap like-for-like with the whole-fight chart.</summary>
    internal static int ResolveBucketMs(Plugin.EncounterHistoryEntry entry)
    {
        int bucketMs = DefaultBucketMs;
        foreach (var ser in entry.Series.Values) if (ser.BucketMs > bucketMs) bucketMs = ser.BucketMs;
        bucketMs = MaxCellBucketMs(entry.BossBuckets, bucketMs);
        bucketMs = MaxCellBucketMs(entry.EliteBuckets, bucketMs);
        return bucketMs;
    }

    private static int MaxCellBucketMs(
        IReadOnlyDictionary<EntityId, IReadOnlyDictionary<int, TargetBucketStats.BucketSnapshot>> store, int seed)
    {
        foreach (var buckets in store.Values)
            foreach (var cell in buckets.Values)
                if (cell.SeriesBucketMs > seed) seed = cell.SeriesBucketMs;
        return seed;
    }

    /// <summary>Maps one store (boss or elite) onto its three wire maps, all null when nothing is
    /// emitted — absent, never empty, so a bucketless run's derived block is byte-identical to a
    /// pre-Spec-B one (§7.5).</summary>
    internal static BucketMaps Build(
        IReadOnlyDictionary<EntityId, IReadOnlyDictionary<int, TargetBucketStats.BucketSnapshot>> store, int bucketMs)
    {
        var dealt  = new Dictionary<string, IReadOnlyDictionary<string, BucketDealt>>();
        var taken  = new Dictionary<string, IReadOnlyDictionary<string, BucketTaken>>();
        var series = new Dictionary<string, IReadOnlyDictionary<string, BucketSeries>>();

        foreach (var (player, buckets) in store)
        {
            var uid = player.Value.ToString(CultureInfo.InvariantCulture);
            var rows = new PlayerRows();
            foreach (var (bucketKey, cell) in buckets) rows.Add(BucketKey(bucketKey), cell, bucketMs);
            if (rows.Dealt.Count > 0)  dealt[uid]  = rows.Dealt;
            if (rows.Taken.Count > 0)  taken[uid]  = rows.Taken;
            if (rows.Series.Count > 0) series[uid] = rows.Series;
        }

        return new BucketMaps(NullIfEmpty(dealt), NullIfEmpty(taken), NullIfEmpty(series));
    }

    /// <summary>One player's three per-bucket row sets, filled cell by cell.</summary>
    private sealed class PlayerRows
    {
        internal readonly Dictionary<string, BucketDealt>  Dealt  = new();
        internal readonly Dictionary<string, BucketTaken>  Taken  = new();
        internal readonly Dictionary<string, BucketSeries> Series = new();

        internal void Add(string key, TargetBucketStats.BucketSnapshot cell, int bucketMs)
        {
            // Skills-but-no-total is a real case (a recorded 0-damage hit still carries hits/crits), so
            // the dealt row is kept whenever EITHER carries content — dropping it would lose hit counts.
            if (cell.DealtTotal != 0 || cell.Skills.Count > 0) Dealt[key] = new BucketDealt(cell.DealtTotal, MapSkills(cell.Skills));
            if (cell.TakenTotal != 0) Taken[key] = new BucketTaken(cell.TakenTotal);
            if (cell.DealtSeries.Length > 0 || cell.TakenSeries.Length > 0)
                Series[key] = new BucketSeries(
                    DerivedBuilder.Rebucket(cell.DealtSeries, cell.SeriesBucketMs, bucketMs),
                    DerivedBuilder.Rebucket(cell.TakenSeries, cell.SeriesBucketMs, bucketMs));
        }
    }

    // The bucket store keeps total/hits/crits only (spec §3.2); the remaining SkillAgg slots stay 0 so
    // per-bucket skill rows share the whole-fight row shape the site already renders.
    private static IReadOnlyList<SkillAgg> MapSkills(IReadOnlyList<(int SkillId, long Total, int Hits, int Crits)> skills)
    {
        var rows = new List<SkillAgg>(skills.Count);
        foreach (var (sid, total, hits, crits) in skills) rows.Add(new SkillAgg(sid, total, hits, crits, 0, 0, 0, 0));
        return rows;
    }

    private static string BucketKey(int bucketKey)
        => bucketKey == TargetBucketStats.OtherKey ? OtherBucketKey : bucketKey.ToString(CultureInfo.InvariantCulture);

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, T>>? NullIfEmpty<T>(
        Dictionary<string, IReadOnlyDictionary<string, T>> map) => map.Count > 0 ? map : null;
}
