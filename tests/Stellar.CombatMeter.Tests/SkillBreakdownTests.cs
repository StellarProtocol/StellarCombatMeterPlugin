using Stellar.CombatMeter;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public sealed class SkillBreakdownTests
{
    [Fact]
    public void SkillMetricTotal_selects_damage_for_DPS_and_heal_for_HPS()
    {
        var dmg  = new SkillStats { Total = 500, HealTotal = 0,   Hits = 10 };
        var heal = new SkillStats { Total = 0,   HealTotal = 800, Hits = 16 };

        Assert.Equal(500, Plugin.SkillMetricTotal(dmg, Metric.Dps));
        Assert.Equal(0,   Plugin.SkillMetricTotal(heal, Metric.Dps));
        Assert.Equal(800, Plugin.SkillMetricTotal(heal, Metric.Hps));
        Assert.Equal(0,   Plugin.SkillMetricTotal(dmg, Metric.Hps));
    }

    [Fact]
    public void SkillMetricTotal_treats_Taken_metric_as_damage_total()
    {
        var sk = new SkillStats { Total = 320, HealTotal = 90 };
        Assert.Equal(320, Plugin.SkillMetricTotal(sk, Metric.Taken));
    }

    [Fact]
    public void BuildIncomingRows_reads_incoming_by_attacker_skill()
    {
        var src = new SourceStats();
        src.IncomingBySkill[303] = new IncomingSkillStats { Total = 1200, Hits = 4, TopHit = 400 };

        var rows = Plugin.BuildIncomingRows(src);

        Assert.Single(rows);
        Assert.Equal(303,  rows[0].SkillId);
        Assert.Equal(1200, rows[0].Total);
        Assert.Equal(4,    rows[0].Hits);
        Assert.Equal(400,  rows[0].TopHit);
    }

    [Fact]
    public void BuildIncomingRows_sorts_by_total_descending()
    {
        var src = new SourceStats();
        src.IncomingBySkill[1] = new IncomingSkillStats { Total = 100, Hits = 1, TopHit = 100 };
        src.IncomingBySkill[2] = new IncomingSkillStats { Total = 900, Hits = 3, TopHit = 500 };
        src.IncomingBySkill[3] = new IncomingSkillStats { Total = 400, Hits = 2, TopHit = 250 };

        var rows = Plugin.BuildIncomingRows(src);

        Assert.Equal(3, rows.Count);
        Assert.Equal(2, rows[0].SkillId);
        Assert.Equal(3, rows[1].SkillId);
        Assert.Equal(1, rows[2].SkillId);
    }

    [Fact]
    public void BuildIncomingRows_returns_empty_when_no_incoming_data()
    {
        var rows = Plugin.BuildIncomingRows(new SourceStats());
        Assert.Empty(rows);
    }

    [Fact]
    public void TakenMaxHit_returns_largest_incoming_TopHit()
    {
        var src = new SourceStats();
        src.IncomingBySkill[1] = new IncomingSkillStats { Total = 100, Hits = 1, TopHit = 100 };
        src.IncomingBySkill[2] = new IncomingSkillStats { Total = 900, Hits = 3, TopHit = 500 };
        src.IncomingBySkill[3] = new IncomingSkillStats { Total = 400, Hits = 2, TopHit = 250 };

        Assert.Equal(500, Plugin.TakenMaxHit(src));
    }

    [Fact]
    public void TakenHitCount_sums_incoming_hits_across_skills()
    {
        var src = new SourceStats();
        src.IncomingBySkill[1] = new IncomingSkillStats { Total = 100, Hits = 1, TopHit = 100 };
        src.IncomingBySkill[2] = new IncomingSkillStats { Total = 900, Hits = 3, TopHit = 500 };
        src.IncomingBySkill[3] = new IncomingSkillStats { Total = 400, Hits = 2, TopHit = 250 };

        Assert.Equal(6, Plugin.TakenHitCount(src));
    }

    [Fact]
    public void TakenMaxHit_and_TakenHitCount_are_zero_with_no_incoming_data()
    {
        var src = new SourceStats();
        Assert.Equal(0, Plugin.TakenMaxHit(src));
        Assert.Equal(0, Plugin.TakenHitCount(src));
    }
}
