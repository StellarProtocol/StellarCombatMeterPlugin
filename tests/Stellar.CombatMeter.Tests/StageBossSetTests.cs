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

    // --- MaxMembers boundary (owner ruling 2026-08-13): the cap is a runaway brake against a bad
    // boss-detection flag admitting a whole mob pack, NOT a fight-size assumption — a live dungeon
    // (Foggy Sea Shadows) spawned 5-10 simultaneous bosses. Raised 8 -> 32 to match the upload
    // schema's bosses[] maxItems bound; this pins the boundary itself so it can never silently
    // regress back down. ---

    [Fact]
    public void Admit_fills_to_MaxMembers_then_refuses_the_next_and_Aggregate_stays_correct()
    {
        var s = new StageBossSet();
        for (var i = 0; i < StageBossSet.MaxMembers; i++)
            Assert.True(s.Admit(E(i + 1), 100000 + i));
        Assert.Equal(StageBossSet.MaxMembers, s.Count);

        // 33rd member refused — the set is full.
        Assert.False(s.Admit(E(999), 999000));
        Assert.Equal(StageBossSet.MaxMembers, s.Count);

        // Aggregate still reflects all 32 correctly while every member is alive.
        for (var i = 0; i < StageBossSet.MaxMembers; i++)
            s.SetLiveness(E(i + 1), Alive);
        Assert.Equal((true, false, false), s.Aggregate());

        // Killing all 32 (not the refused 33rd) reports gone+dead for the full set.
        for (var i = 0; i < StageBossSet.MaxMembers; i++)
            s.SetLiveness(E(i + 1), Dead);
        Assert.Equal((false, true, true), s.Aggregate());
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

    // --- Final review, Important 3: Contains() lets CheckBossCandidate skip the interop call + cache
    // lookup entirely for an already-admitted id. ---

    [Fact]
    public void Contains_true_for_an_admitted_member_alive_or_killed()
    {
        var s = new StageBossSet();
        s.Admit(E(10), 102800); s.SetLiveness(E(10), Alive);
        s.Admit(E(11), 102801); s.SetLiveness(E(11), Dead);

        Assert.True(s.Contains(E(10)));
        Assert.True(s.Contains(E(11)));
    }

    [Fact]
    public void Contains_false_for_an_unknown_id_and_an_empty_set()
    {
        var s = new StageBossSet();
        Assert.False(s.Contains(E(10)));   // empty set

        s.Admit(E(10), 102800);
        Assert.False(s.Contains(E(99)));   // non-empty, but this id was never admitted
    }

    [Fact]
    public void Contains_false_again_once_the_set_drains()
    {
        var s = new StageBossSet();
        s.Admit(E(10), 102800); s.SetLiveness(E(10), Dead);
        s.DrainIfAllGone();

        Assert.False(s.Contains(E(10)));   // drained — a fresh admission must be allowed again
    }

    // --- Spec B (2026-08-14-per-boss-statistics-design §3.1): TryGetConfigId is the per-event bucket-key
    // lookup. A non-member yields configId 0 == TargetBucketStats.OtherKey, so an unrouted target lands
    // in Other rather than being dropped (no-loss invariant §7.2). ---

    [Fact]
    public void TryGetConfigId_returns_admitted_members_config_and_false_after_drain()
    {
        var s = new StageBossSet();
        s.Admit(E(10), 102800); s.SetLiveness(E(10), Alive);
        s.Admit(E(11), 102801);                              // co-boss joins while #10 is present

        Assert.True(s.TryGetConfigId(E(10), out var a));
        Assert.Equal(102800, a);
        Assert.True(s.TryGetConfigId(E(11), out var b));
        Assert.Equal(102801, b);

        Assert.False(s.TryGetConfigId(E(99), out var unknown));
        Assert.Equal(0, unknown);                            // never admitted → Other

        // A KILLED member still resolves until the stage drains, so the post-kill DoT tail keeps
        // bucketing to that boss instead of leaking into Other.
        s.SetLiveness(E(10), Dead); s.SetLiveness(E(11), Dead);
        Assert.True(s.TryGetConfigId(E(10), out var dead));
        Assert.Equal(102800, dead);

        s.DrainIfAllGone();
        Assert.False(s.TryGetConfigId(E(10), out var drained));
        Assert.Equal(0, drained);
    }
}
