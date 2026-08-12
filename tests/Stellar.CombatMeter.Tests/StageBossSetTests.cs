using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.CombatMeter.AutoArchive;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public class StageBossSetTests
{
    private static EntityId E(long v) => new(v);
    private static StageBossSet.BossLiveness Alive => new() { Present = true,  Dead = false };
    private static StageBossSet.BossLiveness Gone  => new() { Present = false, Dead = false };
    private static StageBossSet.BossLiveness Dead  => new() { Present = false, Dead = true  };

    [Fact]
    public void Empty_set_admits_first_boss()
    {
        var s = new StageBossSet();
        Assert.True(s.Admit(E(10), 102800));
        Assert.Equal(1, s.Count);
    }

    [Fact]
    public void Coboss_joins_while_first_is_present()
    {
        var s = new StageBossSet();
        s.Admit(E(10), 102800);
        s.SetLiveness(E(10), Alive);
        Assert.True(s.Admit(E(11), 102801));      // first still present → same stage
        Assert.Equal(2, s.Count);
    }

    [Fact]
    public void Simultaneous_kill_reports_gone_and_dead_only_when_both_die()
    {
        var s = new StageBossSet();
        s.Admit(E(10), 102800); s.Admit(E(11), 102801);
        s.SetLiveness(E(10), Alive); s.SetLiveness(E(11), Alive);
        Assert.Equal((true, false, false), s.Aggregate());
        s.SetLiveness(E(10), Dead);                // one dead, one alive
        Assert.Equal((true, false, false), s.Aggregate());   // still present via #11 → NOT gone
        s.SetLiveness(E(11), Dead);                // both dead
        Assert.Equal((false, true, true), s.Aggregate());
    }

    [Fact]
    public void Staggered_kill_stays_open_across_a_long_gap()
    {
        var s = new StageBossSet();
        s.Admit(E(10), 102800); s.SetLiveness(E(10), Alive);
        s.Admit(E(11), 102801); s.SetLiveness(E(11), Alive);
        s.SetLiveness(E(10), Dead);                // boss A dies early
        s.SetLiveness(E(11), Alive);               // boss B fought for a long time
        Assert.Equal((true, false, false), s.Aggregate());   // one entry stays open
        s.SetLiveness(E(11), Dead);
        Assert.Equal((false, true, true), s.Aggregate());
    }

    [Fact]
    public void Blink_of_one_member_while_another_alive_does_not_go_gone()
    {
        var s = new StageBossSet();
        s.Admit(E(10), 102800); s.Admit(E(11), 102801);
        s.SetLiveness(E(10), Gone);                // transient eviction (not dead)
        s.SetLiveness(E(11), Alive);
        Assert.Equal((true, false, false), s.Aggregate());   // #11 alive → not gone
    }

    [Fact]
    public void Drain_when_all_gone_lets_next_boss_open_fresh_set()
    {
        var s = new StageBossSet();
        s.Admit(E(10), 102800); s.SetLiveness(E(10), Dead);
        s.DrainIfAllGone();
        Assert.Equal(0, s.Count);
        Assert.True(s.Admit(E(20), 102901));       // next stage's boss opens a fresh set
        Assert.Equal(1, s.Count);
    }

    [Fact]
    public void Drain_is_a_noop_while_a_member_is_present()
    {
        var s = new StageBossSet();
        s.Admit(E(10), 102800); s.SetLiveness(E(10), Alive);
        s.DrainIfAllGone();
        Assert.Equal(1, s.Count);
    }

    [Fact]
    public void Killed_flag_is_sticky_and_surfaces_in_members()
    {
        var s = new StageBossSet();
        s.Admit(E(10), 102800); s.SetLiveness(E(10), Dead);
        s.SetLiveness(E(10), Gone);                // corpse evicted after death
        Assert.True(s.MembersSnapshot().Single(m => m.id == E(10)).killed);
    }

    [Fact]
    public void Clear_resets_everything()
    {
        var s = new StageBossSet();
        s.Admit(E(10), 102800);
        s.Clear();
        Assert.Equal(0, s.Count);
    }

    // --- Amendment-1 alloc-free surface: MemberAt indexed access mirrors MembersSnapshot() ---

    [Fact]
    public void MemberAt_indexed_access_matches_snapshot()
    {
        var s = new StageBossSet();
        s.Admit(E(10), 102800); s.SetLiveness(E(10), Alive);
        s.Admit(E(11), 102801); s.SetLiveness(E(11), Dead);

        var snapshot = s.MembersSnapshot();
        Assert.Equal(snapshot.Count, s.Count);
        for (var i = 0; i < s.Count; i++)
            Assert.Equal(snapshot[i], s.MemberAt(i));
    }
}
