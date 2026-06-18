using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter;

/// <summary>Which per-player total to surface in the meter.</summary>
public enum Metric { Dps, Hps, Taken }

/// <summary>A single row in a meter snapshot — all values normalised for display.</summary>
public readonly record struct MeterRow(
    EntityId Id,
    long Value,
    double Share,
    double BarFraction);

/// <summary>
/// Per-entity combat accumulator. Unity-free — safe to use in tests and
/// non-Unity hosts. Only <see cref="EntityId.IsPlayer"/> entities are tracked;
/// mob contributions are silently discarded.
/// </summary>
public sealed class MeterAggregator
{
    // ── Internal accumulator ─────────────────────────────────────────────────

    private sealed class Bucket
    {
        public long Damage;
        public long Healing;
        public long Taken;
    }

    /// <summary>Party-focus grid dimensions: 4 groups of up to 5 members = 20 slots.</summary>
    public const int Groups        = 4;
    public const int SlotsPerGroup = 5;

    private readonly Dictionary<EntityId, Bucket> _buckets = new();
    private readonly List<MeterRow> _rowCache = new();

    // Reusable party-focus grid (flat group*5+slot) + per-group fill cursor.
    private readonly MeterRow?[] _gridCache = new MeterRow?[Groups * SlotsPerGroup];
    private readonly int[]       _nextSlot  = new int[Groups];

    // ── Mutation ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Record a damage or heal event.
    /// Non-player entities are ignored.
    /// </summary>
    public void AddDamage(EntityId id, long amount, bool isHeal)
    {
        if (!id.IsPlayer) return;
        var b = GetOrCreate(id);
        if (isHeal)
            b.Healing += amount;
        else
            b.Damage += amount;
    }

    /// <summary>
    /// Record incoming damage taken by a player.
    /// Non-player entities are ignored.
    /// </summary>
    public void AddTaken(EntityId id, long amount)
    {
        if (!id.IsPlayer) return;
        GetOrCreate(id).Taken += amount;
    }

    /// <summary>Clear all accumulated data (encounter reset).</summary>
    public void Reset()
    {
        _buckets.Clear();
        _rowCache.Clear();
    }

    // ── Query ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Build a sorted, normalised row list for the given metric.
    /// Players whose selected-metric total is zero are excluded from the result.
    /// Returns a cached list — callers must not hold a reference across calls.
    /// <paramref name="elapsedSeconds"/> is accepted for API forward-compatibility
    /// (e.g. future per-second display); Value is the raw accumulated total.
    /// </summary>
    public IReadOnlyList<MeterRow> Rows(Metric metric, double elapsedSeconds)
    {
        _rowCache.Clear();

        // Pass 1: collect non-zero totals and compute team sum + max.
        long teamTotal = 0;
        long max = 0;

        foreach (var (id, b) in _buckets)
        {
            var value = Select(b, metric);
            if (value <= 0) continue;
            teamTotal += value;
            if (value > max) max = value;
            // Temporarily stash raw rows; Share/Bar computed in pass 2.
            _rowCache.Add(new MeterRow(id, value, 0d, 0d));
        }

        // Pass 2: rewrite with computed Share + BarFraction.
        for (var i = 0; i < _rowCache.Count; i++)
        {
            var r = _rowCache[i];
            var share = teamTotal > 0 ? (double)r.Value / teamTotal : 0d;
            var bar   = max       > 0 ? (double)r.Value / max       : 0d;
            _rowCache[i] = r with { Share = share, BarFraction = bar };
        }

        // Sort descending by value.
        _rowCache.Sort(static (x, y) => y.Value.CompareTo(x.Value));

        return _rowCache;
    }

    /// <summary>
    /// Build a fixed 20-slot party-focus grid (4 groups × 5) from the roster.
    /// Members are placed by <see cref="PartyMember.GroupId"/> (1–4) in roster
    /// order — NOT ranked. Slots without a member are <c>null</c>; members with no
    /// combat data are present with <c>Value == 0</c>. <c>BarFraction</c>/<c>Share</c>
    /// normalise to the top placed member's value. Returns a cached array — callers
    /// must not hold a reference across calls.
    /// </summary>
    public IReadOnlyList<MeterRow?> PartyGrid(IReadOnlyList<PartyMember> roster, Metric metric)
    {
        System.Array.Clear(_gridCache, 0, _gridCache.Length);
        System.Array.Clear(_nextSlot, 0, _nextSlot.Length);

        long Val(EntityId id) => _buckets.TryGetValue(id, out var b) ? Select(b, metric) : 0L;
        // GroupId is 1-based for raids (1–4). A normal/just-formed party reports GroupId 0 → Group 1.
        static int GroupIndex(int groupId) { var g = groupId <= 0 ? 0 : groupId - 1; return g < Groups ? g : -1; }

        // Pass 1: place members at their EXACT in-group slot (from NotifyTeamGroupUpdate's char-id order), so the
        // grid mirrors the game's Team×Slot layout. Pass 2 fills anyone without a known slot into the next gap.
        foreach (var m in roster)
        {
            var g = GroupIndex(m.GroupId);
            if (g < 0 || m.Slot < 0 || m.Slot >= SlotsPerGroup) continue;
            var idx = g * SlotsPerGroup + m.Slot;
            if (_gridCache[idx] is null) _gridCache[idx] = new MeterRow(m.EntityId, Val(m.EntityId), 0d, 0d);
        }
        foreach (var m in roster)
        {
            var g = GroupIndex(m.GroupId);
            if (g < 0) continue;
            if (m.Slot >= 0 && m.Slot < SlotsPerGroup
                && _gridCache[g * SlotsPerGroup + m.Slot] is { } pr && pr.Id == m.EntityId) continue;   // already placed
            while (_nextSlot[g] < SlotsPerGroup && _gridCache[g * SlotsPerGroup + _nextSlot[g]] is not null) _nextSlot[g]++;
            if (_nextSlot[g] >= SlotsPerGroup) continue;
            _gridCache[g * SlotsPerGroup + _nextSlot[g]] = new MeterRow(m.EntityId, Val(m.EntityId), 0d, 0d);
        }

        long total = 0, max = 0;
        foreach (var cell in _gridCache)
        {
            if (cell is not { } r) continue;
            total += r.Value;
            if (r.Value > max) max = r.Value;
        }
        for (var i = 0; i < _gridCache.Length; i++)
        {
            if (_gridCache[i] is not { } r) continue;
            var share = total > 0 ? (double)r.Value / total : 0d;
            var bar   = max   > 0 ? (double)r.Value / max   : 0d;
            _gridCache[i] = r with { Share = share, BarFraction = bar };
        }

        return _gridCache;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private Bucket GetOrCreate(EntityId id)
    {
        if (!_buckets.TryGetValue(id, out var b))
        {
            b = new Bucket();
            _buckets[id] = b;
        }
        return b;
    }

    private static long Select(Bucket b, Metric metric) => metric switch
    {
        Metric.Hps   => b.Healing,
        Metric.Taken => b.Taken,
        _            => b.Damage,
    };
}
