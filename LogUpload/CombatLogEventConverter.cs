using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>
/// Pure mapping from a captured <see cref="CombatEvent"/> to its StellarLogs wire DTO
/// (<see cref="CombatLogEvent"/>). Extracted from the capture buffer so the mapping can be unit-tested
/// independent of the buffering machinery (now <see cref="EventSpool"/>). Entity ids are formatted as
/// their raw long value (same convention as the rest of the plugin).
/// </summary>
internal static class CombatLogEventConverter
{
    internal static CombatLogEvent? Convert(CombatEvent ev, SummonOwnerMap? owners = null)
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
                b.DurationMs,
                b.FirerId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                b.SourceKind,
                b.SourceId,
                SrcOwnerOf(b.FirerId, owners)),

            _ => null,   // unrecognized CombatEvent case — skip (never crash the game); caller logs the count
        };
    }

    // Present ONLY when the firer is a known player summon — a bare player/monster firer writes nothing.
    private static string? SrcOwnerOf(EntityId firer, SummonOwnerMap? owners)
    {
        if (owners is null) return null;
        var owner = owners.OwnerOf(firer);
        return owner == firer ? null : owner.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
