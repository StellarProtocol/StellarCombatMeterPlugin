using System.Collections.Generic;
using System.Runtime.InteropServices;
using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter;

/// <summary>
/// Pure per-(player, target-bucket) store: dealt/taken totals, per-skill breakdown, and a
/// per-second series for each cell. Spec B foundation — routing (which bucket key a hit belongs
/// to) is a later task's job; this store only accumulates whatever it is handed. CAPTURE-ONLY:
/// no reference to AutoArchive, Plugin, or any archive/verdict surface.
/// </summary>
internal sealed class TargetBucketStats
{
    /// <summary>Bucket key for damage/taken not attributed to a tracked boss/elite target.</summary>
    internal const int OtherKey = 0;

    private readonly int _bucketMs;
    private readonly int _maxBuckets;
    private readonly Dictionary<(EntityId Player, int BucketKey), Cell> _cells = new();

    public TargetBucketStats(int bucketMs, int maxBuckets)
    {
        _bucketMs = bucketMs;
        _maxBuckets = maxBuckets;
    }

    /// <summary>Records one dealt hit against (player, bucketKey), rolling it into the skill and series aggregates.</summary>
    public void AddDealt(EntityId player, int bucketKey, int skillId, long amount, bool crit, long ms)
    {
        var cell = CellFor(player, bucketKey);
        cell.Dealt += amount;
        cell.Series.Add(TimelineChannel.Dealt, ms, startMs: 0, amount);
        // Single hash lookup per hit: ref to the existing skill slot (or a freshly-added default).
        ref var sk = ref CollectionsMarshal.GetValueRefOrAddDefault(cell.Skills, skillId, out _);
        sk.Total += amount;
        sk.Hits  += 1;
        if (crit) sk.Crits += 1;
    }

    /// <summary>Records damage taken by <paramref name="player"/> attributed to bucketKey's target.</summary>
    public void AddTaken(EntityId player, int bucketKey, long amount, long ms)
    {
        var cell = CellFor(player, bucketKey);
        cell.Taken += amount;
        cell.Series.Add(TimelineChannel.Taken, ms, startMs: 0, amount);
    }

    /// <summary>Drops every cell — same run-boundary contract as the other capture-only stores (elites, timelines).</summary>
    public void Clear() => _cells.Clear();

    private Cell CellFor(EntityId player, int bucketKey)
    {
        var key = (player, bucketKey);
        if (!_cells.TryGetValue(key, out var cell))
        {
            cell = new Cell(new SourceTimeline(_bucketMs, _maxBuckets));
            _cells[key] = cell;
        }
        return cell;
    }

    /// <summary>Bank-time snapshot: allocates freely (never called on the Add hot path).</summary>
    public IReadOnlyDictionary<EntityId, IReadOnlyDictionary<int, BucketSnapshot>> Snapshot()
    {
        var byPlayer = new Dictionary<EntityId, IReadOnlyDictionary<int, BucketSnapshot>>();
        var perPlayerBuckets = new Dictionary<EntityId, Dictionary<int, BucketSnapshot>>();
        foreach (var (key, cell) in _cells)
        {
            if (!perPlayerBuckets.TryGetValue(key.Player, out var buckets))
            {
                buckets = new Dictionary<int, BucketSnapshot>();
                perPlayerBuckets[key.Player] = buckets;
            }
            buckets[key.BucketKey] = cell.ToSnapshot();
        }
        foreach (var (player, buckets) in perPlayerBuckets) byPlayer[player] = buckets;
        return byPlayer;
    }

    private sealed class Cell
    {
        public long Dealt;
        public long Taken;
        public readonly Dictionary<int, SkillAgg> Skills = new();
        public readonly SourceTimeline Series;

        public Cell(SourceTimeline series) => Series = series;

        public BucketSnapshot ToSnapshot()
        {
            var skills = new List<(int SkillId, long Total, int Hits, int Crits)>(Skills.Count);
            foreach (var (skillId, agg) in Skills) skills.Add((skillId, agg.Total, agg.Hits, agg.Crits));
            return new BucketSnapshot(
                Dealt, Taken, skills,
                Series.Freeze(TimelineChannel.Dealt),
                Series.Freeze(TimelineChannel.Taken),
                Series.BucketMs);
        }
    }

    private struct SkillAgg
    {
        public long Total;
        public int  Hits;
        public int  Crits;
    }

    /// <summary>Frozen per-(player, target-bucket) cell: totals, per-skill breakdown, and per-second series.</summary>
    internal sealed record BucketSnapshot(
        long DealtTotal,
        long TakenTotal,
        IReadOnlyList<(int SkillId, long Total, int Hits, int Crits)> Skills,
        long[] DealtSeries,
        long[] TakenSeries,
        int SeriesBucketMs);
}
