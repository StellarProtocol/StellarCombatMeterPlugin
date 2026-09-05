using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public sealed class BuffEffectSamplerTests
{
    static readonly EntityId Self = new(0x0000_0001_0000_0280);
    static readonly EntityId Mate = new(0x0000_0002_0000_0280);
    static CombatEvent.BuffChanged Applied(long ms) => new(ms, Self, 9, 55333, BuffChangeKind.Applied, 1, 1, 5000, Mate, 0, 2327);
    static CombatEvent.BuffChanged Removed(long ms) => new(ms, Self, 9, 55333, BuffChangeKind.Removed, 1, 1, 0, Mate, 0, 2327);
    static Dictionary<int, long> Sheet(long crit, long dmg) => new() { [11710] = crit, [12670] = dmg, [11320] = 100_000 };

    [Fact]
    public void Clean_apply_records_positive_deltas_and_removal_measures_the_same_sign()
    {
        var s = new BuffEffectSampler();
        s.OnSelfBuff(Applied(1_000), Sheet(500, 0), 1_000);
        s.Tick(() => Sheet(1_500, 800), 1_700);           // +1000 crit bp, +800 generic dmg bp
        s.OnSelfBuff(Removed(5_000), Sheet(1_500, 800), 5_000);
        s.Tick(() => Sheet(500, 0), 5_700);               // drop of 1000/800 → flipped → +1000/+800
        var agg = Assert.Single(s.Drain());
        Assert.Equal((55333, 1, 0, 2327, 2), (agg.Base, agg.Stacks, agg.SrcKind, agg.SrcId, agg.N));
        Assert.Equal(1000L, agg.Deltas.Single(d => d.AttrId == 11710).MedianDelta);
        Assert.Equal(800L,  agg.Deltas.Single(d => d.AttrId == 12670).MedianDelta);
        Assert.DoesNotContain(agg.Deltas, d => d.AttrId == 11320);   // untracked attr ignored
    }

    [Fact]
    public void Dirty_window_is_discarded()
    {
        var s = new BuffEffectSampler();
        s.OnSelfBuff(Applied(1_000), Sheet(500, 0), 1_000);
        s.OnSelfBuff(new CombatEvent.BuffChanged(1_200, Self, 10, 777, BuffChangeKind.Applied, 1, 1, 1, Mate, 0, 1), Sheet(500, 0), 1_200); // another self change inside the window
        s.Tick(() => Sheet(1_500, 800), 1_700);
        s.Tick(() => Sheet(1_500, 800), 1_900);
        Assert.Empty(s.Drain());
    }

    [Fact]
    public void Self_applied_and_monster_applied_buffs_are_not_sampled()
    {
        var s = new BuffEffectSampler();
        s.OnSelfBuff(new CombatEvent.BuffChanged(1, Self, 1, 1, BuffChangeKind.Applied, 1, 1, 1, Self, 0, 1), Sheet(0, 0), 1);
        s.OnSelfBuff(new CombatEvent.BuffChanged(2, Self, 2, 2, BuffChangeKind.Applied, 1, 1, 1, new EntityId(0x9_0000_0040), 0, 1), Sheet(0, 0), 2);
        s.Tick(() => Sheet(999, 999), 5_000);
        Assert.Empty(s.Drain());
    }

    [Fact]
    public void Median_over_many_samples_and_cap_at_32()
    {
        var s = new BuffEffectSampler();
        for (int i = 0; i < 40; i++)
        {
            long t = i * 10_000L;
            s.OnSelfBuff(Applied(t), Sheet(500, 0), t);
            s.Tick(() => Sheet(500 + (i % 2 == 0 ? 1000 : 1200), 0), t + 700);
        }
        var agg = Assert.Single(s.Drain());
        Assert.Equal(32, agg.N);
        Assert.InRange(agg.Deltas.Single(d => d.AttrId == 11710).MedianDelta, 1000, 1200);
    }

    // --- Review round 1 (Task 9 fix-up) ---

    [Fact]
    public void Reset_drops_pending_and_samples()
    {
        var s = new BuffEffectSampler();
        s.OnSelfBuff(Applied(1_000), Sheet(500, 0), 1_000);
        s.Tick(() => Sheet(1_500, 800), 1_700);           // window closes, sample recorded
        s.OnSelfBuff(Applied(5_000), Sheet(500, 0), 5_000); // second window still pending
        s.Reset();
        Assert.Empty(s.Drain());
        s.Tick(() => Sheet(1_500, 800), 5_700);            // the reset pending must not resolve into anything
        Assert.Empty(s.Drain());
    }

    [Fact]
    public void Filtered_self_change_just_before_an_external_buff_dirties_it()
    {
        var s = new BuffEffectSampler();
        // Self-applied at 950 is filtered (never enters _pending) but must still dirty the window below —
        // the symmetric quiet-window rule counts EVERY self buff change, admitted or not.
        s.OnSelfBuff(new CombatEvent.BuffChanged(950, Self, 1, 1, BuffChangeKind.Applied, 1, 1, 1, Self, 0, 1), Sheet(500, 0), 950);
        s.OnSelfBuff(Applied(1_000), Sheet(500, 0), 1_000);   // only 50ms after the filtered change → dirty
        s.Tick(() => Sheet(1_500, 800), 1_700);
        Assert.Empty(s.Drain());
    }

    [Fact]
    public void Quiet_gap_before_an_external_buff_keeps_it_clean()
    {
        var s = new BuffEffectSampler();
        s.OnSelfBuff(new CombatEvent.BuffChanged(300, Self, 1, 1, BuffChangeKind.Applied, 1, 1, 1, Self, 0, 1), Sheet(500, 0), 300);
        s.OnSelfBuff(Applied(1_000), Sheet(500, 0), 1_000);   // 700ms after the filtered change → clean
        s.Tick(() => Sheet(1_500, 800), 1_700);
        Assert.Single(s.Drain());
    }

    [Fact]
    public void Sheet_is_not_read_for_filtered_or_dirty_buffs()
    {
        var s = new BuffEffectSampler();
        var reads = 0;
        System.Func<System.Collections.Generic.IReadOnlyDictionary<int, long>> readSheet = () => { reads++; return Sheet(500, 0); };

        s.OnSelfBuff(new CombatEvent.BuffChanged(1_200, Self, 10, 777, BuffChangeKind.Refreshed, 1, 1, 1, Mate, 0, 1), readSheet, 1_200);
        s.OnSelfBuff(new CombatEvent.BuffChanged(1_201, Self, 1, 1, BuffChangeKind.Applied, 1, 1, 1, Self, 0, 1), readSheet, 1_201);
        s.OnSelfBuff(new CombatEvent.BuffChanged(1_202, Self, 2, 2, BuffChangeKind.Applied, 1, 1, 1, new EntityId(0x9_0000_0040), 0, 1), readSheet, 1_202);
        // Dirty: another self change (the monster-applied one above) landed just before this external one.
        s.OnSelfBuff(Applied(1_210), readSheet, 1_210);
        Assert.Equal(0, reads);

        s.OnSelfBuff(Applied(5_000), readSheet, 5_000);       // clean and admitted → the ONE read
        Assert.Equal(1, reads);
    }

    [Fact]
    public void Empty_post_sheet_records_nothing()
    {
        var s = new BuffEffectSampler();
        s.OnSelfBuff(Applied(1_000), Sheet(500, 0), 1_000);
        s.Tick(() => new Dictionary<int, long>(), 1_700);      // untracked/reset entity — empty sheet
        Assert.Empty(s.Drain());
    }
}
