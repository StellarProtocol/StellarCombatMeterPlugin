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
    public void Tracked_set_is_the_34_damage_relevant_IsSyncMe_attrs()
    {
        Assert.Equal(34, SheetRowBuilder.TrackedAttrs.Length);
        Assert.Equal(34, new HashSet<int>(SheetRowBuilder.TrackedAttrs).Count);
        Assert.Contains(11710, SheetRowBuilder.TrackedAttrs); Assert.Contains(13180, SheetRowBuilder.TrackedAttrs);
        Assert.Contains(11580, SheetRowBuilder.TrackedAttrs); Assert.DoesNotContain(11320, SheetRowBuilder.TrackedAttrs);
        // Phase 2 (spec § 6.1 as amended 2026-09-07): cooldown, versatility, haste/attack-speed, mastery, resource.
        foreach (var id in new[] { 11720, 11760, 11840, 11930, 11940, 11960, 11980 }) Assert.Contains(id, SheetRowBuilder.TrackedAttrs);
        Assert.DoesNotContain(100, SheetRowBuilder.TrackedAttrs);   // AttrSkillId is a CAST, not a self-sheet fact (CastRowBuilder)
        Assert.Contains(9011960, SheetRowBuilder.TrackedAttrs);     // phase 2b (2.9.0): plugin-derived local cooldown ratio (D10)
        Assert.Equal(9011960, CdRatioTracker.AttrId);
    }

    [Fact]
    public void Synthetic_row_is_one_unflagged_attr()
    {
        var row = SheetRowBuilder.Synthetic(123L, CdRatioTracker.AttrId, 2500L);
        Assert.Equal(123L, row.Ms);
        Assert.False(row.Keyframe);
        Assert.Single(row.Attrs);
        Assert.Equal(new long[] { 9011960, 2500 }, row.Attrs[0]);
    }

    [Fact]
    public void Keyframe_carries_the_synthetic_id_when_the_sheet_has_it()
    {
        var sheet = new Dictionary<int, long> { [11710] = 3350, [CdRatioTracker.AttrId] = 2500 };
        var kf = SheetRowBuilder.Keyframe(5L, sheet)!;
        Assert.Contains(kf.Attrs, a => a[0] == CdRatioTracker.AttrId && a[1] == 2500);
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

    // Task 5 review finding 1: the keyframe reader composes the live sheet with the derived cooldown ratio, and
    // 9011960 is itself a TRACKED attr — so an unconditional overlay would make Keyframe() non-null for a sheet
    // that holds nothing but the plugin's own number, latching EventSpool's once-per-segment keyframe flag while
    // the framework's attribute map is still empty. ComposeSelfSheet therefore overlays ONLY onto a sheet that
    // already carries a tracked GAME attr. The four legs below are the whole contract.

    [Fact]
    public void ComposeSelfSheet_without_a_ratio_returns_the_very_same_instance()
    {
        var attrs = new Dictionary<int, long> { [11710] = 3350 };
        Assert.Same(attrs, SheetRowBuilder.ComposeSelfSheet(attrs, null));
    }

    [Fact]
    public void ComposeSelfSheet_overlays_the_ratio_on_a_real_sheet_without_mutating_it()
    {
        var attrs = new Dictionary<int, long> { [11710] = 3350, [11320] = 5000 };
        var composed = SheetRowBuilder.ComposeSelfSheet(attrs, 2500);
        Assert.NotSame(attrs, composed);
        Assert.Equal(3, composed.Count);
        Assert.Equal(3350, composed[11710]);
        Assert.Equal(5000, composed[11320]);                    // every original entry survives, tracked or not
        Assert.Equal(2500, composed[CdRatioTracker.AttrId]);
        Assert.Equal(2, attrs.Count);                           // the framework's own dictionary is untouched
        Assert.DoesNotContain(CdRatioTracker.AttrId, attrs.Keys);
    }

    [Fact]
    public void ComposeSelfSheet_leaves_an_empty_sheet_untouched_so_the_keyframe_stays_deferred()
    {
        var attrs = new Dictionary<int, long>();                // e.g. straight after CombatEntityTracker.Reset()
        var composed = SheetRowBuilder.ComposeSelfSheet(attrs, 2500);
        Assert.Same(attrs, composed);
        Assert.Null(SheetRowBuilder.Keyframe(5L, composed));    // → EventSpool leaves the request PENDING
    }

    [Fact]
    public void ComposeSelfSheet_leaves_an_untracked_only_sheet_untouched()
    {
        var attrs = new Dictionary<int, long> { [11320] = 5000 };
        var composed = SheetRowBuilder.ComposeSelfSheet(attrs, 2500);
        Assert.Same(attrs, composed);
        Assert.Null(SheetRowBuilder.Keyframe(5L, composed));
        Assert.False(SheetRowBuilder.HasTrackedGameAttr(attrs));
        Assert.True(SheetRowBuilder.HasTrackedGameAttr(new Dictionary<int, long> { [11710] = 1 }));
        // The derived id alone is NOT a game attr — that is the whole point of the guard.
        Assert.False(SheetRowBuilder.HasTrackedGameAttr(new Dictionary<int, long> { [CdRatioTracker.AttrId] = 2500 }));
    }
}
