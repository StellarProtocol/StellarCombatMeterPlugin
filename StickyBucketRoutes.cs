using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter;

/// <summary>
/// Run-scoped ROUTING MEMORY for the per-boss/per-elite stat buckets: every entity ever ADMITTED as a
/// stage boss (<c>StageBossSet</c>) or an elite (<c>EliteSet</c>) this run, remembered as
/// (isElite, configId) so a hit landing on it AFTER it left the live set still credits ITS bucket.
/// <para><b>The measured defect this fixes</b> (owner-approved 2026-08-15, staging run
/// <c>sea/jCFyDsx9uK</c>): the live sets are the only routing input <c>ResolveTargetBucket</c> used to
/// have, and both DRAIN — <c>StageBossSet.DrainIfAllGone</c> when no member is present, and both sets at
/// the run boundary. But damage keeps arriving on a boss/elite after it stops being live: raid bosses die
/// by a SCRIPTED event and simply VANISH (their HP never reads 0 —
/// docs/recon/raid-clear-and-multiboss.md), while the DoTs and summons already on them keep ticking for
/// seconds afterwards. Every one of those ticks fell through to <c>TargetBucketStats.OtherKey</c>: the
/// measured run lost 13.6M + 13.2M of skill 3003213 off the two elites into "Other".</para>
/// <para><b>CAPTURE-ONLY.</b> Same contract as the buckets it feeds (Spec B,
/// docs/superpowers/specs/2026-08-14-per-boss-statistics-design.md §3) and as the elite channel it serves
/// (owner ruling 2026-08-13): this map is READ by bucket routing and by nothing else. It feeds NOTHING
/// into <c>AutoArchiveEngine</c>, <c>BossStatus</c>, the run verdict, junk suppression,
/// <c>bossId</c>/<c>bosses[]</c>/<c>elites[]</c>, or run identity — routing only ever changes WHICH key a
/// hit accrues under, never WHETHER it accrues, so Σbuckets == totals is untouched by construction.</para>
/// <para><b>BOUNDED</b> (the FPS-leak lesson) at <see cref="MaxEntries"/>, FIFO oldest-eviction —
/// mechanism copied from <c>AutoArchive.KilledBossTracker</c> / <c>SeenSummonSet</c>, the repo's two
/// precedents for a bounded id set, with a Dictionary instead of a HashSet because this one carries a
/// payload. Evicting the OLDEST is the right fail direction here too: post-death DoT tails are always
/// about a RECENT boss/elite, so the entries still doing work are the newest ones. 64 entries is far
/// above any real stage (both live sets cap at 32 members each).</para>
/// <para><b>RUN-scoped, NEVER per-archive.</b> Cleared by <c>ResetRunScopedTrackers</c>
/// (Plugin.RunBoundary.cs) beside <c>_killedBosses</c> / <c>_stageBosses</c> / <c>_eliteSet</c> /
/// <c>_seenSummons</c> — and deliberately NOT by <c>Clear()</c>, which runs on every archive: a boss cut
/// banks at the kill, and the DoT ticks that land on that corpse belong to the NEXT segment's buckets
/// under the SAME boss key. Clearing per-archive would re-introduce the exact "Other" leak for every
/// post-kill tail.</para>
/// </summary>
internal sealed class StickyBucketRoutes
{
    internal const int MaxEntries = 64;

    // _routes is the O(1) lookup; _order is insertion order for FIFO eviction. Kept in lock-step: a key
    // is only ever added together with an Enqueue, and removed only via the Dequeue below.
    private readonly Dictionary<EntityId, (bool IsElite, int ConfigId)> _routes = new();
    private readonly Queue<EntityId> _order = new();

    internal int Count => _routes.Count;

    /// <summary>Remember this entity's bucket at ADMISSION time. Called from the two admission sites —
    /// <c>CheckBossCandidate</c> (Plugin.BossDetection.cs) and <c>CheckEliteCandidate</c>
    /// (Plugin.EliteDetection.cs) — right after their set accepted the member, so the map can only ever
    /// hold ids the capture channels themselves already classified.
    /// <para>A recorded BOSS is never downgraded to an elite, mirroring <c>RouteTargetBucket</c>'s
    /// precedence: a real boss can be RE-TYPED to Elite in event/rotation content (<c>MonsterType</c> is
    /// not stable per name — docs/recon/raid-clear-and-multiboss.md) and the surface it was TRACKED on
    /// must win. An elite→boss upgrade rewrites in place (no re-queue: the id already holds its FIFO
    /// slot, and re-queuing would let one id occupy two slots and silently shrink capacity).</para></summary>
    internal void Record(EntityId id, bool isElite, int configId)
    {
        if (id.Value == 0) return;
        if (_routes.TryGetValue(id, out var existing))
        {
            if (existing.IsElite && !isElite) _routes[id] = (false, configId);   // elite → boss upgrade only
            return;
        }
        _routes[id] = (isElite, configId);
        _order.Enqueue(id);
        if (_routes.Count > MaxEntries) _routes.Remove(_order.Dequeue());
    }

    /// <summary>Hot-path lookup (one dictionary probe, no allocation — the tuple is a value type).
    /// <c>Known == false</c> means this entity was never admitted this run, which is what keeps
    /// never-admitted trash in <c>TargetBucketStats.OtherKey</c>.</summary>
    internal (bool Known, bool IsElite, int ConfigId) Lookup(EntityId id)
        => _routes.TryGetValue(id, out var r) ? (true, r.IsElite, r.ConfigId) : (false, false, 0);

    internal void Clear()
    {
        _routes.Clear();
        _order.Clear();
    }
}
