using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter.AutoArchive;

/// <summary>
/// Bosses whose fight has already been observed dead THIS RUN. A corpse must never re-open a boss
/// segment: without this, the death pulse re-armed the latch, CheckBossCandidate re-adopted the dead
/// boss (still cached isBoss=true in Plugin's _bossCheck), the inline cut fired, and the next engine
/// tick observed the same corpse as dead again — one 0 ms archive per turn of that loop (owner runs
/// sea/696115723671437312, sea/420833196448415744, archives 1 s apart). Keyed on the ENTITY uuid, so a
/// respawned boss with a fresh uuid is a NEW identity and cuts normally.
/// <para><b>Extracted as a pure class (review round, 2026-07-26)</b> — same reasoning as
/// <see cref="AutoArchiveEngine"/> and <c>ReplayCaptureGate</c>: <c>Plugin</c> cannot be instantiated in
/// tests (it needs the IL2CPP service surface), so the load-bearing wiring — mark, consult, evict — has
/// to live somewhere headless-testable, not in a plugin field.</para>
/// <para><b>Cap behaviour is FIFO, evicting the OLDEST (not the newest)</b> — this is the whole point of
/// the fix. The loop this class exists to break is only reachable through a corpse still emitting
/// combat events, which is always a RECENT kill; keeping the newest marks is what actually holds the
/// line. An earlier version dropped the NEW mark at capacity (<c>if (... &amp;&amp; Count &lt;
/// Max) Add(...)</c>) — that fails OPEN: the corpse that just triggered saturation would itself go
/// unmarked and become re-adoptable, reopening the exact loop this class closes. (Contrast the sibling
/// boss-lookup cache, Plugin.BossDetection.cs's <c>_bossCheck</c> (moved there from Plugin.AutoArchive.cs
/// by the Minor E extraction, review round 2026-07-27 second pass), which fails CLOSED — dropping a new
/// entry there only means one non-boss id gets re-resolved next time, never a re-adoption.) BOUNDED (the
/// FPS-leak lesson) and cleared at the scene boundary (<c>Plugin.OnSceneChanged</c>) — NOT on every
/// archive, which would make the meter forget which bosses are already dead mid-run.</para>
/// </summary>
internal sealed class KilledBossTracker
{
    internal const int MaxEntries = 64;

    // _seen is the O(1) membership test; _order is insertion order for FIFO eviction. Kept in lock-step
    // on every path (Add only ever happens together with Enqueue; Remove only together with Dequeue).
    private readonly HashSet<EntityId> _seen = new();
    private readonly Queue<EntityId> _order = new();

    internal int Count => _seen.Count;

    /// <summary>Record a confirmed-dead boss. Idempotent — marking an already-marked id is a no-op (does
    /// not grow the collection or move it in eviction order). Returns the id evicted to make room, or
    /// null when no eviction was needed.</summary>
    internal EntityId? MarkKilled(EntityId id)
    {
        if (!_seen.Add(id)) return null;   // already marked — no growth, no re-queue
        _order.Enqueue(id);
        if (_seen.Count <= MaxEntries) return null;
        var evicted = _order.Dequeue();
        _seen.Remove(evicted);
        return evicted;
    }

    internal bool IsKilled(EntityId id) => _seen.Contains(id);

    internal void Clear()
    {
        _seen.Clear();
        _order.Clear();
    }
}
