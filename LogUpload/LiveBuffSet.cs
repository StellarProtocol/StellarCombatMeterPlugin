using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>The buffs currently live in AOI, fed by every converted buff row (rDPS phase 2, spec § 6.1.3), so a NEW
/// spool segment can open with a keyframe of what was already up — a buff applied before the segment's first row
/// was otherwise invisible to the worker for the whole segment (spec § 3 "segment cuts"). Keyed per (target, uuid).
/// <para>BOUNDED at <see cref="MaxEntries"/> (AOI-scale): the rows are held in an insertion-ordered list — an
/// applied/refreshed row for a present key moves it to the young end — so a full set evicts its OLDEST entry in O(1)
/// to admit a new key (never refuses the new key outright). A per-target index makes a seed's replacement O(listed).
/// </para>
/// <para>SEEDS (framework ≥ 2.11.0, spec upload design 2026-09-26 items 3–5): <see cref="Seed"/> takes a
/// <c>CombatEvent.EntityBuffsSeeded</c> snapshot. It REPLACES the target (live rows it does not list are gone; listed
/// live rows stay and keep being restated by the keyframe, exactly as 2.14.2 did) and marks every listed uuid
/// SEED-PENDING. A seed-pending uuid holds no live row of its own — 2.14.2 never knew such a buff until its first live
/// delta — so it is never keyframed and never takes a cap slot. <see cref="TakeSeeded"/> consumes the mark on the
/// uuid's first live delta, letting the spool restore the 2.14.2 upload shape (Refreshed → applied, Removed → disk
/// only). Marks are bounded per target at <see cref="MaxSeedTargets"/> (oldest-seeded target dropped, O(1)).</para>
/// <para>SCENE CHANGE (<see cref="Clear"/>) drops the live rows only (the framework clears its own buff cache silently
/// there — no Removed rows arrive). Seed marks survive it on purpose: the framework's EnterScene seeds can be drained
/// BEFORE the plugin's SceneChanged handler runs, and a stale mark is harmless — the framework no longer holds that
/// uuid, so its next event is an Applied, which consumes the mark without converting anything.</para>
/// Not thread-safe: every method is main-thread only — every caller is EventSpool's own main-thread path.</summary>
internal sealed class LiveBuffSet
{
    internal const int MaxEntries = 4096;
    internal const int MaxSeedTargets = 1024;

    private readonly Dictionary<(string tgt, int uuid), LinkedListNode<LiveBuff>> _live = new();
    private readonly LinkedList<LiveBuff> _order = new();                    // oldest apply/refresh first
    private readonly Dictionary<string, HashSet<int>> _liveByTarget = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LinkedListNode<(string tgt, HashSet<int> uuids)>> _seeded = new(StringComparer.Ordinal);
    private readonly LinkedList<(string tgt, HashSet<int> uuids)> _seedOrder = new();   // oldest seed first

    internal int Count => _live.Count;

    /// <summary>Targets currently holding seed-pending marks (bounded by <see cref="MaxSeedTargets"/>).</summary>
    internal int SeedTargetCount => _seeded.Count;

    internal void Apply(LiveBuff live)
    {
        var key = (live.Row.Tgt, live.Row.Uuid);
        if (live.Row.Kind == "removed") { RemoveLive(key); return; }
        if (_live.TryGetValue(key, out var node))
        {
            node.Value = live;                              // applied/refreshed: the row's own Ms is the apply instant
            _order.Remove(node);
            _order.AddLast(node);                           // young again — the eviction order follows the last update
            return;
        }
        if (_live.Count >= MaxEntries) RemoveLive(KeyOf(_order.First!.Value));   // O(1): the oldest entry
        _live[key] = _order.AddLast(live);
        IndexOf(key.Tgt).Add(key.Uuid);
    }

    /// <summary>A complete buff snapshot for <paramref name="tgt"/> (see the class doc). An empty snapshot is the
    /// framework's "this entity now holds nothing" — <see cref="DropTarget"/>.</summary>
    internal void Seed(string tgt, IReadOnlyList<ActiveBuff> buffs)
    {
        if (buffs.Count == 0) { DropTarget(tgt); return; }
        var listed = new HashSet<int>();
        for (var i = 0; i < buffs.Count; i++) listed.Add(buffs[i].BuffUuid);
        if (_liveByTarget.TryGetValue(tgt, out var held))
        {
            List<int>? gone = null;
            foreach (var uuid in held) if (!listed.Contains(uuid)) (gone ??= new List<int>()).Add(uuid);
            if (gone is not null) foreach (var uuid in gone) RemoveLive((tgt, uuid));
        }
        RemoveSeeded(tgt);
        if (_seeded.Count >= MaxSeedTargets) RemoveSeeded(_seedOrder.First!.Value.tgt);   // O(1): the oldest seed
        _seeded[tgt] = _seedOrder.AddLast((tgt, listed));
    }

    /// <summary>True exactly once per seeded (target, uuid): on its first live delta. Consumes the mark.</summary>
    internal bool TakeSeeded(string tgt, int uuid)
    {
        if (!_seeded.TryGetValue(tgt, out var node) || !node.Value.uuids.Remove(uuid)) return false;
        if (node.Value.uuids.Count == 0) RemoveSeeded(tgt);
        return true;
    }

    /// <summary>Drops everything held for <paramref name="tgt"/> — its live rows and its seed marks.</summary>
    internal void DropTarget(string tgt)
    {
        if (_liveByTarget.TryGetValue(tgt, out var held))
            foreach (var uuid in new List<int>(held)) RemoveLive((tgt, uuid));
        RemoveSeeded(tgt);
    }

    /// <summary>One synthetic `applied` per live buff, stamped <paramref name="kfMs"/>, DurMs = remaining; a buff whose own
    /// duration has run out is dropped here (a missed remove). Deterministic order: target, then uuid. Seed-pending
    /// buffs are not live rows and are never restated.</summary>
    internal List<LiveBuff> Keyframe(long kfMs)
    {
        var result = new List<LiveBuff>(_live.Count);
        List<(string, int)>? expired = null;
        foreach (var live in _order)
        {
            var row = live.Row;
            long remaining = row.DurMs > 0 ? row.Ms + row.DurMs - kfMs : row.DurMs;
            if (row.DurMs > 0 && remaining <= 0) { (expired ??= new()).Add(KeyOf(live)); continue; }
            var dur = (int)Math.Min(int.MaxValue, remaining);
            result.Add(live with { Row = row with { Ms = kfMs, Kind = "applied", DurMs = dur, Kf = true } });
        }
        if (expired is not null) foreach (var k in expired) RemoveLive(k);
        result.Sort((a, b) => { var c = string.CompareOrdinal(a.Row.Tgt, b.Row.Tgt); return c != 0 ? c : a.Row.Uuid.CompareTo(b.Row.Uuid); });
        return result;
    }

    /// <summary>Scene change: drops every LIVE row; seed marks survive (see the class doc).</summary>
    internal void Clear()
    {
        _live.Clear();
        _order.Clear();
        _liveByTarget.Clear();
    }

    private static (string, int) KeyOf(LiveBuff live) => (live.Row.Tgt, live.Row.Uuid);

    private HashSet<int> IndexOf(string tgt)
    {
        if (!_liveByTarget.TryGetValue(tgt, out var set)) _liveByTarget[tgt] = set = new HashSet<int>();
        return set;
    }

    private void RemoveLive((string tgt, int uuid) key)
    {
        if (!_live.Remove(key, out var node)) return;
        _order.Remove(node);
        if (_liveByTarget.TryGetValue(key.tgt, out var set) && set.Remove(key.uuid) && set.Count == 0)
            _liveByTarget.Remove(key.tgt);
    }

    private void RemoveSeeded(string tgt)
    {
        if (_seeded.Remove(tgt, out var node)) _seedOrder.Remove(node);
    }
}
