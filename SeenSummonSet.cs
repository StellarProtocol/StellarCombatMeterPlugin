using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter;

/// <summary>
/// Summon ENTITY ids already observed appearing THIS RUN — the novelty gate for the appear-sourced
/// imagine-cast channel (<c>Plugin.Capture.cs</c>, <c>TryRecordImagineCastFromAppear</c>). The same
/// summon entity re-appearing (an AOI blink, the owner running back into view, a scene-cell
/// re-entry) is NEVER a new cast; only a fresh entity uuid — i.e. a fresh summon spawn — is a cast
/// candidate. Keyed on the ENTITY uuid deliberately: a re-CAST spawns a new entity with a new uuid
/// and passes, while every re-appear of the same entity carries the same uuid and is rejected.
/// <para><b>Mirrors <c>AutoArchive.KilledBossTracker</c></b> (the repo's precedent for a bounded id
/// set) rather than reusing it: the mechanism is identical (HashSet membership + Queue insertion
/// order, FIFO oldest-out at the cap) but MarkKilled/IsKilled naming on a summon-novelty set would
/// mislead readers near the PROTECTED boss/archive code, and this set must stay visibly decoupled
/// from that machinery (capture-only channel — feeds nothing in AutoArchive/BossStatus/verdict
/// paths). Eviction keeps the NEWEST marks, same rationale as the tracker: the duplicate-appear
/// hazard (a persistent companion blinking in and out of AOI) is always about a RECENT summon;
/// evicting the oldest is the fail direction that preserves the marks doing the work.</para>
/// <para>BOUNDED (the FPS-leak lesson) and RUN-scoped: cleared by <c>ResetRunScopedTrackers</c>
/// (Plugin.RunBoundary.cs) alongside the other run-scoped trackers — NOT by <c>Clear()</c>, which
/// runs on every archive and would let a long-lived companion re-record one phantom cast per
/// mid-run archive boundary.</para>
/// </summary>
internal sealed class SeenSummonSet
{
    internal const int MaxEntries = 64;

    // _seen is the O(1) membership test; _order is insertion order for FIFO eviction. Kept in
    // lock-step on every path (Add only ever happens together with Enqueue; Remove with Dequeue).
    private readonly HashSet<EntityId> _seen = new();
    private readonly Queue<EntityId> _order = new();

    internal int Count => _seen.Count;

    /// <summary>Record a summon-entity sighting. Returns TRUE when the id is NOVEL (first sighting
    /// this run — a cast candidate); FALSE when it was already seen (a re-appear, never a cast).
    /// At capacity the OLDEST mark is evicted to make room for the new one.</summary>
    internal bool MarkSeen(EntityId id)
    {
        if (!_seen.Add(id)) return false;   // already seen — no growth, no re-queue
        _order.Enqueue(id);
        if (_seen.Count > MaxEntries) _seen.Remove(_order.Dequeue());
        return true;
    }

    internal void Clear()
    {
        _seen.Clear();
        _order.Clear();
    }
}
