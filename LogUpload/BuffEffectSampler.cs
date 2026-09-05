using System;
using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter.LogUpload;

internal sealed record BuffEffectAgg(int Base, int Stacks, int SrcKind, int SrcId, int N, IReadOnlyList<(int AttrId, long MedianDelta)> Deltas);

/// <summary>
/// Measures what an EXTERNAL buff does to the local player's attribute sheet (spec 2026-09-05 § 4.2 / § 6.1):
/// snapshot the sheet when a party member's buff lands on (or leaves) self, read it again after
/// <see cref="WindowMs"/>, and keep the per-attr delta only when no OTHER self buff changed inside the window
/// (a clean window). Pure: callers feed sheets and clocks. Medians per (base, stacks) key at drain.
/// </summary>
internal sealed class BuffEffectSampler
{
    internal static readonly int[] TrackedAttrs =
    {
        11710, 11780, 12510, 12530, 12670, 12630,
        13100, 13110, 13120, 13130, 13140, 13150, 13160, 13170, 13180,
        11330, 11340, 11500, 11510, 11520, 11530, 11540, 11550, 11560, 11570, 11580,
    };
    internal const int WindowMs = 600;
    internal const int MaxSamplesPerKey = 32;
    /// <summary>Defence in depth against a clock that never advances (e.g. <c>ServerNowMs</c> stalled):
    /// without this cap a feed that keeps admitting candidates while <see cref="Tick"/> never closes any
    /// window would grow <c>_pending</c> without bound. Drop-oldest keeps memory flat; losing the oldest
    /// (longest-overdue, most likely already stale) candidate is the least-bad loss.</summary>
    internal const int MaxPending = 256;
    private const int DirtySeq = -1;   // unmatchable sentinel: _selfChangeSeq never goes negative

    private static readonly Dictionary<int, long> EmptyPre = new();

    private sealed record Key(int Base, int Stacks, int SrcKind, int SrcId);
    private sealed class Pending { public Key Key = null!; public Dictionary<int, long> Pre = null!; public long Deadline; public int SeqAtSnapshot; public int Sign; }

    private readonly List<Pending> _pending = new();
    private readonly Dictionary<Key, Dictionary<int, List<long>>> _samples = new();
    private int _selfChangeSeq;
    private long _lastSelfChangeMs = long.MinValue;

    internal bool HasPending => _pending.Count > 0;

    /// <summary>Convenience overload for an already-read sheet (tests; and any future eager caller).
    /// Delegates to the lazy overload so the admission rule lives in exactly one place.</summary>
    internal void OnSelfBuff(CombatEvent.BuffChanged b, IReadOnlyDictionary<int, long> sheetNow, long nowMs)
        => OnSelfBuff(b, () => sheetNow, nowMs);

    /// <summary>Symmetric quiet-window rule (spec 2026-09-05 § 4.2 rev): a sample is clean only if NO
    /// self buff change of ANY kind — admitted, refreshed, self-applied, or monster-applied — happened
    /// within <see cref="WindowMs"/> BEFORE or AFTER it. The BEFORE half is <c>quiet</c> below, checked
    /// against <see cref="_lastSelfChangeMs"/> which every call stamps unconditionally (so a change this
    /// method goes on to filter out still dirties its neighbors); the AFTER half is the existing
    /// <see cref="_selfChangeSeq"/> mismatch check at <see cref="Tick"/> (also bumped unconditionally).
    /// <paramref name="readSheet"/> is called at most once, and only for a candidate that is both
    /// admitted (passes the Refreshed/self-firer filters) AND quiet — a dirty admission is discarded at
    /// Tick regardless of its "pre" sheet, so reading one for it would be pure waste.</summary>
    internal void OnSelfBuff(CombatEvent.BuffChanged b, Func<IReadOnlyDictionary<int, long>> readSheet, long nowMs)
    {
        _selfChangeSeq++;
        // Sentinel-safe: on the very first call ever, _lastSelfChangeMs is long.MinValue and
        // `nowMs - _lastSelfChangeMs` would OVERFLOW (wrapping to a bogus negative, which read as
        // "not quiet") — special-case the sentinel instead of subtracting through it.
        var quiet = _lastSelfChangeMs == long.MinValue || nowMs - _lastSelfChangeMs >= WindowMs;
        _lastSelfChangeMs = nowMs;
        if (b.Kind == BuffChangeKind.Refreshed) return;
        if (!b.FirerId.IsPlayer || b.FirerId == b.TargetId) return;

        Dictionary<int, long> pre = EmptyPre;
        if (quiet)
        {
            var sheetNow = readSheet();
            pre = new Dictionary<int, long>(TrackedAttrs.Length);
            foreach (var a in TrackedAttrs) if (sheetNow.TryGetValue(a, out var v)) pre[a] = v;
        }
        // Defence in depth against a clock that never advances (see MaxPending doc) — drop the oldest
        // pending candidate rather than let this list grow without bound.
        if (_pending.Count >= MaxPending) _pending.RemoveAt(0);
        _pending.Add(new Pending
        {
            Key = new Key(b.BaseId, b.Stacks, b.SourceKind, b.SourceId), Pre = pre,
            Deadline = nowMs + WindowMs, SeqAtSnapshot = quiet ? _selfChangeSeq : DirtySeq,
            Sign = b.Kind == BuffChangeKind.Removed ? -1 : 1,
        });
    }

    internal void Tick(Func<IReadOnlyDictionary<int, long>> readSheet, long nowMs)
    {
        if (_pending.Count == 0) return;
        IReadOnlyDictionary<int, long>? post = null;
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            var p = _pending[i];
            if (nowMs < p.Deadline) continue;
            _pending.RemoveAt(i);
            if (_selfChangeSeq != p.SeqAtSnapshot) continue;          // dirty window
            post ??= readSheet();
            Record(p, post);
        }
    }

    private void Record(Pending p, IReadOnlyDictionary<int, long> post)
    {
        if (post.Count == 0) return;   // untracked/reset entity — an empty sheet is not a real reading
        if (!_samples.TryGetValue(p.Key, out var byAttr)) _samples[p.Key] = byAttr = new Dictionary<int, List<long>>();
        foreach (var a in TrackedAttrs)
        {
            var had = p.Pre.TryGetValue(a, out var before);
            var has = post.TryGetValue(a, out var after);
            if (!had && !has) continue;
            var delta = p.Sign * (after - before);
            if (!byAttr.TryGetValue(a, out var list)) byAttr[a] = list = new List<long>(8);
            if (list.Count < MaxSamplesPerKey) list.Add(delta);
        }
    }

    internal IReadOnlyList<BuffEffectAgg> Drain()
    {
        var result = new List<BuffEffectAgg>(_samples.Count);
        foreach (var (key, byAttr) in _samples)
        {
            var deltas = new List<(int, long)>();
            int n = int.MaxValue;
            foreach (var (attr, list) in byAttr)
            {
                if (list.Count == 0) continue;
                var med = Median(list);
                n = Math.Min(n, list.Count);
                if (med != 0) deltas.Add((attr, med));
            }
            if (deltas.Count > 0) result.Add(new BuffEffectAgg(key.Base, key.Stacks, key.SrcKind, key.SrcId, n, deltas));
        }
        Reset();
        return result;
    }

    internal void Reset() { _pending.Clear(); _samples.Clear(); }

    private static long Median(List<long> xs)
    {
        var s = xs.OrderBy(x => x).ToArray();
        return s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2;
    }
}
