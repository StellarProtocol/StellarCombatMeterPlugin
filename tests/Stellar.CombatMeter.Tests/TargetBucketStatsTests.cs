using System.Linq;
using Stellar.Abstractions.Domain;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public class TargetBucketStatsTests
{
    private static EntityId E(long v) => new(v);

    [Fact]
    public void Dealt_accumulates_per_player_per_bucket_and_per_skill()
    {
        var s = new TargetBucketStats(bucketMs: 500, maxBuckets: 4096);
        s.AddDealt(E(1), 102800, skillId: 7, amount: 100, crit: false, ms: 0);
        s.AddDealt(E(1), 102800, skillId: 7, amount: 50,  crit: true,  ms: 600);
        s.AddDealt(E(1), TargetBucketStats.OtherKey, skillId: 9, amount: 25, crit: false, ms: 700);
        var snap = s.Snapshot();
        var boss = snap[E(1)][102800];
        Assert.Equal(150, boss.DealtTotal);
        var sk = Assert.Single(boss.Skills);
        Assert.Equal((7, 150L, 2, 1), (sk.SkillId, sk.Total, sk.Hits, sk.Crits));
        Assert.Equal(25, snap[E(1)][TargetBucketStats.OtherKey].DealtTotal);
    }

    [Fact]
    public void Sum_of_buckets_equals_whole_total_per_channel()
    {
        var s = new TargetBucketStats(500, 4096);
        s.AddDealt(E(1), 102800, 7, 100, false, 0);
        s.AddDealt(E(1), 102801, 7, 70,  false, 0);
        s.AddDealt(E(1), TargetBucketStats.OtherKey, 9, 30, false, 0);
        s.AddTaken(E(1), 102800, 40, 0);
        s.AddTaken(E(1), TargetBucketStats.OtherKey, 5, 0);
        var b = s.Snapshot()[E(1)];
        Assert.Equal(200, b.Values.Sum(x => x.DealtTotal));   // == the fight total the page shows
        Assert.Equal(45,  b.Values.Sum(x => x.TakenTotal));
    }

    [Fact]
    public void Series_bucket_at_the_same_cadence_and_anchor_as_the_whole_fight_series()
    {
        var s = new TargetBucketStats(500, 4096);
        s.AddDealt(E(1), 102800, 7, 100, false, ms: 0);
        s.AddDealt(E(1), 102800, 7, 60,  false, ms: 501);
        var b = s.Snapshot()[E(1)][102800];
        Assert.Equal(500, b.SeriesBucketMs);
        Assert.Equal(new long[] { 100, 60 }, b.DealtSeries);
    }

    [Fact]
    public void Clear_resets_everything()
    {
        var s = new TargetBucketStats(500, 4096);
        s.AddDealt(E(1), 102800, 7, 100, false, 0);
        s.Clear();
        Assert.Empty(s.Snapshot());
    }
}
