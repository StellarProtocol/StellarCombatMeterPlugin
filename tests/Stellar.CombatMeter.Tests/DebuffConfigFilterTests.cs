using System.Collections.Generic;
using Xunit;

namespace Stellar.CombatMeter.Tests;

// Pins Plugin.FilterSeenEffects — the pure snapshot/eligibility/kind/GROUP/search/sort seam behind the buffs &
// debuffs config panel's tabs + search box + virtual list (Plugin.DebuffConfig.cs). Same-named effects group
// into one row ("Cuisine ×136", like CooldownBar). Internal, so this test project must see [InternalsVisibleTo].
public class DebuffConfigFilterTests
{
    // All rows default to DEBUFFS (isBuff:false); pass isBuff:true for a buff row.
    private static Dictionary<int, (string name, bool hasIcon, bool isBuff, bool hidden)> Seen(
        params (int id, string name, bool hasIcon)[] rows)
    {
        var d = new Dictionary<int, (string name, bool hasIcon, bool isBuff, bool hidden)>();
        foreach (var r in rows) d[r.id] = (r.name, r.hasIcon, false, r.name.Length == 0);   // "" = game-hidden, unlabelled
        return d;
    }

    [Fact]
    public void No_icon_is_never_eligible_even_with_ShowHidden()
    {
        var seen = Seen((1, "Poison", false));
        Plugin.FilterSeenEffects(seen, "", showHidden: true, wantBuff: false, out var groups, out var count);
        Assert.Equal(0, count);
        Assert.Empty(groups);
    }

    [Fact]
    public void Unnamed_iconed_debuff_is_hidden_unless_ShowHidden()
    {
        var seen = Seen((1, "", true));
        Plugin.FilterSeenEffects(seen, "", showHidden: false, wantBuff: false, out _, out var hiddenCount);
        Assert.Equal(0, hiddenCount);

        Plugin.FilterSeenEffects(seen, "", showHidden: true, wantBuff: false, out var groups, out var shownCount);
        Assert.Equal(1, shownCount);
        Assert.Equal(1, groups[0].RepId);
        Assert.Equal("", groups[0].Name);
        Assert.Equal(1, groups[0].Count);
    }

    [Fact]
    public void Same_named_effects_group_into_one_row_with_a_count()
    {
        var seen = Seen((10, "Cuisine", true), (11, "Cuisine", true), (12, "Cuisine", true), (20, "Poison", true));
        Plugin.FilterSeenEffects(seen, "", showHidden: false, wantBuff: false, out var groups, out var count);
        Assert.Equal(2, count);   // Cuisine (×3) + Poison
        var cuisine = System.Array.Find(groups, g => g.Name == "Cuisine");
        Assert.Equal(3, cuisine.Count);
        Assert.Equal(new[] { 10, 11, 12 }, cuisine.Ids);
    }

    [Fact]
    public void Search_matches_name_case_insensitively()
    {
        var seen = Seen((1, "Poison", true), (2, "Burn", true));
        Plugin.FilterSeenEffects(seen, "poi", showHidden: false, wantBuff: false, out var groups, out var count);
        Assert.Equal(1, count);
        Assert.Equal("Poison", groups[0].Name);
    }

    [Fact]
    public void Search_matches_an_id_within_a_group()
    {
        var seen = Seen((12345, "Poison", true), (999, "Burn", true));
        Plugin.FilterSeenEffects(seen, "234", showHidden: false, wantBuff: false, out var groups, out var count);
        Assert.Equal(1, count);
        Assert.Equal(12345, groups[0].RepId);
    }

    [Fact]
    public void Empty_query_keeps_every_eligible_group()
    {
        var seen = Seen((1, "Poison", true), (2, "Burn", true), (3, "NoIcon", false));
        Plugin.FilterSeenEffects(seen, "", showHidden: false, wantBuff: false, out _, out var count);
        Assert.Equal(2, count);
    }

    [Fact]
    public void Groups_sort_by_name_then_rep_id()
    {
        var seen = Seen((3, "Burn", true), (1, "Burn", true), (2, "Ablaze", true));
        Plugin.FilterSeenEffects(seen, "", showHidden: false, wantBuff: false, out var groups, out var count);
        Assert.Equal(2, count);   // Ablaze + Burn(×2)
        Assert.Equal("Ablaze", groups[0].Name);
        Assert.Equal("Burn", groups[1].Name);
        Assert.Equal(2, groups[1].Count);
        Assert.Equal(new[] { 1, 3 }, groups[1].Ids);
    }

    [Fact]
    public void Empty_catalog_yields_empty_result()
    {
        Plugin.FilterSeenEffects(Seen(), "anything", showHidden: true, wantBuff: false, out var groups, out var count);
        Assert.Equal(0, count);
        Assert.Empty(groups);
    }

    [Fact]
    public void Tab_filters_by_kind()
    {
        var seen = new Dictionary<int, (string name, bool hasIcon, bool isBuff, bool hidden)>
        {
            [1] = ("Poison", true, false, false),        // debuff
            [2] = ("Attack Up", true, true, false),      // buff
            [3] = ("Bleed", true, false, false),         // debuff
        };
        Plugin.FilterSeenEffects(seen, "", showHidden: false, wantBuff: false, out var d, out var dCount);
        Assert.Equal(2, dCount);
        Assert.Equal(new[] { "Bleed", "Poison" }, new[] { d[0].Name, d[1].Name });

        Plugin.FilterSeenEffects(seen, "", showHidden: false, wantBuff: true, out var b, out var bCount);
        Assert.Equal(1, bCount);
        Assert.Equal("Attack Up", b[0].Name);
    }

    [Fact]
    public void Hidden_rows_keep_their_label_stay_per_id_sort_first_and_search_by_label()
    {
        // A game-hidden effect is listed under its EffectLabel ("Design name (id)"); two with the same label stay
        // separate rows, cluster before named rows, and the search box matches the shown label.
        var seen = new Dictionary<int, (string name, bool hasIcon, bool isBuff, bool hidden)>
        {
            [5]  = ("Burn", true, false, false),
            [7]  = ("Firmament Sub-Buff (7)", true, false, true),
            [8]  = ("Firmament Sub-Buff (7)", true, false, true),
            [9]  = ("Ablaze", true, false, false),
        };
        Plugin.FilterSeenEffects(seen, "", showHidden: false, wantBuff: false, out _, out var namedOnly);
        Assert.Equal(2, namedOnly);

        Plugin.FilterSeenEffects(seen, "", showHidden: true, wantBuff: false, out var all, out var n);
        Assert.Equal(4, n);
        Assert.True(all[0].Hidden && all[1].Hidden);
        Assert.Equal(new[] { 7, 8 }, new[] { all[0].RepId, all[1].RepId });
        Assert.Equal("Firmament Sub-Buff (7)", all[0].Name);
        Assert.Equal("Ablaze", all[2].Name);

        Plugin.FilterSeenEffects(seen, "firmament", showHidden: true, wantBuff: false, out var hit, out var hits);
        Assert.Equal(2, hits);
        Assert.All(hit, g => Assert.True(g.Hidden));
    }
}
