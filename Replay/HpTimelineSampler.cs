using System;
using System.Collections.Generic;

namespace Stellar.CombatMeter.Replay;

/// <summary>
/// Samples HP% timelines for multiple entities at the replay capture cadence (2 Hz),
/// sharing one accumulator so all tracked entities sample on the same tick.
/// The HP read is injected so the class is headless-testable; the plugin supplies a reader that
/// tries the native boss-blood tap, then wire vitals, then a generic-attr fallback (see
/// Plugin.ReplayHp.cs's ReadHpPair).
///
/// <para><b>Sentinel grid (L2 fix, 2026-08-26 raid-bosshp-capture-design):</b> every reader
/// (<see cref="GetTrack"/>, <see cref="TrimBelow"/>, <c>Replay.ReplayWindow.SliceHp</c>) treats the
/// sample list as a DENSE 500 ms grid rooted at <c>Ms0</c> — sample <c>i</c> lives at grid time
/// <c>Ms0 + i*cadenceMs</c>, with no per-sample timestamp. A tick whose read is unusable
/// (<c>maxHp &lt;= 0</c>) therefore MUST still append something — a bare <c>continue</c> silently
/// shifts every later real sample's nominal grid time earlier than it actually happened, and once
/// that drift exceeds a window's bound the genuinely-captured sample falls outside it and is
/// dropped (recon L2, certain — this alone turned partially-sampled raid tracks into "~2 samples
/// per segment"). <see cref="Tick"/> appends <see cref="SentinelPct"/> (-1) instead: the grid stays
/// truthful, <see cref="TrimBelow"/> counts a sentinel as an ordinary grid slot (its trim math is
/// purely positional — it never inspects the value), and the upload writer passes -1 through
/// unmodified so the site can render it as a gap rather than a false 0%.</para>
/// </summary>
internal sealed class HpTimelineSampler
{
    internal const int SampleIntervalMs = 500;
    internal const int MaxSamplesPerEntity = 3600;

    /// <summary>Recorded in place of a real percent when a tick's HP read was unusable (maxHp
    /// unknown) — keeps the dense grid's timing honest instead of skipping the append. Never a
    /// valid percent (the real range is clamped to [0,100]), so it is unambiguous on read-back.
    /// The upload writer ships it as-is; the site treats a negative pct as a gap, not a value.</summary>
    internal const int SentinelPct = -1;

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
            if (maxHp <= 0)
            {
                // L2 fix: append a sentinel, not a skip — Ms0 + i*cadenceMs must keep naming the
                // real elapsed time even for a tick whose read was unusable (class doc). Still
                // subject to the same MaxSamplesPerEntity cap checked above — a sentinel is an
                // honest grid slot, so it counts toward the cap like any other sample.
                entry.Pct.Add(SentinelPct);
                continue;
            }
            var pct = (int)Math.Round(100.0 * hp / maxHp);
            entry.Pct.Add(pct < 0 ? 0 : pct > 100 ? 100 : pct);
        }
    }

    /// <summary>Records the entity's death: appends ONE final pct=0 sample so the uploaded track
    /// reaches 0 even though the live 2 Hz sampler stops when the boss entity vanishes on death
    /// (the source of the "replay clipped at ~8-12%" report). Idempotent: no-op when the entity is
    /// untracked or its last REAL (non-sentinel) sample is already 0 — trailing
    /// <see cref="SentinelPct"/> entries (unusable-read gaps recorded after the real death sample,
    /// e.g. by a caller that keeps invoking this every tick while the boss stays gone) are skipped
    /// when looking for "already 0", so a gap right after the terminal 0 can never defeat
    /// idempotency and mint a duplicate. Conversely, when NO real 0 has been recorded yet, trailing
    /// sentinels never block the append — a death observed after a run of gaps still appends the 0.
    /// ms0 is combat-relative, clamped ≥ 0 (unused here — samples share one implicit 500 ms grid
    /// rooted at Track's ms0, there is no per-sample timestamp to stamp; kept in the signature so
    /// callers pass the death instant for parity with Track/the rest of the sampler API).</summary>
    internal void MarkDead(long entityId, long ms0)
    {
        if (!_entries.TryGetValue(entityId, out var e)) return;
        for (var i = e.Pct.Count - 1; i >= 0; i--)
        {
            if (e.Pct[i] == SentinelPct) continue;
            if (e.Pct[i] == 0) return;   // already terminated — idempotent no-op
            break;                       // last REAL sample is non-zero — fall through to append
        }
        if (e.Pct.Count >= MaxSamplesPerEntity) return;
        e.Pct.Add(0);
    }

    /// <summary>The sampled track for an entity, or null when it has no samples.</summary>
    internal HpTrack? GetTrack(long entityId)
        => _entries.TryGetValue(entityId, out var e) && e.Pct.Count > 0
            ? new HpTrack(e.Ms0, e.Pct.ToArray())
            : null;

    internal IEnumerable<long> TrackedIds => _entries.Keys;

    /// <summary>Frees the leading samples of every tracked entity whose grid time
    /// (<c>Ms0 + i*cadenceMs</c>) is &lt;= <paramref name="ms"/> — an uploaded window's samples — and
    /// advances that entity's <see cref="Entry.Ms0"/> by the dropped count so <c>Ms0 + i*cadence</c>
    /// still names the correct grid slot for later slicing. Lifecycle op (runs at archive when the
    /// watermark advances); the shared accumulator and future appends are untouched.</summary>
    internal void TrimBelow(long ms, int cadenceMs)
    {
        foreach (var e in _entries.Values)
        {
            var drop = 0;
            while (drop < e.Pct.Count && e.Ms0 + (long)drop * cadenceMs <= ms) drop++;
            if (drop == 0) continue;
            e.Pct.RemoveRange(0, drop);
            e.Ms0 += (long)drop * cadenceMs;
        }
    }

    internal void Reset()
    {
        _entries.Clear();
        _accumMs = 0f;
    }
}
