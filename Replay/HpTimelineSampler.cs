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

    /// <summary>Catch-up bound (2026-08-26 grid-drift fix — owner-measured <c>gridDriftMs=2970</c> on
    /// one raid segment via the recon §6 archive diagnostic): the max number of REAL (repeated-value)
    /// catch-up slots a single <see cref="Tick"/> call appends when <c>dtMs</c> spans MULTIPLE 500 ms
    /// intervals (loading screens run at 1-10 Hz; any multi-second hitch). 20 slots = a 10 s hitch.
    /// Beyond this, additional whole intervals still owed are drained as SENTINEL slots (see
    /// <see cref="MaxSentinelDrainSlotsPerTick"/>) instead of more repeats of the same frozen read —
    /// re-appending an identical value dozens more times adds no information. Both bounds together
    /// mean a single pathological <c>dtMs</c> (a resumed-from-suspend process, a corrupt float) can
    /// NEVER make <see cref="Tick"/> spin: total loop iterations are capped regardless of how large
    /// <c>dtMs</c> is.</summary>
    internal const int MaxCatchUpSlotsPerTick = 20;

    /// <summary>Hard outer bound on the SENTINEL drain past <see cref="MaxCatchUpSlotsPerTick"/> —
    /// caps the combined worst case at (MaxCatchUpSlotsPerTick + this) = 40 slots = 20 s of grid-time
    /// processed in one <see cref="Tick"/> call. Any remainder beyond BOTH bounds is dropped (the
    /// accumulator resets to 0) rather than looped over further — reachable only by a truly
    /// pathological <c>dtMs</c>, never a realistic loading-screen hitch.</summary>
    internal const int MaxSentinelDrainSlotsPerTick = 20;

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

    /// <summary>Advances the shared accumulator; emits one sample per entity per 500 ms window OWED
    /// by <paramref name="dtMs"/> — a catch-up loop, not a single step (2026-08-26 grid-drift fix).
    /// A frame whose <c>dtMs</c> spans MULTIPLE 500 ms intervals (loading screens run at 1-10 Hz;
    /// hitches) must append ONE slot per owed interval, not one slot with the remainder silently
    /// zeroed — the old single-step drain shifted every LATER sample's nominal grid time earlier
    /// than it actually happened (owner-measured <c>gridDriftMs=2970</c> on one raid segment,
    /// recon §6). Each entity's live value is read ONCE per <see cref="Tick"/> call and repeated
    /// across that entity's real catch-up slots (see <see cref="MaxCatchUpSlotsPerTick"/>) — the
    /// game was frozen for the whole hitch, so there is only one true observation regardless of how
    /// many grid slots it spans; re-querying would not produce new information. An unusable read
    /// still appends the sentinel for EVERY owed slot, same semantics as before.</summary>
    internal void Tick(float dtMs)
    {
        _accumMs += dtMs;
        if (_accumMs < SampleIntervalMs) return;

        // Slot counts are computed BEFORE any entity read, so every entity reads its live value
        // exactly once this call regardless of how many slots the hitch spans (see doc above). Two
        // bounded loops, never one unbounded loop — see MaxCatchUpSlotsPerTick/
        // MaxSentinelDrainSlotsPerTick for why a pathological dtMs can never make this spin.
        var realSlots = 0;
        while (_accumMs >= SampleIntervalMs && realSlots < MaxCatchUpSlotsPerTick)
        {
            _accumMs -= SampleIntervalMs;
            realSlots++;
        }
        var sentinelSlots = 0;
        while (_accumMs >= SampleIntervalMs && sentinelSlots < MaxSentinelDrainSlotsPerTick)
        {
            _accumMs -= SampleIntervalMs;
            sentinelSlots++;
        }
        // Remainder beyond BOTH bounds (a truly extreme dtMs) — drop it rather than loop further.
        // Grid fidelity is already best-effort past this point; the whole point of the bounds is
        // that Tick never spins, regardless of how large dtMs is.
        if (_accumMs >= SampleIntervalMs) _accumMs = 0f;

        foreach (var kv in _entries)
        {
            var entry = kv.Value;
            if (entry.Pct.Count >= MaxSamplesPerEntity) continue;   // already at cap — skip the read entirely
            var (hp, maxHp) = _readHp(kv.Key);
            var usable = maxHp > 0;
            var raw = usable ? (int)Math.Round(100.0 * hp / maxHp) : 0;
            var pct = raw < 0 ? 0 : raw > 100 ? 100 : raw;

            // Real catch-up slots repeat this ONE read; the sentinel-drain remainder (if the hitch
            // exceeded MaxCatchUpSlotsPerTick) is always the sentinel regardless of usable — no
            // further reads are attempted for it (see class/Tick doc). MaxSamplesPerEntity is still
            // enforced per-slot, so a catch-up burst can't overshoot the cap.
            for (var i = 0; i < realSlots && entry.Pct.Count < MaxSamplesPerEntity; i++)
                entry.Pct.Add(usable ? pct : SentinelPct);
            for (var i = 0; i < sentinelSlots && entry.Pct.Count < MaxSamplesPerEntity; i++)
                entry.Pct.Add(SentinelPct);
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
    /// callers pass the death instant for parity with Track/the rest of the sampler API).
    ///
    /// I5 (2026-08-26 full-chain review): deliberately NOT capped by <see cref="MaxSamplesPerEntity"/>
    /// — unlike <see cref="Tick"/>, this appends AT MOST ONE sample per call (idempotency above
    /// guarantees that), so it cannot runaway-grow a track. With the L2 sentinel-grid fix a
    /// long-gappy track can legitimately SIT at the cap (every tick a sentinel), and the terminal
    /// death sample — the whole point of this method — must never be the one sample silently
    /// dropped by a cap meant to bound unbounded per-tick growth, not a single terminating write.</summary>
    internal void MarkDead(long entityId, long ms0)
    {
        if (!_entries.TryGetValue(entityId, out var e)) return;
        for (var i = e.Pct.Count - 1; i >= 0; i--)
        {
            if (e.Pct[i] == SentinelPct) continue;
            if (e.Pct[i] == 0) return;   // already terminated — idempotent no-op
            break;                       // last REAL sample is non-zero — fall through to append
        }
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
