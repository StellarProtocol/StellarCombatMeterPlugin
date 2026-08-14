using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter;

// ------------------------------------------------------------------------------------------------
// Per-boss/per-elite statistics — CAPTURE-ONLY bucket routing (Spec B,
// docs/superpowers/specs/2026-08-14-per-boss-statistics-design.md §3). Split out of Plugin.Capture.cs
// (2026-08-15, at the 500-LoC file cap) into its own partial so the sticky-routing memory below has
// room to carry its rationale; behaviour of the moved code is byte-identical.
//
// Split boss vs elite exactly like bosses[]/elites[] (owner ruling 2026-08-13: elites never reach boss
// surfaces). Routing READS _stageBosses/_eliteSet/_stickyRoutes and feeds NOTHING back into
// AutoArchiveEngine, BossStatus, the verdict, junk suppression, bossId/bosses[], or run identity — the
// whole-fight _stats stay the single source of truth for every existing surface (archive-flow
// invariants untouched: docs/recon/combatmeter-archive-flow.md). Bucket width/cap are the whole-fight
// timeline constants and the anchor is the same (combat start), so a per-bucket series starts out
// aligned with the whole-fight one. It does NOT stay aligned by construction: each cell owns its own
// SourceTimeline and coalesces INDEPENDENTLY (its BucketMs doubles when THAT cell outruns the cap), so
// on a very long fight cadences can diverge — they are normalized to one bucketMs at emission
// (LogUpload/DerivedBucketBuilder.ResolveBucketMs), which is what makes the site's chart swap
// like-for-like.
// ------------------------------------------------------------------------------------------------
public sealed partial class Plugin
{
    private readonly TargetBucketStats _bossBuckets  = new(TimelineBucketMs, TimelineMaxBuckets);
    private readonly TargetBucketStats _eliteBuckets = new(TimelineBucketMs, TimelineMaxBuckets);

    // Run-scoped sticky routing memory (owner-approved fix 2026-08-15, measured on staging run
    // sea/jCFyDsx9uK): the third and LAST routing input, consulted only when both live sets miss. See
    // StickyBucketRoutes' own doc for the defect, the bound, and why it is cleared at the run boundary
    // (ResetRunScopedTrackers, Plugin.RunBoundary.cs) and NEVER by Clear().
    private readonly StickyBucketRoutes _stickyRoutes = new();

    /// <summary>Pure bucket precedence among the LIVE sets: a tracked stage boss wins; else a tracked
    /// elite (routed to its OWN store via <c>isElite</c>); else <see cref="TargetBucketStats.OtherKey"/>
    /// on the boss store. Boss beats elite because a real boss can be RE-TYPED to Elite in
    /// event/rotation content (<c>MonsterType</c> is not stable per name —
    /// docs/recon/raid-clear-and-multiboss.md), and the surface it is TRACKED on must win. Pinned
    /// headless (Plugin cannot be instantiated in tests — repo pattern, see
    /// <see cref="ObserveBurstHit"/>).</summary>
    internal static (bool isElite, int bucketKey) RouteTargetBucket(
        bool bossMember, int bossConfigId, bool eliteMember, int eliteConfigId)
    {
        if (bossMember) return (false, bossConfigId);
        if (eliteMember) return (true, eliteConfigId);
        return (false, TargetBucketStats.OtherKey);
    }

    /// <summary>Full pure routing decision — <b>live sets FIRST, then the sticky map, then Other</b>
    /// (owner-approved 2026-08-15).
    /// <para>Live-first is what keeps this change inert for every hit that already routed correctly: while
    /// an entity is still a live member the sticky map is not even consulted, so <see cref="RouteTargetBucket"/>
    /// alone decides (including the boss-beats-elite overlap). The sticky branch only ever converts a hit
    /// that would have become <c>Other</c> into the bucket of a boss/elite THIS RUN ALREADY ADMITTED —
    /// the post-scripted-death / post-drain DoT and summon tail that the measured run lost.</para>
    /// <para>An entity never admitted this run has no sticky entry and still lands in
    /// <c>Other</c> (§7.2 no-loss: every hit routes SOMEWHERE, so Σbuckets == totals is unchanged —
    /// routing only picks a key, it never decides whether a hit accrues).</para>
    /// Tuple parameters keep this at three arguments rather than seven (STELLAR0003 / the >5-parameter
    /// standard). Pinned headless.</summary>
    internal static (bool isElite, int bucketKey) RouteTargetBucketWithSticky(
        (bool member, int configId) boss,
        (bool member, int configId) elite,
        (bool known, bool isElite, int configId) sticky)
    {
        if (boss.member || elite.member)
            return RouteTargetBucket(boss.member, boss.configId, elite.member, elite.configId);
        if (sticky.known) return (sticky.isElite, sticky.configId);
        return (false, TargetBucketStats.OtherKey);
    }

    // Both live sets are probed (not short-circuited on the boss hit) so RouteTargetBucket alone decides
    // precedence — the overlap case is a re-typed boss, not a theoretical one. Alloc-free: two bounded
    // MaxMembers scans plus — only when BOTH miss — one dictionary probe, no interop call and no
    // admission logic (membership is whatever the engine and the elite channel already admitted). The
    // `default` skips the sticky probe on the common live-member path; it reads as (known:false, …), so
    // the pure function above is still the single place the precedence is written down.
    private (bool isElite, int bucketKey) ResolveTargetBucket(EntityId id)
    {
        var boss  = _stageBosses.TryGetConfigId(id, out var bossConfigId);
        var elite = _eliteSet.TryGetConfigId(id, out var eliteConfigId);
        var sticky = boss || elite ? default : _stickyRoutes.Lookup(id);
        return RouteTargetBucketWithSticky((boss, bossConfigId), (elite, eliteConfigId), sticky);
    }
}
