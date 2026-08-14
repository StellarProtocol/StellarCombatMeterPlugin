// Spec B task 3 — the per-boss/per-elite bucket stores latch onto the history entry and ship in the
// `derived` block. These pin the EMISSION seam: the IL2CPP capture call sites (Plugin.Capture.cs)
// cannot be instantiated headless, so DerivedBuilder.Build is the closest testable boundary to the
// wire — and it is where §7's no-loss invariants become checkable (sums equal totals, nothing is
// dropped, the six maps are absent-not-empty on a bucketless run).
// Spec: docs/superpowers/specs/2026-08-14-per-boss-statistics-design.md §4.2/§7/§8.

using System;
using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public sealed class DerivedBucketsTests
{
    private static EntityId E(long v) => new(v);
    private static string K(long v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private const long PlayerA = 1001L << 16;
    private const long PlayerB = 2002L << 16;
    private const int  Sunfire = 102800;
    private const int  Moonstrike = 102801;
    private const int  EliteId = 555;

    private static Plugin.EncounterHistoryEntry Entry(
        Dictionary<EntityId, SourceStats>? stats = null,
        Dictionary<EntityId, SourceSeries>? series = null,
        TargetBucketStats? boss = null,
        TargetBucketStats? elite = null)
        => new()
        {
            CombatDurationMs = 10_000,
            Stats  = stats  ?? new Dictionary<EntityId, SourceStats>(),
            Series = series ?? new Dictionary<EntityId, SourceSeries>(),
            BossBuckets  = boss?.Snapshot()  ?? EmptyBuckets(),
            EliteBuckets = elite?.Snapshot() ?? EmptyBuckets(),
        };

    private static IReadOnlyDictionary<EntityId, IReadOnlyDictionary<int, TargetBucketStats.BucketSnapshot>> EmptyBuckets()
        => new Dictionary<EntityId, IReadOnlyDictionary<int, TargetBucketStats.BucketSnapshot>>();

    // Hand-built cell — used where the fixture must pin a specific per-cell SeriesBucketMs (the
    // independent-coalesce case a real store only reaches after ~30 min of fight).
    private static IReadOnlyDictionary<EntityId, IReadOnlyDictionary<int, TargetBucketStats.BucketSnapshot>> Cells(
        EntityId player, params (int Key, TargetBucketStats.BucketSnapshot Snap)[] cells)
    {
        var inner = new Dictionary<int, TargetBucketStats.BucketSnapshot>();
        foreach (var (key, snap) in cells) inner[key] = snap;
        return new Dictionary<EntityId, IReadOnlyDictionary<int, TargetBucketStats.BucketSnapshot>> { [player] = inner };
    }

    private static TargetBucketStats.BucketSnapshot Cell(
        long dealt = 0, long taken = 0, long[]? dealtSeries = null, long[]? takenSeries = null,
        int seriesBucketMs = 1000, IReadOnlyList<(int, long, int, int)>? skills = null)
        => new(dealt, taken,
            skills ?? Array.Empty<(int, long, int, int)>(),
            dealtSeries ?? Array.Empty<long>(),
            takenSeries ?? Array.Empty<long>(),
            seriesBucketMs);

    // -------------------------------------------------------------------------
    // Absence (§7.5 no surface regression): a run with no buckets emits NO bucket
    // fields at all — the stored derived block must stay byte-identical to today's.
    // -------------------------------------------------------------------------

    [Fact]
    public void Derived_omits_all_six_bucket_maps_when_both_stores_are_empty()
    {
        var d = DerivedBuilder.Build(Entry(), truncatedEvents: false);

        Assert.Null(d.PerActorBossDealt);
        Assert.Null(d.PerActorBossTaken);
        Assert.Null(d.PerActorBossSeries);
        Assert.Null(d.PerActorEliteDealt);
        Assert.Null(d.PerActorEliteTaken);
        Assert.Null(d.PerActorEliteSeries);

        var json = WriteLog(d);
        Assert.DoesNotContain("perActorBoss", json);
        Assert.DoesNotContain("perActorElite", json);
    }

    // -------------------------------------------------------------------------
    // Shape (§4.2): uid string keys, "<configId>" bucket keys, "other" for OtherKey,
    // per-skill rows in the whole-fight SkillAgg shape.
    // -------------------------------------------------------------------------

    [Fact]
    public void Derived_emits_boss_buckets_keyed_by_config_id_with_skills_and_other()
    {
        var boss = new TargetBucketStats(1000, 4096);
        boss.AddDealt(E(PlayerA), Sunfire, skillId: 7, amount: 100, crit: false, ms: 0);
        boss.AddDealt(E(PlayerA), Sunfire, skillId: 7, amount: 50, crit: true, ms: 1200);
        boss.AddDealt(E(PlayerA), TargetBucketStats.OtherKey, skillId: 9, amount: 25, crit: false, ms: 1400);
        boss.AddTaken(E(PlayerA), Sunfire, amount: 40, ms: 1500);

        var d = DerivedBuilder.Build(Entry(boss: boss), truncatedEvents: false);

        var dealt = d.PerActorBossDealt![K(PlayerA)];
        Assert.Equal(150, dealt[K(Sunfire)].Total);
        var sk = Assert.Single(dealt[K(Sunfire)].Skills);
        Assert.Equal((7, 150L, 2, 1), (sk.SkillId, sk.Total, sk.Hits, sk.Crits));
        Assert.Equal(25, dealt["other"].Total);            // OtherKey renders as the literal "other"
        Assert.Equal(40, d.PerActorBossTaken![K(PlayerA)][K(Sunfire)].Total);
        Assert.Equal(new long[] { 100, 50 }, d.PerActorBossSeries![K(PlayerA)][K(Sunfire)].Dealt.ToArray());
        Assert.Equal(new long[] { 0, 40 }, d.PerActorBossSeries![K(PlayerA)][K(Sunfire)].Taken.ToArray());
    }

    [Fact]
    public void Derived_keeps_elite_buckets_in_their_own_maps_never_merged_into_boss()
    {
        var elite = new TargetBucketStats(1000, 4096);
        elite.AddDealt(E(PlayerA), EliteId, skillId: 3, amount: 70, crit: false, ms: 0);
        elite.AddTaken(E(PlayerA), EliteId, amount: 12, ms: 0);

        var d = DerivedBuilder.Build(Entry(elite: elite), truncatedEvents: false);

        Assert.Null(d.PerActorBossDealt);                  // boss store empty → boss maps absent
        Assert.Null(d.PerActorBossTaken);
        Assert.Null(d.PerActorBossSeries);
        Assert.Equal(70, d.PerActorEliteDealt![K(PlayerA)][K(EliteId)].Total);
        Assert.Equal(12, d.PerActorEliteTaken![K(PlayerA)][K(EliteId)].Total);
        Assert.Equal(new long[] { 70 }, d.PerActorEliteSeries![K(PlayerA)][K(EliteId)].Dealt.ToArray());
    }

    // -------------------------------------------------------------------------
    // Normalization (task-2 review correction a): every per-cell SourceTimeline coalesces
    // INDEPENDENTLY, so a cell can carry a different BucketMs than the whole-fight actor
    // series. The derived block has ONE bucketMs — its max must include the bucket cells,
    // and every series (actor AND bucket) is rebucketed onto it, or the site's per-bucket
    // chart swap skews against the whole-fight chart.
    // -------------------------------------------------------------------------

    [Fact]
    public void Block_bucketMs_accounts_for_bucket_cells_and_rebuckets_the_actor_series()
    {
        var entry = Entry(series: new Dictionary<EntityId, SourceSeries>
        {
            [E(PlayerA)] = new SourceSeries
            {
                BucketMs = 1000,
                Dealt = new long[] { 1, 2, 3, 4 }, Healing = new long[] { 5, 0, 0, 0 }, Taken = new long[] { 0, 0, 6, 0 },
            },
        });
        // A cell that coalesced to 2000 ms while the whole-fight timeline stayed at 1000.
        entry.BossBuckets = Cells(E(PlayerA), (Sunfire, Cell(dealt: 30, dealtSeries: new long[] { 10, 20 }, seriesBucketMs: 2000)));

        var d = DerivedBuilder.Build(entry, truncatedEvents: false);

        Assert.Equal(2000, d.Series.BucketMs);                                        // block max includes the cell
        Assert.Equal(new long[] { 3, 7 }, d.Series.PerActor[K(PlayerA)].Dealt.ToArray());   // actor series merged pairwise
        Assert.Equal(new long[] { 10, 20 }, d.PerActorBossSeries![K(PlayerA)][K(Sunfire)].Dealt.ToArray()); // already at 2000
    }

    [Fact]
    public void Bucket_series_are_rebucketed_up_to_the_block_bucketMs()
    {
        var entry = Entry(series: new Dictionary<EntityId, SourceSeries>
        {
            // Whole-fight timeline coalesced to 2000; the cell is still at 1000.
            [E(PlayerA)] = new SourceSeries
            {
                BucketMs = 2000,
                Dealt = new long[] { 3, 7 }, Healing = Array.Empty<long>(), Taken = Array.Empty<long>(),
            },
        });
        entry.BossBuckets = Cells(E(PlayerA), (Sunfire, Cell(
            dealt: 10, taken: 4,
            dealtSeries: new long[] { 1, 2, 3, 4 }, takenSeries: new long[] { 0, 0, 4, 0 },
            seriesBucketMs: 1000)));

        var d = DerivedBuilder.Build(entry, truncatedEvents: false);

        Assert.Equal(2000, d.Series.BucketMs);
        var s = d.PerActorBossSeries![K(PlayerA)][K(Sunfire)];
        Assert.Equal(new long[] { 3, 7 }, s.Dealt.ToArray());
        Assert.Equal(new long[] { 0, 4 }, s.Taken.ToArray());
        // Rebucketing is loss-free: the series total still equals the cell total (§7.1/§7.2).
        Assert.Equal(10, s.Dealt.Sum());
        Assert.Equal(4, s.Taken.Sum());
    }

    // -------------------------------------------------------------------------
    // §7.1 SUMS EQUAL TOTALS — the pinned no-loss invariant, at the emission seam.
    // Σ (boss buckets + elite buckets, incl. Other) per player per channel MUST equal the
    // whole-fight ActorAgg total the run page already shows. NEVER WEAKEN THIS TEST.
    // -------------------------------------------------------------------------

    [Fact]
    public void Emitted_bucket_sums_equal_the_emitted_actor_totals_per_channel()
    {
        var boss = new TargetBucketStats(1000, 4096);
        var elite = new TargetBucketStats(1000, 4096);
        var stats = new Dictionary<EntityId, SourceStats>();

        // Two players, both channels, across two bosses + Other + one elite. Every hit is booked
        // BOTH into the whole-fight stats and into its bucket — exactly what Plugin.Capture.cs does.
        void Dealt(long player, bool isElite, int bucket, int skill, long amount)
        {
            (isElite ? elite : boss).AddDealt(E(player), bucket, skill, amount, crit: false, ms: 0);
            Stats(player).TotalDamage += amount;
        }
        void Taken(long player, bool isElite, int bucket, long amount)
        {
            (isElite ? elite : boss).AddTaken(E(player), bucket, amount, ms: 0);
            Stats(player).TotalTaken += amount;
        }
        SourceStats Stats(long player)
        {
            if (!stats.TryGetValue(E(player), out var s)) { s = new SourceStats(); stats[E(player)] = s; }
            return s;
        }

        Dealt(PlayerA, false, Sunfire, 7, 100);
        Dealt(PlayerA, false, Moonstrike, 7, 60);
        Dealt(PlayerA, false, TargetBucketStats.OtherKey, 9, 30);
        Dealt(PlayerA, true, EliteId, 7, 40);
        Dealt(PlayerB, false, Sunfire, 11, 210);
        Dealt(PlayerB, false, TargetBucketStats.OtherKey, 11, 5);
        Taken(PlayerA, false, Sunfire, 25);
        Taken(PlayerA, false, TargetBucketStats.OtherKey, 5);
        Taken(PlayerA, true, EliteId, 10);
        Taken(PlayerB, false, Moonstrike, 77);

        var d = DerivedBuilder.Build(Entry(stats: stats, boss: boss, elite: elite), truncatedEvents: false);

        foreach (var uid in new[] { K(PlayerA), K(PlayerB) })
        {
            long dealt = BucketSum(d.PerActorBossDealt, uid, b => b.Total) + BucketSum(d.PerActorEliteDealt, uid, b => b.Total);
            long taken = BucketSum(d.PerActorBossTaken, uid, b => b.Total) + BucketSum(d.PerActorEliteTaken, uid, b => b.Total);
            Assert.Equal(d.PerActor[uid].Damage, dealt);
            Assert.Equal(d.PerActor[uid].DamageTaken, taken);
        }

        // Sanity: the fixture really did exercise all three bucket kinds.
        Assert.Equal(3, d.PerActorBossDealt![K(PlayerA)].Count);   // Sunfire + Moonstrike + other
        Assert.True(d.PerActorEliteDealt!.ContainsKey(K(PlayerA)));
    }

    private static long BucketSum<T>(IReadOnlyDictionary<string, IReadOnlyDictionary<string, T>>? map, string uid, Func<T, long> pick)
        => map is not null && map.TryGetValue(uid, out var buckets) ? buckets.Values.Sum(pick) : 0L;

    // -------------------------------------------------------------------------
    // Wire shape: the six keys reach the JSON with the documented names/nesting.
    // -------------------------------------------------------------------------

    [Fact]
    public void CombatLogWriter_emits_the_six_bucket_keys_with_config_id_and_other_bucket_names()
    {
        var boss = new TargetBucketStats(1000, 4096);
        boss.AddDealt(E(PlayerA), Sunfire, skillId: 7, amount: 100, crit: true, ms: 0);
        boss.AddDealt(E(PlayerA), TargetBucketStats.OtherKey, skillId: 9, amount: 25, crit: false, ms: 0);
        boss.AddTaken(E(PlayerA), Sunfire, amount: 40, ms: 0);
        var elite = new TargetBucketStats(1000, 4096);
        elite.AddDealt(E(PlayerA), EliteId, skillId: 3, amount: 70, crit: false, ms: 0);
        elite.AddTaken(E(PlayerA), EliteId, amount: 12, ms: 0);

        var json = WriteLog(DerivedBuilder.Build(Entry(boss: boss, elite: elite), truncatedEvents: false));

        Assert.Contains($"\"perActorBossDealt\":{{\"{PlayerA}\":{{", json);
        Assert.Contains($"\"{Sunfire}\":{{\"total\":100,\"skills\":[{{\"skillId\":7,\"total\":100,\"hits\":1,\"crits\":1", json);
        Assert.Contains("\"other\":{\"total\":25", json);
        // "other" carried dealt-only, so it is present in the dealt/series maps and ABSENT from taken.
        Assert.Contains($"\"perActorBossTaken\":{{\"{PlayerA}\":{{\"{Sunfire}\":{{\"total\":40}}}}}}", json);
        Assert.Contains($"\"perActorBossSeries\":{{\"{PlayerA}\":{{\"{Sunfire}\":{{\"dealt\":[100],\"taken\":[40]}}", json);
        Assert.Contains("\"other\":{\"dealt\":[25],\"taken\":[]}", json);
        Assert.Contains($"\"perActorEliteDealt\":{{\"{PlayerA}\":{{\"{EliteId}\":{{\"total\":70", json);
        Assert.Contains($"\"perActorEliteTaken\":{{\"{PlayerA}\":{{\"{EliteId}\":{{\"total\":12}}}}}}", json);
        Assert.Contains($"\"perActorEliteSeries\":{{\"{PlayerA}\":{{\"{EliteId}\":{{\"dealt\":[70],\"taken\":[12]}}}}}}", json);
    }

    private static string WriteLog(Derived d)
    {
        var header = new LogHeader("cm-buckets", 70_000L, "3.7", "SEA", "1.18.0", "2.0.0", "unlisted",
            CombatLogAssembler.BuildEncounter(new Plugin.EncounterHistoryEntry()), new Uploader(PlayerA, "sig", "nonce"));
        return CombatLogWriter.Write(new CombatLog(1, header, new Dictionary<string, Actor>(), Array.Empty<CombatLogEvent>(), d));
    }
}
