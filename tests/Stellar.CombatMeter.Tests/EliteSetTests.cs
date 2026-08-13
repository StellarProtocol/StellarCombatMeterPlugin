using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.CombatMeter.AutoArchive;
using Xunit;

namespace Stellar.CombatMeter.Tests;

// ELITE CAPTURE channel (owner ruling 2026-08-13): elites (MonsterType==1) get HP + movement + identity
// capture SAME AS BOSSES, but EliteSet is CAPTURE ONLY — no Aggregate()/DrainIfAllGone() consumer exists
// (see EliteSet's own doc). These tests mirror StageBossSetTests' core cases (admit/dedup/cap/killed-
// sticky/clear/Contains) for the members EliteSet actually shares with StageBossSet's shape.
public class EliteSetTests
{
    private static EntityId E(long v) => new(v);
    private static EliteSet.EliteLiveness Alive => new() { Present = true,  Dead = false };
    private static EliteSet.EliteLiveness Gone  => new() { Present = false, Dead = false };
    private static EliteSet.EliteLiveness Dead  => new() { Present = false, Dead = true  };

    [Fact]
    public void Empty_set_admits_first_elite()
    {
        var s = new EliteSet();
        Assert.True(s.Admit(E(10), 200100));
        Assert.Equal(1, s.Count);
    }

    [Fact]
    public void Admit_dedupes_an_already_tracked_id()
    {
        var s = new EliteSet();
        s.Admit(E(10), 200100);
        Assert.False(s.Admit(E(10), 200100));   // already tracked — no-op
        Assert.Equal(1, s.Count);
    }

    [Fact]
    public void Admit_ignores_zero_id()
    {
        var s = new EliteSet();
        Assert.False(s.Admit(new EntityId(0), 200100));
        Assert.Equal(0, s.Count);
    }

    // Unlike StageBossSet, there is no "stage open/closed" gate — a second (unrelated) elite is admitted
    // even while the first one is already dead/gone, because elite capture is RUN-scoped, not
    // stage-scoped (no drain concept).
    [Fact]
    public void Second_elite_is_admitted_even_after_the_first_died()
    {
        var s = new EliteSet();
        s.Admit(E(10), 200100);
        s.SetLiveness(E(10), Dead);
        Assert.True(s.Admit(E(11), 200101));
        Assert.Equal(2, s.Count);
    }

    [Fact]
    public void Killed_flag_is_sticky_and_surfaces_in_members()
    {
        var s = new EliteSet();
        s.Admit(E(10), 200100);
        s.SetLiveness(E(10), Dead);
        s.SetLiveness(E(10), Gone);   // corpse evicted after death — Killed must stay sticky
        Assert.True(s.MembersSnapshot().Single(m => m.id == E(10)).killed);
    }

    [Fact]
    public void Clear_resets_everything()
    {
        var s = new EliteSet();
        s.Admit(E(10), 200100);
        s.Clear();
        Assert.Equal(0, s.Count);
    }

    // --- MaxMembers boundary — mirrors StageBossSet's own pinned cap test.

    [Fact]
    public void Admit_fills_to_MaxMembers_then_refuses_the_next()
    {
        var s = new EliteSet();
        for (var i = 0; i < EliteSet.MaxMembers; i++)
            Assert.True(s.Admit(E(i + 1), 200000 + i));
        Assert.Equal(EliteSet.MaxMembers, s.Count);

        Assert.False(s.Admit(E(999), 999000));
        Assert.Equal(EliteSet.MaxMembers, s.Count);
    }

    // --- MemberAt / Contains — alloc-free surface parity with StageBossSet.

    [Fact]
    public void MemberAt_indexed_access_matches_snapshot()
    {
        var s = new EliteSet();
        s.Admit(E(10), 200100); s.SetLiveness(E(10), Alive);
        s.Admit(E(11), 200101); s.SetLiveness(E(11), Dead);

        var snapshot = s.MembersSnapshot();
        Assert.Equal(snapshot.Count, s.Count);
        for (var i = 0; i < s.Count; i++)
            Assert.Equal(snapshot[i], s.MemberAt(i));
    }

    [Fact]
    public void Contains_true_for_an_admitted_member_alive_or_killed()
    {
        var s = new EliteSet();
        s.Admit(E(10), 200100); s.SetLiveness(E(10), Alive);
        s.Admit(E(11), 200101); s.SetLiveness(E(11), Dead);

        Assert.True(s.Contains(E(10)));
        Assert.True(s.Contains(E(11)));
    }

    [Fact]
    public void Contains_false_for_an_unknown_id_and_an_empty_set()
    {
        var s = new EliteSet();
        Assert.False(s.Contains(E(10)));   // empty set

        s.Admit(E(10), 200100);
        Assert.False(s.Contains(E(99)));   // non-empty, but this id was never admitted
    }
}
