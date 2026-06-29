// UNVERIFIED — this code has never been executed in-game.
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
/// Ring-bounded to <see cref="MaxEvents"/> to protect the heap in very long fights
/// (cap ~120 k events ≈ ~30 min dungeon at typical event rates).
/// All calls must originate on the Unity main thread (same thread as OnCombatEvent).
/// </remarks>
internal sealed class CombatEventBuffer
{
    // Safety ceiling: discard oldest events once exceeded (ring behaviour).
    internal const int MaxEvents = 120_000;

    private readonly List<CombatEvent> _raw = new(4096);

    internal int Count => _raw.Count;

    internal void Add(CombatEvent evt)
    {
        if (_raw.Count >= MaxEvents)
        {
            // Evict the oldest event to keep memory bounded.
            _raw.RemoveAt(0);
        }
        _raw.Add(evt);
    }

    internal void Clear() => _raw.Clear();

    /// <summary>
    /// Returns the captured events as StellarLogs DTOs and resets the buffer.
    /// Entity ids are formatted as their raw long value (same as the rest of the plugin).
    /// </summary>
    internal IReadOnlyList<CombatLogEvent> Flush()
    {
        var result = new List<CombatLogEvent>(_raw.Count);
        foreach (var ev in _raw)
            result.Add(Convert(ev));
        _raw.Clear();
        return result;
    }

    private static CombatLogEvent Convert(CombatEvent ev)
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

            _ => throw new InvalidOperationException($"Unexpected CombatEvent type: {ev.GetType().Name}"),
        };
    }
}
