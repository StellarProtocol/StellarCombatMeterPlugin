using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>
/// Pins the LIVE-meter class display (owner 2026-09-25: a transformed player must keep their original class
/// on screen). While a Battle Imagine is active attr 220 reads the transform (14 Lucy, 15 Natsu); the meter
/// used to show "Lucy", the transform crest and — because RoleClassifier maps any unknown class to DPS — a
/// DPS-red bar for a tank/healer. Never weaken these pins.
/// </summary>
public class PlayableClassDisplayTests
{
    [Fact]
    public void Roster_class_wins_when_playable()
        => Assert.Equal(12, PlayableClass.ResolveDisplayProfession(0, 12, 5, 14));

    [Fact]
    public void Self_transformed_uses_the_live_class_not_attr220()   // live = curProfessionId, attr 220 = Lucy
        => Assert.Equal(5, PlayableClass.ResolveDisplayProfession(0, 0, 5, 14));

    [Fact]
    public void Roster_reading_the_transform_falls_through_to_the_next_real_source()
        => Assert.Equal(9, PlayableClass.ResolveDisplayProfession(0, 15, 0, 9));

    [Fact]
    public void Every_source_transformed_keeps_the_last_class_shown()
        => Assert.Equal(12, PlayableClass.ResolveDisplayProfession(12, 15, 0, 15));

    [Fact]
    public void Transform_with_no_history_is_unknown_never_the_transform()
    {
        Assert.Equal(0, PlayableClass.ResolveDisplayProfession(0, 14, 0, 14));
        Assert.Equal(0, PlayableClass.ResolveDisplayProfession(14, 14));   // a transform is never sticky
    }

    [Fact]
    public void Archived_class_line_drops_transforms_keeping_play_order()
    {
        Assert.Equal(new[] { 12 }, PlayableClass.PlayableOnly(new[] { 12, 15 }));
        Assert.Equal(new[] { 5, 2 }, PlayableClass.PlayableOnly(new[] { 5, 14, 2 }));
        Assert.Empty(PlayableClass.PlayableOnly(new[] { 14 }));
    }
    [Fact]
    public void Run_banked_while_transformed_ships_the_real_class_stats_not_the_transform_pile()
    {
        // Sampler keys by attr 220: real-class samples land in pile 5, the Lucy seconds in pile 14.
        var t = new AttrRangeTracker();
        t.Observe(5,  new Dictionary<int, long> { [220] = 5,  [11010] = 2400 });
        t.Observe(14, new Dictionary<int, long> { [220] = 14, [11010] = 9100 });
        // At archive the player is still Lucy: attr 220 = 14, the framework's live class = 5.
        var shipped = PlayableClass.ResolveDisplayProfession(0, 0, 5, 14);
        Assert.Equal(5, shipped);
        var baseAttrs = t.Base(shipped).ToDictionary(a => (int)a[0], a => a[1]);
        Assert.Equal(5, baseAttrs[220]);
        Assert.Equal(2400, baseAttrs[11010]);
        Assert.True(t.Has(14));   // capture untouched: the transform pile is still recorded
    }
}
