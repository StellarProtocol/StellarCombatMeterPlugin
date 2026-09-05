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

    private sealed record Key(int Base, int Stacks, int SrcKind, int SrcId);
    private sealed class Pending { public Key Key = null!; public Dictionary<int, long> Pre = null!; public long Deadline; public int SeqAtSnapshot; public int Sign; }

    private readonly List<Pending> _pending = new();
    private readonly Dictionary<Key, Dictionary<int, List<long>>> _samples = new();
    private int _selfChangeSeq;

    internal void OnSelfBuff(CombatEvent.BuffChanged b, IReadOnlyDictionary<int, long> sheetNow, long nowMs)
    {
        _selfChangeSeq++;
        if (b.Kind == BuffChangeKind.Refreshed) return;
        if (!b.FirerId.IsPlayer || b.FirerId == b.TargetId) return;
        var pre = new Dictionary<int, long>(TrackedAttrs.Length);
        foreach (var a in TrackedAttrs) if (sheetNow.TryGetValue(a, out var v)) pre[a] = v;
        // Overlap guard: a self-buff change landing while an EARLIER window is still open taints
        // BOTH — the earlier one is dropped by the ordinary seq-mismatch check below (this call
        // already bumped the shared counter past its snapshot), and THIS new one is born dirty: its
        // own "pre" sheet was read while the earlier buff's effect might still be mid-resolution, so
        // a sentinel snapshot seq that can never equal a real future counter value guarantees it is
        // discarded at Tick too, however clean ITS OWN window looks in isolation.
        var overlapping = _pending.Count > 0;
        _pending.Add(new Pending
        {
            Key = new Key(b.BaseId, b.Stacks, b.SourceKind, b.SourceId), Pre = pre,
            Deadline = nowMs + WindowMs, SeqAtSnapshot = overlapping ? -1 : _selfChangeSeq,
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
