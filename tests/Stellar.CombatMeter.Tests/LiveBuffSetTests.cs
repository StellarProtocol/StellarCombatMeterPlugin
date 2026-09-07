using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>The live buff set behind the segment-start KEYFRAME (rDPS phase 2, spec § 6.1.3): every applied/refreshed
/// row upserts, removed deletes, and a keyframe re-states each live buff as a synthetic `applied` with its REMAINING
/// duration. A buff whose own duration has run out is dropped (a missed remove), a permanent one (durMs ≤ 0) stays.</summary>
public sealed class LiveBuffSetTests
{
    static readonly EntityId Mate = new(0x0000_0002_0000_0280), Self = new(0x0000_0001_0000_0280);
    static LiveBuff Row(long ms, string kind, int uuid, int durMs, string tgt = "1") =>
        new(new BuffEvent(ms, tgt, uuid, 55333, kind, 1, 1, durMs, "2", 0, 2327), Mate, tgt == "1" ? Self : Mate);

    [Fact]
    public void Keyframe_restates_live_buffs_as_applied_with_remaining_duration_and_kf()
    {
        var set = new LiveBuffSet();
        set.Apply(Row(1000, "applied", 1, 10_000));
        set.Apply(Row(2000, "applied", 2, 0));            // permanent
        set.Apply(Row(3000, "refreshed", 1, 8_000));      // refreshed: the newer row wins, its ms is the new apply instant
        var kf = set.Keyframe(5000);
        Assert.Equal(2, kf.Count);
        var b1 = kf.Single(l => l.Row.Uuid == 1).Row;
        Assert.Equal(5000, b1.Ms); Assert.Equal("applied", b1.Kind); Assert.Equal(6_000, b1.DurMs); Assert.True(b1.Kf);
        Assert.Equal(0, kf.Single(l => l.Row.Uuid == 2).Row.DurMs);
        Assert.All(kf, l => Assert.Equal(Mate, l.Firer));
    }

    [Fact]
    public void Removed_buffs_and_expired_ones_are_not_in_the_keyframe()
    {
        var set = new LiveBuffSet();
        set.Apply(Row(1000, "applied", 1, 2_000));
        set.Apply(Row(1000, "applied", 2, 10_000));
        set.Apply(Row(1500, "removed", 2, 0));
        Assert.Empty(set.Keyframe(4000));                  // uuid 1 expired at 3000, uuid 2 removed
        Assert.Equal(0, set.Count);                        // the expired entry was dropped, not kept
    }

    [Fact]
    public void Keys_are_per_target_and_the_set_is_bounded()
    {
        var set = new LiveBuffSet();
        set.Apply(Row(1, "applied", 7, 0, "1"));
        set.Apply(Row(1, "applied", 7, 0, "2"));           // same uuid on another target = another buff
        Assert.Equal(2, set.Count);
        for (var i = 0; i < LiveBuffSet.MaxEntries + 10; i++) set.Apply(Row(1, "applied", 100 + i, 0, "3"));
        Assert.Equal(LiveBuffSet.MaxEntries, set.Count);
        set.Clear();
        Assert.Equal(0, set.Count);
    }

    // Boundary: Ms + DurMs == kfMs exactly (remaining == 0, not negative) is still "run out" — dropped from
    // the keyframe AND removed from the set, same as a buff that expired strictly before kfMs.
    [Fact]
    public void Keyframe_drops_a_buff_whose_remaining_duration_is_exactly_zero()
    {
        var set = new LiveBuffSet();
        set.Apply(Row(1000, "applied", 1, 2_000));         // 1000 + 2000 == kfMs below
        Assert.Empty(set.Keyframe(3000));
        Assert.Equal(0, set.Count);
    }

    // A FULL set (MaxEntries reached) must still let an EXISTING key be updated (a refreshed/applied row
    // replaces it — the new Ms/DurMs show in the next Keyframe) and removed (a removed row for a present
    // key drops it, Count decreases) — only NEW keys are refused while full (see Keys_are_per_target_...).
    [Fact]
    public void A_full_set_still_lets_an_existing_key_be_updated_and_removed()
    {
        var set = new LiveBuffSet();
        set.Apply(Row(1, "applied", 7, 0, "3"));                              // the key we'll update/remove
        for (var i = 0; i < LiveBuffSet.MaxEntries - 1; i++) set.Apply(Row(1, "applied", 100 + i, 0, "3"));
        Assert.Equal(LiveBuffSet.MaxEntries, set.Count);                      // full

        set.Apply(Row(2000, "refreshed", 7, 5_000, "3"));                     // update: refreshed for a PRESENT key
        Assert.Equal(LiveBuffSet.MaxEntries, set.Count);                      // no growth — same key, not a new one
        var updated = set.Keyframe(3000).Single(l => l.Row.Uuid == 7).Row;
        Assert.Equal(3000, updated.Ms); Assert.Equal(4_000, updated.DurMs);

        set.Apply(Row(1, "removed", 7, 0, "3"));                              // remove: present key drops
        Assert.Equal(LiveBuffSet.MaxEntries - 1, set.Count);
    }
}
