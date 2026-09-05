using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>The sheet track's rows (spec § 6.1): a delta row carries ONLY the tracked attrs present in that
/// packet (absolute values); a keyframe carries every tracked attr the live sheet has; untracked attrs never
/// reach the wire; a packet with no tracked attr yields no row.</summary>
public sealed class SheetRowBuilderTests
{
    static readonly EntityId Self = new(0x0000_0001_0000_0280);

    [Fact]
    public void Tracked_set_is_the_26_damage_relevant_IsSyncMe_attrs()
    {
        Assert.Equal(26, SheetRowBuilder.TrackedAttrs.Length);
        Assert.Contains(11710, SheetRowBuilder.TrackedAttrs); Assert.Contains(13180, SheetRowBuilder.TrackedAttrs);
        Assert.Contains(11580, SheetRowBuilder.TrackedAttrs); Assert.DoesNotContain(11320, SheetRowBuilder.TrackedAttrs);
    }

    [Fact]
    public void Project_keeps_tracked_attrs_only_in_packet_order()
    {
        var ac = new CombatEvent.EntityAttributesChanged(77L, Self, new List<AttrValue> { new(11320, 5000), new(12670, 1200), new(11710, 3350) });
        var row = SheetRowBuilder.Project(ac)!;
        Assert.Equal(77L, row.Ms); Assert.False(row.Keyframe);
        Assert.Equal(2, row.Attrs.Count);
        Assert.Equal(new long[] { 12670, 1200 }, row.Attrs[0]); Assert.Equal(new long[] { 11710, 3350 }, row.Attrs[1]);
    }

    [Fact]
    public void Project_returns_null_when_no_tracked_attr_is_present()
    {
        var ac = new CombatEvent.EntityAttributesChanged(77L, Self, new List<AttrValue> { new(11320, 5000) });
        Assert.Null(SheetRowBuilder.Project(ac));
    }

    [Fact]
    public void Keyframe_carries_every_tracked_attr_the_sheet_has_and_is_flagged()
    {
        var sheet = new Dictionary<int, long> { [11710] = 3350, [11320] = 5000, [12510] = 15000 };
        var kf = SheetRowBuilder.Keyframe(5L, sheet)!;
        Assert.True(kf.Keyframe); Assert.Equal(5L, kf.Ms);
        Assert.Equal(2, kf.Attrs.Count);
        Assert.Contains(kf.Attrs, a => a[0] == 11710 && a[1] == 3350);
        Assert.Contains(kf.Attrs, a => a[0] == 12510 && a[1] == 15000);
    }

    [Fact]
    public void Keyframe_is_null_for_a_sheet_without_tracked_attrs()
    {
        Assert.Null(SheetRowBuilder.Keyframe(5L, new Dictionary<int, long> { [11320] = 1 }));
        Assert.Null(SheetRowBuilder.Keyframe(5L, new Dictionary<int, long>()));
    }
}
