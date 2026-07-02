// SP1: Full combat-event capture buffer. Holds the raw CombatEvent stream for a single run,
// then converts to the vendored StellarLogs wire format on flush.

using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>
/// Accumulates raw <see cref="CombatEvent"/> objects during a single encounter.
/// At run-end, <see cref="Flush"/> converts the buffered events to the
/// StellarLogs <see cref="CombatLogEvent"/> wire format and clears the buffer.
/// </summary>
/// <remarks>
/// Raw events are now a FORENSIC track only — totals/skills/series/deaths ride on the `derived`
/// aggregates (uncapped), so these caps no longer affect any rendered number. Dmg/skill and buff
/// events get SEPARATE budgets, each backed by an O(1) circular ring (no per-event O(n) shift),
/// so a buff flood can never evict damage. All calls must originate on the Unity main thread
/// (same thread as OnCombatEvent).
/// </remarks>
internal sealed class CombatEventBuffer
{
    // Kept SMALL to bound the upload blob: the ingest worker JSON.parse+ajv-validates the whole event
    // array, and a 70k-event (~10MB) blob exceeded the Worker resource limit (HTTP 503 / error 1102) in
    // real raids. Totals/skills/series/deaths ride on the uncapped `derived` aggregates, so raw events are
    // a forensic tail sample only — ~10k events ≈ ~1.5MB stays well under the limit.
    internal const int MaxDamageEvents = 8_000;    // dmg+skill forensic ring (tail sample)
    internal const int MaxBuffEvents   = 2_000;    // buffs on their own budget (preserved for future buff features)

    private readonly Ring _dmg = new(MaxDamageEvents);
    private readonly Ring _buff = new(MaxBuffEvents);

    /// <summary>True once the dmg/skill forensic ring has overflowed (metadata only — nothing rendered depends on raw events).</summary>
    internal bool Truncated { get; private set; }

    internal int Count => _dmg.Count + _buff.Count;

    internal void Add(CombatEvent evt)
    {
        if (evt is CombatEvent.BuffChanged) { _buff.Add(evt); return; }  // buff overflow is NOT flagged (nothing renders buffs)
        if (_dmg.Count >= _dmg.Capacity) Truncated = true;               // dmg/skill forensic ring overflowed
        _dmg.Add(evt);
    }

    internal void Clear() { _dmg.Clear(); _buff.Clear(); Truncated = false; }

    /// <summary>Count of events skipped during the most recent <see cref="Flush"/> because their
    /// <see cref="CombatEvent"/> case had no wire-format mapping (forward-compat safety net for a
    /// future framework event type — never throws). Callers should log a one-line warning when
    /// this is nonzero; this class has no logger of its own.</summary>
    internal int SkippedUnknownEvents { get; private set; }

    /// <summary>
    /// Returns the captured events as StellarLogs DTOs (chronologically merged) and resets the buffer.
    /// Entity ids are formatted as their raw long value (same as the rest of the plugin).
    /// </summary>
    internal IReadOnlyList<CombatLogEvent> Flush()
    {
        // Merge both rings, sort by timestamp so the uploaded stream is chronological.
        var merged = new List<CombatEvent>(_dmg.Count + _buff.Count);
        _dmg.CopyTo(merged); _buff.CopyTo(merged);
        merged.Sort((a, b) => a.TimestampMs.CompareTo(b.TimestampMs));
        var result = new List<CombatLogEvent>(merged.Count);
        var skipped = 0;
        foreach (var ev in merged)
        {
            var converted = Convert(ev);
            if (converted is null) { skipped++; continue; }   // unknown event type — skip, never throw
            result.Add(converted);
        }
        Clear();
        SkippedUnknownEvents = skipped;   // set AFTER Clear() so callers can read it post-Flush()
        return result;
    }

    private static CombatLogEvent? Convert(CombatEvent ev)
    {
        return ev switch
        {
            CombatEvent.SkillUsed su => new SkillEvent(
                su.TimestampMs,
                su.CasterId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                su.SkillId,
                (int)su.Phase),

            CombatEvent.DamageDealt d => new DamageEvent(
                d.TimestampMs,
                d.SourceId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                d.TargetId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                d.SkillId,
                d.Amount,
                d.ActualAmount,
                d.ShieldAbsorbed,
                d.IsCrit,
                d.IsLucky,
                d.IsHeal,
                d.IsDead,
                (int)d.Element,
                (int)d.SourceKind,
                // Source: no distinct wire field on DamageDealt beyond SourceKind; zero-fill.
                // TODO(SP1): if the wire exposes a secondary numeric source field, wire it here.
                0),

            CombatEvent.BuffChanged b => new BuffEvent(
                b.TimestampMs,
                b.TargetId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                b.BuffUuid,
                b.BaseId,
                b.Kind switch
                {
                    BuffChangeKind.Applied   => "applied",
                    BuffChangeKind.Refreshed => "refreshed",
                    BuffChangeKind.Removed   => "removed",
                    _                        => "applied",
                },
                b.Stacks,
                b.Layer,
                b.DurationMs),

            _ => null,   // unrecognized CombatEvent case — skip (never crash the game); caller logs the count
        };
    }

    // O(1) circular buffer: overwrites oldest when full.
    private sealed class Ring
    {
        private readonly CombatEvent[] _buf;
        private int _head;            // next write slot
        public int Count { get; private set; }
        public int Capacity => _buf.Length;
        public Ring(int cap) => _buf = new CombatEvent[cap];
        public void Add(CombatEvent e) { _buf[_head] = e; _head = (_head + 1) % _buf.Length; if (Count < _buf.Length) Count++; }
        public void Clear() { _head = 0; Count = 0; Array.Clear(_buf, 0, _buf.Length); }
        public void CopyTo(List<CombatEvent> dst)
        {
            // emit oldest-first
            int start = Count < _buf.Length ? 0 : _head;
            for (int i = 0; i < Count; i++) dst.Add(_buf[(start + i) % _buf.Length]);
        }
    }
}
