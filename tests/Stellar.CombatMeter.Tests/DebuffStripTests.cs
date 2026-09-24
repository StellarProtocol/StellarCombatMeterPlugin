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
        var r = DebuffStrip.Build(buffs, t, new DebuffSelection(), nowMs:0);
        Assert.Equal(1, r.Count);
        Assert.Equal(10, r.E0.BaseId);
    }

    [Fact]
    public void ShowOnlySelected_excludes_an_unselected_debuff()
    {
        var buffs = new[] { Buff(10,1,0,1000,1), Buff(11,1,0,1000,2) };
        var t = Table((10,true,true),(11,true,true));
        var sel = new DebuffSelection { Mode = DebuffTrackMode.ShowOnlySelected };
        sel.SetSelected(10, true);
        var r = DebuffStrip.Build(buffs, t, sel, nowMs:0);
        Assert.Equal(1, r.Count);
        Assert.Equal(10, r.E0.BaseId);
    }

    [Fact]
    public void Orders_stable_fifo_by_create_time()
    {
        var buffs = new[] { Buff(10,1,300,1000,1), Buff(11,1,100,1000,2), Buff(12,1,200,1000,3) };
        var t = Table((10,true,true),(11,true,true),(12,true,true));
        var r = DebuffStrip.Build(buffs, t, new DebuffSelection(), nowMs:0);
        Assert.Equal(11, r.E0.BaseId); // oldest first
        Assert.Equal(12, r.E1.BaseId);
        Assert.Equal(10, r.E2.BaseId);
    }

    [Fact]
    public void Caps_at_four_and_reports_overflow()
    {
        // 6 kept debuffs: the renderer sacrifices the 4th cell for "+N" whenever there's overflow, so
        // Build reserves it too — count is 3 (not 4), overflow is 3 (not 2), and E3 is left default.
        var buffs = new List<ActiveBuff>();
        for (int i = 0; i < 6; i++) buffs.Add(Buff(10+i, 1, i, 1000, i));
        var rows = new List<(int,bool,bool)>();
        for (int i = 0; i < 6; i++) rows.Add((10+i, true, true));
        var r = DebuffStrip.Build(buffs, Table(rows.ToArray()), new DebuffSelection(), nowMs:0);
        Assert.Equal(3, r.Count);
        Assert.Equal(3, r.Overflow);
        Assert.Equal(12, r.E2.BaseId); // 3rd kept is the 3rd-oldest (E0=10, E1=11, E2=12)
    }

    [Fact]
    public void Exactly_four_shows_all_no_overflow()
    {
        // Exactly MaxCells kept debuffs: no overflow, so all four cells render (none sacrificed for "+N").
        var buffs = new List<ActiveBuff>();
        for (int i = 0; i < 4; i++) buffs.Add(Buff(10+i, 1, i, 1000, i));
        var rows = new List<(int,bool,bool)>();
        for (int i = 0; i < 4; i++) rows.Add((10+i, true, true));
        var r = DebuffStrip.Build(buffs, Table(rows.ToArray()), new DebuffSelection(), nowMs:0);
        Assert.Equal(4, r.Count);
        Assert.Equal(0, r.Overflow);
        Assert.Equal(13, r.E3.BaseId);
    }

    [Fact]
    public void Remain_fraction_is_time_left_and_permanent_is_full()
    {
        var buffs = new[] { Buff(10,1, create:0, dur:1000, uuid:1), Buff(11,1, create:0, dur:0, uuid:2) };
        var t = Table((10,true,true),(11,true,true));
        var r = DebuffStrip.Build(buffs, t, new DebuffSelection(), nowMs:250);
        Assert.Equal(0.75f, r.E0.RemainFraction, 3); // 750/1000 left
        Assert.Equal(1f,    r.E1.RemainFraction, 3); // permanent -> full
    }

    [Fact]
    public void Stacks_carry_through()
    {
        var buffs = new[] { Buff(10, stacks:5, create:0, dur:1000, uuid:1) };
        var r = DebuffStrip.Build(buffs, Table((10,true,true)), new DebuffSelection(), nowMs:0);
        Assert.Equal(5, r.E0.Stacks);
    }

    [Fact]
    public void Same_create_time_preserves_input_order()
    {
        // Regression: List.Sort is unstable (introsort); an AoE applying several debuffs in the
        // same tick must not have its icon order flicker between Build() calls. Build uses an
        // in-place insertion sort (stable by construction) so ties preserve input order.
        var buffs = new[] { Buff(10,1,100,1000,1), Buff(11,1,100,1000,2) };
        var t = Table((10,true,true),(11,true,true));
        var r = DebuffStrip.Build(buffs, t, new DebuffSelection(), nowMs:0);
        Assert.Equal(10, r.E0.BaseId);
        Assert.Equal(11, r.E1.BaseId);
    }

    [Fact]
    public void No_name_debuff_excluded_unless_show_hidden()
    {
        var buffs = new[] { Buff(10, 1, 0, 1000, 1), Buff(11, 1, 0, 1000, 2) };
        System.Func<int, BuffInfo?> t = id => id == 10
            ? new BuffInfo(10, "", "", "path/x", default, true, 0)     // no name — hidden by default
            : new BuffInfo(11, "B11", "", "path/x", default, true, 0); // named — shown
        var r = DebuffStrip.Build(buffs, t, new DebuffSelection(), nowMs: 0);
        Assert.Equal(1, r.Count);
        Assert.Equal(11, r.E0.BaseId);

        var shown = DebuffStrip.Build(buffs, t, new DebuffSelection { ShowHidden = true }, nowMs: 0);
        Assert.Equal(2, shown.Count);
    }
}
