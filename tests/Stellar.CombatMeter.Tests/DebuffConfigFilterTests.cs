using System.Collections.Generic;
using Xunit;

namespace Stellar.CombatMeter.Tests;

// Pins Plugin.FilterSeenDebuffs — the pure snapshot/eligibility/search/sort seam behind the debuff
// config panel's search box + virtual list (Plugin.DebuffConfig.cs). Internal, so this test project
// must see [InternalsVisibleTo] (already granted for the other Plugin.* internal seams).
public class DebuffConfigFilterTests
{
    private static Dictionary<int, (string name, bool hasIcon)> Seen(
        params (int id, string name, bool hasIcon)[] rows)
    {
        var d = new Dictionary<int, (string name, bool hasIcon)>();
        foreach (var r in rows) d[r.id] = (r.name, r.hasIcon);
        return d;
    }

    [Fact]
    public void No_icon_is_never_eligible_even_with_ShowHidden()
    {
        var seen = Seen((1, "Poison", false));
        Plugin.FilterSeenDebuffs(seen, "", showHidden: true, out var ids, out _, out var count);
        Assert.Equal(0, count);
        Assert.Empty(ids);
    }

    [Fact]
    public void Unnamed_iconed_debuff_is_hidden_unless_ShowHidden()
    {
        var seen = Seen((1, "", true));
        Plugin.FilterSeenDebuffs(seen, "", showHidden: false, out _, out _, out var hiddenCount);
        Assert.Equal(0, hiddenCount);

        Plugin.FilterSeenDebuffs(seen, "", showHidden: true, out var ids, out var names, out var shownCount);
        Assert.Equal(1, shownCount);
        Assert.Equal(1, ids[0]);
        Assert.Equal("", names[0]);
    }

    [Fact]
    public void Search_matches_name_case_insensitively()
    {
        var seen = Seen((1, "Poison", true), (2, "Burn", true));
        Plugin.FilterSeenDebuffs(seen, "poi", showHidden: false, out var ids, out _, out var count);
        Assert.Equal(1, count);
        Assert.Equal(1, ids[0]);
    }

    [Fact]
    public void Search_matches_id_as_a_substring()
    {
        var seen = Seen((12345, "Poison", true), (999, "Burn", true));
        Plugin.FilterSeenDebuffs(seen, "234", showHidden: false, out var ids, out _, out var count);
        Assert.Equal(1, count);
        Assert.Equal(12345, ids[0]);
    }

    [Fact]
    public void Empty_query_keeps_every_eligible_row()
    {
        var seen = Seen((1, "Poison", true), (2, "Burn", true), (3, "NoIcon", false));
        Plugin.FilterSeenDebuffs(seen, "", showHidden: false, out _, out _, out var count);
        Assert.Equal(2, count);
    }

    [Fact]
    public void Results_sort_by_name_then_id()
    {
        var seen = Seen((3, "Burn", true), (1, "Burn", true), (2, "Ablaze", true));
        Plugin.FilterSeenDebuffs(seen, "", showHidden: false, out var ids, out var names, out var count);
        Assert.Equal(3, count);
        Assert.Equal(new[] { "Ablaze", "Burn", "Burn" }, names);
        Assert.Equal(new[] { 2, 1, 3 }, ids);   // "Burn" ties (1 vs 3) break by id ascending
    }

    [Fact]
    public void Empty_catalog_yields_empty_result()
    {
        Plugin.FilterSeenDebuffs(Seen(), "anything", showHidden: true, out var ids, out var names, out var count);
        Assert.Equal(0, count);
        Assert.Empty(ids);
        Assert.Empty(names);
    }
}
