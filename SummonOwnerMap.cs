using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter;

/// <summary>Run-scoped summon → summoner map fed by <c>CombatEvent.EntitySummonAppeared</c> (player summoners
/// only). Resolves a buff's firer to the player who owns it (spec § 5.2/§ 6.8 — Tina's Lower CD is
/// NekoChan's). Bounded FIFO like <see cref="SeenSummonSet"/>; cleared with it at the run boundary.
/// TRACKING, not a number: recorded through pause. CAPTURE ONLY — feeds nothing in archive/verdict paths.</summary>
internal sealed class SummonOwnerMap
{
    internal const int MaxEntries = 256;

    private readonly Dictionary<EntityId, EntityId> _owner = new();
    private readonly Queue<EntityId> _order = new();

    internal int Count => _owner.Count;

    internal void Record(EntityId summon, EntityId owner)
    {
        if (_owner.ContainsKey(summon)) { _owner[summon] = owner; return; }
        _owner[summon] = owner;
        _order.Enqueue(summon);
        if (_owner.Count > MaxEntries) _owner.Remove(_order.Dequeue());
    }

    /// <summary>The owning player for a known summon; the id itself otherwise (a player, a real monster, None).</summary>
    internal EntityId OwnerOf(EntityId id) => _owner.TryGetValue(id, out var o) ? o : id;

    internal void Clear() { _owner.Clear(); _order.Clear(); }
}
