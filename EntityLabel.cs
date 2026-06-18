using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

/// <summary>
/// Pure label-resolution helper for damage-source rows. Lifted out of
/// <see cref="Plugin"/> so the fallback chain is testable without an active
/// Unity / IL2CPP host.
///
/// <para>
/// Resolution order (matches the live behavior used by <c>DrawSourceRow</c>):
/// <list type="number">
///   <item><description>If the entity is the local player and <see cref="IPlayerState.Name"/> is non-empty, use the live PlayerState name.</description></item>
///   <item><description>Roster name: the party member whose <c>CharId</c> matches <c>id.Uid</c> and whose <c>Name</c> is non-empty.</description></item>
///   <item><description>Otherwise the combat cache name (populated from <c>AttrName</c> observations).</description></item>
///   <item><description>For the local player only, fall back to the literal <c>"Self"</c>.</description></item>
///   <item><description>For other entities, synthesize <c>Player#&lt;uid&gt;</c> / <c>Mob#&lt;uid&gt;</c> / <c>Entity#&lt;uid&gt;</c> by entity-id classification.</description></item>
/// </list>
/// </para>
/// </summary>
internal static class EntityLabel
{
    public static string Resolve(
        EntityId id, EntityId self, IPlayerState player, ICombatLookup combat, IReadOnlyList<PartyMember> roster)
    {
        if (id == self)
        {
            var localName = player.Name;
            if (!string.IsNullOrEmpty(localName)) return localName!;
        }

        var rosterName = RosterName(id, roster);
        if (!string.IsNullOrEmpty(rosterName)) return rosterName!;

        var name = combat.GetEntityName(id);
        if (!string.IsNullOrEmpty(name)) return name!;

        if (id == self) return "Self";
        if (id.IsPlayer)  return $"Player#{id.Uid}";
        if (id.IsMonster) return $"Mob#{id.Uid}";
        return $"Entity#{id.Uid}";
    }

    private static string? RosterName(EntityId id, IReadOnlyList<PartyMember> roster)
    {
        long charId = id.Uid;
        for (int i = 0; i < roster.Count; i++)
            if (roster[i].CharId == charId && !string.IsNullOrEmpty(roster[i].Name))
                return roster[i].Name;
        return null;
    }
}
