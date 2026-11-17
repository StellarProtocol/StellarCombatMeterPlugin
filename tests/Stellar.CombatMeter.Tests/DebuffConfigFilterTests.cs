using System.Collections.Generic;
using Xunit;

namespace Stellar.CombatMeter.Tests;

// Pins Plugin.FilterSeenEffects — the pure snapshot/eligibility/kind/GROUP/search/sort seam behind the buffs &
// debuffs config panel's tabs + search box + virtual list (Plugin.DebuffConfig.cs). Same-named effects group
// into one row ("Cuisine ×136", like CooldownBar). Internal, so this test project must see [InternalsVisibleTo].
public class DebuffConfigFilterTests
{
    // All rows default to DEBUFFS (isBuff:false); pass isBuff:true for a buff row.
    private static Dictionary<int, (string name, bool isBuff, bool hidden)> Seen(
        params (int id, string name)[] rows)
    {
        var d = new Dictionary<int, (string name, bool isBuff, bool hidden)>();
        foreach (var r in rows) d[r.id] = (r.name, false, r.name.Length == 0);   // "" = game-hidden, unlabelled
        return d;
    }

    [Fact]
    public void Icon_less_effect_is_eligible()
    {
        // Every effect draws (no art → a category placeholder tile, owner 2026-11-17), so there is no icon gate.
        var seen = Seen((1, "Poison"));
        Plugin.FilterSeenEffects(seen, "", showHidden: false, wantBuff: false, out var groups, out var count);
        Assert.Equal(1, count);
        Assert.Equal(1, groups[0].RepId);
    }

    [Fact]
    public void Unnamed_iconed_debuff_is_hidden_unless_ShowHidden()
    {
        var seen = Seen((1, ""));
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
        var seen = Seen((10, "Cuisine"), (11, "Cuisine"), (12, "Cuisine"), (20, "Poison"));
        Plugin.FilterSeenEffects(seen, "", showHidden: false, wantBuff: false, out var groups, out var count);
        Assert.Equal(2, count);   // Cuisine (×3) + Poison
        var cuisine = System.Array.Find(groups, g => g.Name == "Cuisine");
        Assert.Equal(3, cuisine.Count);
        Assert.Equal(new[] { 10, 11, 12 }, cuisine.Ids);
    }

    [Fact]
    public void Search_matches_name_case_insensitively()
    {
        var seen = Seen((1, "Poison"), (2, "Burn"));
        Plugin.FilterSeenEffects(seen, "poi", showHidden: false, wantBuff: false, out var groups, out var count);
        Assert.Equal(1, count);
        Assert.Equal("Poison", groups[0].Name);
    }

    [Fact]
    public void Search_matches_an_id_within_a_group()
    {
        var seen = Seen((12345, "Poison"), (999, "Burn"));
        Plugin.FilterSeenEffects(seen, "234", showHidden: false, wantBuff: false, out var groups, out var count);
        Assert.Equal(1, count);
        Assert.Equal(12345, groups[0].RepId);
    }

    [Fact]
    public void Empty_query_keeps_every_eligible_group()
    {
        var seen = Seen((1, "Poison"), (2, "Burn"), (3, "NoIcon"));
        Plugin.FilterSeenEffects(seen, "", showHidden: false, wantBuff: false, out _, out var count);
        Assert.Equal(3, count);
    }

    [Fact]
    public void Groups_sort_by_name_then_rep_id()
    {
        var seen = Seen((3, "Burn"), (1, "Burn"), (2, "Ablaze"));
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
        var seen = new Dictionary<int, (string name, bool isBuff, bool hidden)>
        {
            [1] = ("Poison", false, false),        // debuff
            [2] = ("Attack Up", true, false),       // buff
            [3] = ("Bleed", false, false),         // debuff
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
        var seen = new Dictionary<int, (string name, bool isBuff, bool hidden)>
        {
            [5]  = ("Burn", false, false),
            [7]  = ("Firmament Sub-Buff (7)", false, true),
            [8]  = ("Firmament Sub-Buff (7)", false, true),
            [9]  = ("Ablaze", false, false),
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

    [Fact]
    public void Ticked_groups_sort_first_then_hidden_then_name()
    {
        var seen = new Dictionary<int, (string name, bool isBuff, bool hidden)>
        {
            [1] = ("Ablaze", false, false),
            [2] = ("Burn", false, false),
            [3] = ("Cuisine", false, false),
            [4] = ("Cuisine", false, false),
            [7] = ("Sub-Buff (7)", false, true),
            [8] = ("Sub-Buff (8)", false, true),
        };
        var ticked = new HashSet<int> { 2, 8, 3, 4 };
        Plugin.FilterSeenEffects(seen, "", showHidden: true, wantBuff: false, ticked.Contains,
                                 out var g, out var n, out var total);
        Assert.Equal(5, n);
        Assert.Equal(5, total);
        // Ticked block (hidden first inside it), then the rest (hidden first), each by name.
        Assert.Equal(new[] { "Sub-Buff (8)", "Burn", "Cuisine", "Sub-Buff (7)", "Ablaze" }, System.Array.ConvertAll(g, x => x.Name));
        Assert.True(g[0].Ticked && g[1].Ticked && g[2].Ticked);
        Assert.False(g[3].Ticked || g[4].Ticked);
    }

    [Fact]
    public void Partly_ticked_name_group_does_not_sort_as_ticked()
    {
        var seen = Seen((10, "Cuisine"), (11, "Cuisine"), (20, "Burn"));
        Plugin.FilterSeenEffects(seen, "", showHidden: false, wantBuff: false, new HashSet<int> { 11 }.Contains,
                                 out var g, out _, out _);
        Assert.Equal("Burn", g[0].Name);       // nothing fully ticked → plain name order
        Assert.False(g[1].Ticked);
    }

    [Fact]
    public void Total_counts_eligible_groups_ignoring_the_search_text()
    {
        var seen = Seen((1, "Poison"), (2, "Burn"), (3, "Burn"), (4, ""), (5, "NoIcon"));
        Plugin.FilterSeenEffects(seen, "poi", showHidden: false, wantBuff: false, null, out _, out var n, out var total);
        Assert.Equal(1, n);
        Assert.Equal(3, total);   // Poison + Burn(×2) + NoIcon; the game-hidden row is not eligible
        Plugin.FilterSeenEffects(seen, "poi", showHidden: true, wantBuff: false, null, out _, out _, out var withHidden);
        Assert.Equal(4, withHidden);
    }
}
