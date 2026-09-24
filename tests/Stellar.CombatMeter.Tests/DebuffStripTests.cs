using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Domain.GameData;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public class DebuffStripTests
{
    // buff table: id -> (isDebuff, hasIcon)
    private static Func<int, BuffInfo?> Table(params (int id, bool debuff, bool icon)[] rows)
    {
        var map = new Dictionary<int, BuffInfo>();
        foreach (var r in rows)
            map[r.id] = new BuffInfo(r.id, $"B{r.id}", "", r.icon ? "path/x" : "", default, r.debuff, 0);
        return id => map.TryGetValue(id, out var b) ? b : (BuffInfo?)null;
    }
    private static ActiveBuff Buff(int baseId, int stacks, long create, int dur, int uuid)
        => new(uuid, baseId, 1, EntityId.None, stacks, 0, create, dur);

    [Fact]
    public void Keeps_only_debuffs_with_an_icon()
    {
        var buffs = new[] { Buff(10,1,0,1000,1), Buff(20,1,0,1000,2), Buff(30,1,0,1000,3) };
        var t = Table((10,true,true),(20,false,true) /*buff*/,(30,true,false) /*no icon*/);
        var r = DebuffStrip.Build(buffs, t, Array.Empty<int>(), nowMs:0);
        Assert.Equal(1, r.Count);
        Assert.Equal(10, r.E0.BaseId);
    }

    [Fact]
    public void Denylist_excludes_a_debuff()
    {
        var buffs = new[] { Buff(10,1,0,1000,1), Buff(11,1,0,1000,2) };
        var t = Table((10,true,true),(11,true,true));
        var r = DebuffStrip.Build(buffs, t, new HashSet<int>{11}, nowMs:0);
        Assert.Equal(1, r.Count);
        Assert.Equal(10, r.E0.BaseId);
    }

    [Fact]
    public void Orders_stable_fifo_by_create_time()
    {
        var buffs = new[] { Buff(10,1,300,1000,1), Buff(11,1,100,1000,2), Buff(12,1,200,1000,3) };
        var t = Table((10,true,true),(11,true,true),(12,true,true));
        var r = DebuffStrip.Build(buffs, t, Array.Empty<int>(), nowMs:0);
        Assert.Equal(11, r.E0.BaseId); // oldest first
        Assert.Equal(12, r.E1.BaseId);
        Assert.Equal(10, r.E2.BaseId);
    }

    [Fact]
    public void Caps_at_four_and_reports_overflow()
    {
        var buffs = new List<ActiveBuff>();
        for (int i = 0; i < 6; i++) buffs.Add(Buff(10+i, 1, i, 1000, i));
        var rows = new List<(int,bool,bool)>();
        for (int i = 0; i < 6; i++) rows.Add((10+i, true, true));
        var r = DebuffStrip.Build(buffs, Table(rows.ToArray()), Array.Empty<int>(), nowMs:0);
        Assert.Equal(4, r.Count);
        Assert.Equal(2, r.Overflow);
        Assert.Equal(13, r.E3.BaseId); // 4th kept is the 4th-oldest
    }

    [Fact]
    public void Remain_fraction_is_time_left_and_permanent_is_full()
    {
        var buffs = new[] { Buff(10,1, create:0, dur:1000, uuid:1), Buff(11,1, create:0, dur:0, uuid:2) };
        var t = Table((10,true,true),(11,true,true));
        var r = DebuffStrip.Build(buffs, t, Array.Empty<int>(), nowMs:250);
        Assert.Equal(0.75f, r.E0.RemainFraction, 3); // 750/1000 left
        Assert.Equal(1f,    r.E1.RemainFraction, 3); // permanent -> full
    }

    [Fact]
    public void Stacks_carry_through()
    {
        var buffs = new[] { Buff(10, stacks:5, create:0, dur:1000, uuid:1) };
        var r = DebuffStrip.Build(buffs, Table((10,true,true)), Array.Empty<int>(), nowMs:0);
        Assert.Equal(5, r.E0.Stacks);
    }
}
