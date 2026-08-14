using Stellar.Abstractions.Domain;
using Stellar.CombatMeter.AutoArchive;
using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>
/// Pins the STICKY bucket-routing memory (owner-approved fix 2026-08-15, measured on staging run
/// <c>sea/jCFyDsx9uK</c>): damage that lands on a boss/elite AFTER it left the live set — a raid boss
/// scripted-killed and vanished while its DoTs and summons keep ticking — must still credit THAT
/// boss/elite's bucket, not <c>Other</c>. The measured run lost 13.6M + 13.2M of skill 3003213 off two
/// elites that way.
/// <para>Headless by the repo convention (Plugin cannot be instantiated in tests — see
/// <c>TargetBucketRoutingTests</c> / <c>SeenSummonSetTests</c>): the load-bearing state lives in the pure
/// <see cref="StickyBucketRoutes"/> class and the pure
/// <c>Plugin.RouteTargetBucketWithSticky</c> decision, and <see cref="Route"/> below reproduces
/// <c>Plugin.ResolveTargetBucket</c>'s composition of the three inputs exactly.</para>
/// <para>CAPTURE-ONLY, like every other surface in this area: nothing here touches the auto-archive
/// engine, BossStatus, the verdict, bossId/bosses[] or run identity (docs/recon/combatmeter-archive-flow.md).</para>
/// </summary>
public class StickyBucketRoutesTests
{
    private static EntityId Id(long v) => new(v);

    private const int SunfireCfg = 102800;
    private const int EliteCfg   = 55001;

    // Mirror of Plugin.ResolveTargetBucket (Plugin.BucketRouting.cs): probe both LIVE sets first and
    // only on a double miss consult the sticky map, then hand all three to the one pure decision.
    private static (bool isElite, int bucketKey) Route(
        StageBossSet bosses, EliteSet elites, StickyBucketRoutes sticky, EntityId id)
    {
        var boss  = bosses.TryGetConfigId(id, out var bossCfg);
        var elite = elites.TryGetConfigId(id, out var eliteCfg);
        var s = boss || elite ? default : sticky.Lookup(id);
        return Plugin.RouteTargetBucketWithSticky((boss, bossCfg), (elite, eliteCfg), s);
    }

    // ---------------------------------------------------------------------------------------------
    // The two regression pins — a hit AFTER the entity left the live set.
    // ---------------------------------------------------------------------------------------------

    /// <summary>THE MEASURED REGRESSION. An elite is admitted (and its route recorded); the live elite
    /// set later empties; a DoT tick still lands on it. Before the sticky map this routed to
    /// <c>Other</c>. NEVER WEAKEN THIS TEST.</summary>
    [Fact]
    public void A_hit_after_the_live_elite_set_drained_still_credits_that_elite()
    {
        var bosses = new StageBossSet();
        var elites = new EliteSet();
        var sticky = new StickyBucketRoutes();

        elites.Admit(Id(7), EliteCfg);
        sticky.Record(Id(7), isElite: true, EliteCfg);
        Assert.Equal((true, EliteCfg), Route(bosses, elites, sticky, Id(7)));   // live

        elites.Clear();                                                         // left the live set

        Assert.Equal((true, EliteCfg), Route(bosses, elites, sticky, Id(7)));   // still ITS bucket
    }

    /// <summary>Same shape for a BOSS, through the real drain that produces it: a raid boss is
    /// scripted-killed and vanishes, <c>DrainIfAllGone</c> empties the stage, and the DoTs already on it
    /// keep ticking for seconds. NEVER WEAKEN THIS TEST.</summary>
    [Fact]
    public void A_hit_after_DrainIfAllGone_still_credits_that_boss()
    {
        var bosses = new StageBossSet();
        var elites = new EliteSet();
        var sticky = new StickyBucketRoutes();

        bosses.Admit(Id(9), SunfireCfg);
        sticky.Record(Id(9), isElite: false, SunfireCfg);

        // Scripted death: HP never reads 0, the entity simply stops being present
        // (docs/recon/raid-clear-and-multiboss.md), and the stage drains.
        bosses.SetLiveness(Id(9), new StageBossSet.BossLiveness { Present = false, Dead = true });
        bosses.DrainIfAllGone();
        Assert.Equal(0, bosses.Count);

        var (isElite, key) = Route(bosses, elites, sticky, Id(9));
        Assert.False(isElite);                 // a boss never lands in the elite store
        Assert.Equal(SunfireCfg, key);
    }

    /// <summary>The no-loss floor is unchanged: an entity this run never ADMITTED (plain trash, an add,
    /// a totem) has no sticky entry and still lands in <c>Other</c> on the boss store — the sticky map
    /// only ever recovers hits on bosses/elites the capture channels themselves classified.</summary>
    [Fact]
    public void A_never_admitted_entity_still_routes_to_Other()
    {
        var sticky = new StickyBucketRoutes();
        sticky.Record(Id(9), isElite: false, SunfireCfg);   // a DIFFERENT entity is remembered

        var (isElite, key) = Route(new StageBossSet(), new EliteSet(), sticky, Id(4242));

        Assert.False(isElite);
        Assert.Equal(TargetBucketStats.OtherKey, key);
    }

    /// <summary>Live membership always wins, so this change is inert for every hit that already routed
    /// correctly — including the re-typed-boss overlap, where the sticky map is not even consulted.</summary>
    [Fact]
    public void Live_membership_wins_over_the_sticky_map()
    {
        var bosses = new StageBossSet();
        var elites = new EliteSet();
        var sticky = new StickyBucketRoutes();

        bosses.Admit(Id(9), SunfireCfg);
        elites.Admit(Id(9), EliteCfg);            // re-typed overlap: tracked on BOTH
        sticky.Record(Id(9), isElite: true, EliteCfg);   // a stale/elite-first memory must not win

        Assert.Equal((false, SunfireCfg), Route(bosses, elites, sticky, Id(9)));
    }

    // ---------------------------------------------------------------------------------------------
    // Lifecycle: run boundary forgets, an archive must NOT.
    // ---------------------------------------------------------------------------------------------

    /// <summary>The reset rule, pinned by its OBSERVABLE consequence in both directions (the placement
    /// itself — <c>_stickyRoutes.Clear()</c> in <c>ResetRunScopedTrackers</c>, Plugin.RunBoundary.cs, and
    /// deliberately absent from <c>Clear()</c>, Plugin.cs — is instance glue that no headless test can
    /// reach, same convention as <c>BankRunBoundary</c>'s own documented glue gap).
    /// <list type="bullet">
    /// <item>ARCHIVE (<c>Clear()</c>): buckets and <c>_stats</c> reset together, the sticky map does NOT
    /// — a boss cut banks at the kill while the DoT tail keeps ticking into the NEXT segment, and those
    /// ticks must still credit that boss. Clearing per-archive would re-open the exact "Other" leak.</item>
    /// <item>RUN BOUNDARY (<c>ResetRunScopedTrackers</c>): the map is dropped with the live sets — a new
    /// run's entities are new entities, and a recycled entity id must never credit a boss/elite that is
    /// not in this instance.</item>
    /// </list></summary>
    [Fact]
    public void An_archive_keeps_the_route_and_a_run_boundary_forgets_it()
    {
        var bosses = new StageBossSet();
        var elites = new EliteSet();
        var sticky = new StickyBucketRoutes();

        bosses.Admit(Id(9), SunfireCfg);
        sticky.Record(Id(9), isElite: false, SunfireCfg);
        bosses.SetLiveness(Id(9), new StageBossSet.BossLiveness { Present = false, Dead = true });
        bosses.DrainIfAllGone();

        // --- archive (Clear()): _stats/buckets reset; the sticky map is untouched ---
        Assert.Equal((false, SunfireCfg), Route(bosses, elites, sticky, Id(9)));
        Assert.Equal(1, sticky.Count);

        // --- run boundary (ResetRunScopedTrackers): live sets AND the map all drop together ---
        bosses.Clear();
        elites.Clear();
        sticky.Clear();

        Assert.Equal(0, sticky.Count);
        Assert.Equal((false, TargetBucketStats.OtherKey), Route(bosses, elites, sticky, Id(9)));
    }

    // ---------------------------------------------------------------------------------------------
    // §7.2 no-loss: routing picks a KEY, never whether a hit accrues.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Σbuckets == totals is unchanged by sticky routing: every hit still routes to exactly one
    /// (store, key) pair, so the sums the emission seam pins
    /// (<c>DerivedBucketsTests.Emitted_bucket_sums_equal_the_emitted_actor_totals_per_channel</c>) cannot
    /// move — only WHICH key inside those sums a hit lands on. Driven through the real stores across all
    /// three routing outcomes (live, sticky, unknown). NEVER WEAKEN THIS TEST.</summary>
    [Fact]
    public void Sticky_routing_moves_hits_between_keys_and_never_loses_one()
    {
        var bosses = new StageBossSet();
        var elites = new EliteSet();
        var sticky = new StickyBucketRoutes();
        var bossStore  = new TargetBucketStats(1000, 4096);
        var eliteStore = new TargetBucketStats(1000, 4096);
        var player = Id(1);

        bosses.Admit(Id(9), SunfireCfg);
        sticky.Record(Id(9), isElite: false, SunfireCfg);
        elites.Admit(Id(7), EliteCfg);
        sticky.Record(Id(7), isElite: true, EliteCfg);

        long total = 0;
        void Hit(EntityId target, long amount)
        {
            var (isElite, key) = Route(bosses, elites, sticky, target);
            (isElite ? eliteStore : bossStore).AddDealt(player, key, skillId: 3003213, amount, crit: false, ms: 0);
            total += amount;
        }

        Hit(Id(9), 100);       // live boss
        Hit(Id(7), 50);        // live elite
        Hit(Id(4242), 5);      // never admitted → Other

        bosses.SetLiveness(Id(9), new StageBossSet.BossLiveness { Present = false, Dead = true });
        bosses.DrainIfAllGone();
        elites.Clear();

        Hit(Id(9), 13_600_000);   // post-death DoT tail on the boss  → sticky
        Hit(Id(7), 13_200_000);   // post-death DoT tail on the elite → sticky
        Hit(Id(4242), 7);         // still Other

        long summed = 0;
        foreach (var buckets in bossStore.Snapshot()[player].Values) summed += buckets.DealtTotal;
        foreach (var buckets in eliteStore.Snapshot()[player].Values) summed += buckets.DealtTotal;
        Assert.Equal(total, summed);

        // And the tails really landed on their own keys rather than in Other.
        Assert.Equal(13_600_100, bossStore.Snapshot()[player][SunfireCfg].DealtTotal);
        Assert.Equal(13_200_050, eliteStore.Snapshot()[player][EliteCfg].DealtTotal);
        Assert.Equal(12, bossStore.Snapshot()[player][TargetBucketStats.OtherKey].DealtTotal);
    }

    // ---------------------------------------------------------------------------------------------
    // The bounded set itself (mirrors KilledBossTrackerTests / SeenSummonSetTests).
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void An_unrecorded_id_is_not_known()
    {
        var s = new StickyBucketRoutes();
        Assert.Equal((false, false, 0), s.Lookup(Id(1)));
    }

    [Fact]
    public void A_zero_entity_id_is_never_recorded()
    {
        var s = new StickyBucketRoutes();
        s.Record(Id(0), isElite: false, SunfireCfg);
        Assert.Equal(0, s.Count);
    }

    [Fact]
    public void A_recorded_boss_is_never_downgraded_to_an_elite()
    {
        // Precedence must survive re-admission churn exactly as RouteTargetBucket states it: a real boss
        // can be RE-TYPED to Elite in event content, and the surface it was TRACKED on wins.
        var s = new StickyBucketRoutes();
        s.Record(Id(9), isElite: false, SunfireCfg);
        s.Record(Id(9), isElite: true, EliteCfg);

        Assert.Equal((true, false, SunfireCfg), s.Lookup(Id(9)));
        Assert.Equal(1, s.Count);
    }

    [Fact]
    public void An_elite_later_seen_as_a_boss_upgrades_in_place()
    {
        var s = new StickyBucketRoutes();
        s.Record(Id(9), isElite: true, EliteCfg);
        s.Record(Id(9), isElite: false, SunfireCfg);

        Assert.Equal((true, false, SunfireCfg), s.Lookup(Id(9)));
        Assert.Equal(1, s.Count);   // rewritten in place — no second FIFO slot for one id
    }

    [Fact]
    public void At_capacity_the_oldest_route_is_evicted()
    {
        // FIFO, oldest out — post-death DoT tails are always about a RECENT boss/elite, so keeping the
        // newest routes is the fail direction that preserves the entries doing the work.
        var s = new StickyBucketRoutes();
        for (long i = 1; i <= StickyBucketRoutes.MaxEntries; i++) s.Record(Id(i), isElite: false, (int)i);

        s.Record(Id(StickyBucketRoutes.MaxEntries + 1), isElite: false, 999);

        Assert.Equal(StickyBucketRoutes.MaxEntries, s.Count);
        Assert.False(s.Lookup(Id(1)).Known);                                          // oldest evicted
        Assert.Equal((true, false, 999), s.Lookup(Id(StickyBucketRoutes.MaxEntries + 1)));
        Assert.Equal((true, false, (int)StickyBucketRoutes.MaxEntries), s.Lookup(Id(StickyBucketRoutes.MaxEntries)));
    }

    [Fact]
    public void Count_never_exceeds_MaxEntries()
    {
        var s = new StickyBucketRoutes();
        for (long i = 1; i <= StickyBucketRoutes.MaxEntries * 3; i++)
        {
            s.Record(Id(i), isElite: i % 2 == 0, (int)i);
            Assert.True(s.Count <= StickyBucketRoutes.MaxEntries);
        }
        Assert.Equal(StickyBucketRoutes.MaxEntries, s.Count);
    }

    [Fact]
    public void Clear_forgets_everything()
    {
        var s = new StickyBucketRoutes();
        s.Record(Id(9), isElite: false, SunfireCfg);
        s.Record(Id(7), isElite: true, EliteCfg);

        s.Clear();

        Assert.Equal(0, s.Count);
        Assert.False(s.Lookup(Id(9)).Known);

        // The queue was cleared in lock-step with the map: capacity is fully available again.
        for (long i = 1; i <= StickyBucketRoutes.MaxEntries; i++) s.Record(Id(i), isElite: false, (int)i);
        Assert.Equal(StickyBucketRoutes.MaxEntries, s.Count);
        Assert.True(s.Lookup(Id(1)).Known);
    }
}
