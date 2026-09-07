using System;
using System.Collections.Generic;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>The buffs currently live in AOI, fed by every converted buff row (rDPS phase 2, spec § 6.1.3), so a NEW
/// spool segment can open with a keyframe of what was already up — a buff applied before the segment's first row
/// was otherwise invisible to the worker for the whole segment (spec § 3 "segment cuts"). Keyed per (target, uuid).
/// Bounded at <see cref="MaxEntries"/> (AOI-scale; a full set drops NEW keys, never live ones). Cleared on scene
/// change by the plugin (the framework clears its own buff cache silently there — no Removed rows arrive).</summary>
internal sealed class LiveBuffSet
{
    internal const int MaxEntries = 4096;

    private readonly Dictionary<(string tgt, int uuid), LiveBuff> _live = new();

    internal int Count => _live.Count;

    internal void Apply(LiveBuff live)
    {
        var key = (live.Row.Tgt, live.Row.Uuid);
        if (live.Row.Kind == "removed") { _live.Remove(key); return; }
        if (_live.Count >= MaxEntries && !_live.ContainsKey(key)) return;
        _live[key] = live;                                  // applied/refreshed: the row's own Ms is the apply instant
    }

    /// <summary>One synthetic `applied` per live buff, stamped <paramref name="kfMs"/>, DurMs = remaining; a buff whose own
    /// duration has run out is dropped here (a missed remove). Deterministic order: target, then uuid.</summary>
    internal List<LiveBuff> Keyframe(long kfMs)
    {
        var result = new List<LiveBuff>(_live.Count);
        List<(string, int)>? expired = null;
        foreach (var kv in _live)
        {
            var row = kv.Value.Row;
            long remaining = row.DurMs > 0 ? row.Ms + row.DurMs - kfMs : row.DurMs;
            if (row.DurMs > 0 && remaining <= 0) { (expired ??= new()).Add(kv.Key); continue; }
            var dur = (int)Math.Min(int.MaxValue, remaining);
            result.Add(kv.Value with { Row = row with { Ms = kfMs, Kind = "applied", DurMs = dur, Kf = true } });
        }
        if (expired is not null) foreach (var k in expired) _live.Remove(k);
        result.Sort((a, b) => { var c = string.CompareOrdinal(a.Row.Tgt, b.Row.Tgt); return c != 0 ? c : a.Row.Uuid.CompareTo(b.Row.Uuid); });
        return result;
    }

    internal void Clear() => _live.Clear();
}
