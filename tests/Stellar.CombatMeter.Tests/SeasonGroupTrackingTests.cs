using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain.GameData;
using Stellar.Abstractions.Services;
using Xunit;

namespace Stellar.CombatMeter.Tests;

// Owner report 2026-11-20: Season-4 Potion/Cuisine buffs never showed in the party Buffs strip. The selection only
// held the buff ids that existed when the groups were ticked; Season 4 added 54 Potion + 24 Cuisine buff ids. A name
// group ticked as a whole now keeps every member the game adds later — for new installs, for configs saved before this
// existed (migrated from the exact Season-3 memberships), and in any client language (the group is a representative
// id whose game name defines it, never an English string).
public class SeasonGroupTrackingTests
{
    private sealed class FakeConfigSection : IConfigSection
    {
        public readonly Dictionary<string, object?> Store = new();
        public T? Get<T>(string key, T? defaultValue) => Store.TryGetValue(key, out var v) && v is T t ? t : defaultValue;
        public void Set<T>(string key, T value) => Store[key] = value;
        public void Save() { }
        public void SaveQuiet() { }
        public void RemoveByPrefix(string prefix) { }
    }

    private const int S3Potion = 2033011, S3Cuisine = 2032011, S4Potion = 2033211, S4Cuisine = 2032311, Other = 2099001;

    private static Func<int, BuffInfo?> Table(string potion = "Potion", string cuisine = "Cuisine")
    {
        var t = new Dictionary<int, BuffInfo>();
        foreach (var id in DefaultTrackedEffects.Buffs)
            t[id] = new BuffInfo(id, id < 2033000 ? cuisine : potion, "", "icon", default, false, 0);
        t[S4Potion]  = new BuffInfo(S4Potion, potion, "", "icon", default, false, 0);
        t[S4Cuisine] = new BuffInfo(S4Cuisine, cuisine, "", "icon", default, false, 0);
        t[Other]     = new BuffInfo(Other, "Haste", "", "icon", default, false, 0);
        return id => t.TryGetValue(id, out var b) ? b : null;
    }

    private static bool Shows(DebuffSelection sel, int id, Func<int, BuffInfo?> get)
        => sel.ShouldShow(id, hasName: true, get(id)?.Name, get);

    [Fact]
    public void New_install_shows_a_new_seasons_Potion_and_Cuisine()
    {
        var get = Table();
        var sel = DefaultTrackedEffects.LoadBuffs(new FakeConfigSection());
        Assert.True(Shows(sel, S3Potion, get));
        Assert.True(Shows(sel, S4Potion, get));     // never in the id list
        Assert.True(Shows(sel, S4Cuisine, get));
        Assert.False(Shows(sel, Other, get));
    }

    [Fact]
    public void A_config_saved_before_groups_existed_is_migrated_when_the_whole_group_was_ticked()
    {
        var get = Table();
        var cfg = new FakeConfigSection();
        cfg.Store["status.buff.track"] = DefaultTrackedEffects.Buffs;     // the owner's saved Season-3 list
        var sel = DefaultTrackedEffects.LoadBuffs(cfg);
        Assert.True(Shows(sel, S4Potion, get));
        Assert.True(Shows(sel, S4Cuisine, get));
    }

    [Fact]
    public void A_partly_ticked_group_is_left_exactly_as_the_player_set_it()
    {
        var get = Table();
        var cfg = new FakeConfigSection();
        cfg.Store["status.buff.track"] = new[] { S3Potion };              // one potion only, no cuisine
        var sel = DefaultTrackedEffects.LoadBuffs(cfg);
        Assert.True(Shows(sel, S3Potion, get));
        Assert.False(Shows(sel, S4Potion, get));
        Assert.False(Shows(sel, S4Cuisine, get));
    }

    [Fact]
    public void Groups_match_by_the_clients_own_name_not_an_English_string()
    {
        var get = Table(potion: "ポーション", cuisine: "料理");
        var sel = DefaultTrackedEffects.LoadBuffs(new FakeConfigSection());
        Assert.True(Shows(sel, S4Potion, get));
        Assert.True(Shows(sel, S4Cuisine, get));
    }

    [Fact]
    public void Saved_groups_round_trip_and_unticking_a_group_drops_it()
    {
        var get = Table();
        var cfg = new FakeConfigSection();
        var sel = DefaultTrackedEffects.LoadBuffs(cfg);
        sel.Save(cfg, DefaultTrackedEffects.BuffPrefix);
        var again = DefaultTrackedEffects.LoadBuffs(cfg);
        Assert.Equal(new HashSet<int>(sel.GroupReps), new HashSet<int>(again.GroupReps));

        again.SetGroup(S4Cuisine, "Cuisine", on: false, get);              // untick via ANOTHER member of the group
        foreach (var id in DefaultTrackedEffects.Buffs) if (id < 2033000) again.SetSelected(id, false);
        Assert.False(Shows(again, S4Cuisine, get));
        Assert.True(Shows(again, S4Potion, get));
    }

    [Fact]
    public void In_show_all_except_mode_a_ticked_group_hides_its_new_members_too()
    {
        var get = Table();
        var sel = new DebuffSelection { Mode = DebuffTrackMode.ShowAllExceptSelected };
        sel.SetGroup(S3Potion, "Potion", on: true, get);
        Assert.False(Shows(sel, S4Potion, get));
        Assert.True(Shows(sel, Other, get));
    }
}
