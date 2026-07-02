using System;
using System.Collections.Generic;

namespace Stellar.CombatMeter.Replay;

/// <summary>
/// Samples HP% timelines for multiple entities at the replay capture cadence (2 Hz),
/// sharing one accumulator so all tracked entities sample on the same tick.
/// The HP read is injected so the class is headless-testable; the plugin supplies
/// a reader that merges live vitals with the attr-cache fallback (attrs 11310/11320).
/// A sample is skipped when maxHp is unknown (&lt;= 0) — same semantics as the
/// original boss-HP path.
/// </summary>
internal sealed class HpTimelineSampler
{
    internal const int SampleIntervalMs = 500;
    internal const int MaxSamplesPerEntity = 3600;

    private readonly Func<long, (long Hp, long MaxHp)> _readHp;
    private readonly Dictionary<long, Entry> _entries = new();
    private float _accumMs;

    private sealed class Entry
    {
        internal long Ms0;
        internal readonly List<int> Pct = new();
    }

    internal HpTimelineSampler(Func<long, (long Hp, long MaxHp)> readHp) => _readHp = readHp;

    /// <summary>Registers an entity for sampling; idempotent. ms0 is combat-relative, clamped ≥ 0.</summary>
    internal void Track(long entityId, long ms0)
    {
        if (_entries.ContainsKey(entityId)) return;
        _entries[entityId] = new Entry { Ms0 = ms0 < 0 ? 0 : ms0 };
    }

    /// <summary>Advances the shared accumulator; emits one sample per entity per 500 ms window.</summary>
    internal void Tick(float dtMs)
    {
        _accumMs += dtMs;
        if (_accumMs < SampleIntervalMs) return;
        _accumMs -= SampleIntervalMs;
        if (_accumMs >= SampleIntervalMs) _accumMs = 0f;

        foreach (var kv in _entries)
        {
            var entry = kv.Value;
            if (entry.Pct.Count >= MaxSamplesPerEntity) continue;
            var (hp, maxHp) = _readHp(kv.Key);
            if (maxHp <= 0) continue;
            var pct = (int)Math.Round(100.0 * hp / maxHp);
            entry.Pct.Add(pct < 0 ? 0 : pct > 100 ? 100 : pct);
        }
    }

    /// <summary>The sampled track for an entity, or null when it has no samples.</summary>
    internal BossHpTrack? GetTrack(long entityId)
        => _entries.TryGetValue(entityId, out var e) && e.Pct.Count > 0
            ? new BossHpTrack(e.Ms0, e.Pct.ToArray())
            : null;

    internal IEnumerable<long> TrackedIds => _entries.Keys;

    internal void Reset()
    {
        _entries.Clear();
        _accumMs = 0f;
    }
}
