using System;
using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Domain.GameData;
using Stellar.Abstractions.Services;
using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>
/// Pins the new-install "Buffs &amp; Debuffs" defaults to the owner's MAIN client config (2026-09-29) and pins that a
/// saved config is never overridden or rewritten by loading. Expected values are written out here independently of
/// <see cref="DefaultTrackedEffects"/> (the buff list as a count + sum + sum-of-squares fingerprint of the owner's
/// 292 ids) so that editing the source list without meaning to fails the test.
/// </summary>
public class DefaultTrackedEffectsTests
{
    private sealed class RecordingConfigSection : IConfigSection
    {
        public readonly Dictionary<string, object?> Store = new();
        public int Writes;
        public T? Get<T>(string key, T? defaultValue)
            => Store.TryGetValue(key, out var v) && v is T t ? t : defaultValue;
        public void Set<T>(string key, T value) { Writes++; Store[key] = value; }
        public void Save() => Writes++;
        public void SaveQuiet() => Writes++;
        public void RemoveByPrefix(string prefix) => Writes++;
    }

    // Owner MAIN config 2026-09-29, combatmeter.status.debuff.track.
    private static readonly int[] OwnerDebuffs = { 2110049, 2110050, 2110056, 2110057 };
    // Fingerprint of the owner's combatmeter.status.buff.track (292 distinct ids, 2032011..2033189).
    private const int OwnerBuffCount = 292;
    private const long OwnerBuffSum = 593534379L;
    private const long OwnerBuffSumSq = 1206448900109723L;

    [Fact]
    public void Fresh_config_debuffs_tab_gets_the_owner_list_show_only_checked()
    {
        var sel = DefaultTrackedEffects.LoadDebuffs(new RecordingConfigSection());
        Assert.Equal(DebuffTrackMode.ShowOnlySelected, sel.Mode);
        Assert.False(sel.ShowHidden);
        Assert.Equal(OwnerDebuffs, sel.Selected.OrderBy(i => i).ToArray());
    }

    [Fact]
    public void Fresh_config_buffs_tab_gets_the_owner_list_show_only_checked()
    {
        var sel = DefaultTrackedEffects.LoadBuffs(new RecordingConfigSection());
        Assert.Equal(DebuffTrackMode.ShowOnlySelected, sel.Mode);
        Assert.False(sel.ShowHidden);
        Assert.Equal(OwnerBuffCount, sel.Selected.Count);
        Assert.Equal(OwnerBuffSum, sel.Selected.Sum(i => (long)i));
        Assert.Equal(OwnerBuffSumSq, sel.Selected.Sum(i => (long)i * i));
        Assert.True(sel.IsSelected(2032011));   // first Cuisine id
        Assert.True(sel.IsSelected(2033189));   // last Potion id
    }

    [Fact]
    public void Default_lists_are_distinct_and_disjoint()
    {
        var d = DefaultTrackedEffects.Debuffs;
        var b = DefaultTrackedEffects.Buffs;
        Assert.Equal(d.Length, d.Distinct().Count());
        Assert.Equal(b.Length, b.Distinct().Count());
        Assert.Empty(d.Intersect(b));
    }

    [Fact]
    public void Loading_a_fresh_config_writes_nothing_back()
    {
        // Defaults are resolved on read, never persisted — so a config only ever holds what the user chose.
        var cfg = new RecordingConfigSection();
        DefaultTrackedEffects.LoadDebuffs(cfg);
        DefaultTrackedEffects.LoadBuffs(cfg);
        Assert.Equal(0, cfg.Writes);
        Assert.Empty(cfg.Store);
    }

    [Fact]
    public void Existing_saved_config_is_loaded_exactly_and_untouched()
    {
        var cfg = new RecordingConfigSection();
        cfg.Store["status.debuff.track"] = new[] { 2110049 };
        cfg.Store["status.debuff.mode"] = (int)DebuffTrackMode.ShowAllExceptSelected;
        cfg.Store["status.debuff.showHidden"] = true;
        cfg.Store["status.buff.track"] = Array.Empty<int>();   // user unchecked everything — must stay empty
        cfg.Store["status.buff.mode"] = (int)DebuffTrackMode.ShowOnlySelected;
        cfg.Store["status.buff.showHidden"] = false;
        var before = cfg.Store.ToDictionary(kv => kv.Key, kv => kv.Value);

        var deb = DefaultTrackedEffects.LoadDebuffs(cfg);
        var buf = DefaultTrackedEffects.LoadBuffs(cfg);

        Assert.Equal(DebuffTrackMode.ShowAllExceptSelected, deb.Mode);
        Assert.True(deb.ShowHidden);
        Assert.Equal(new[] { 2110049 }, deb.Selected.ToArray());
        Assert.Empty(buf.Selected);
        Assert.Equal(DebuffTrackMode.ShowOnlySelected, buf.Mode);
        Assert.Equal(0, cfg.Writes);
        Assert.Equal(before, cfg.Store);
    }

    [Fact]
    public void Fresh_defaults_in_the_strip_show_only_tracked_ids()
    {
        var cfg = new RecordingConfigSection();
        var deb = DefaultTrackedEffects.LoadDebuffs(cfg);
        var buf = DefaultTrackedEffects.LoadBuffs(cfg);
        var table = new Dictionary<int, BuffInfo>
        {
            [2110057] = new BuffInfo(2110057, "Weakened: Wish Sealed", "", "icon", default, true, 0),  // tracked debuff
            [2110001] = new BuffInfo(2110001, "Some Debuff", "", "icon", default, true, 0),            // untracked debuff
            [2033011] = new BuffInfo(2033011, "Potion", "", "icon", default, false, 0),                // tracked buff
            [2032011] = new BuffInfo(2032011, "Cuisine", "", "icon", default, false, 0),               // tracked buff
            [2100001] = new BuffInfo(2100001, "Some Buff", "", "icon", default, false, 0),             // untracked buff
        };
        Func<int, BuffInfo?> get = id => table.TryGetValue(id, out var b) ? b : (BuffInfo?)null;
        var live = new[]
        {
            new ActiveBuff(1, 2110057, 1, EntityId.None, 1, 0, 10, 1000),
            new ActiveBuff(2, 2110001, 1, EntityId.None, 1, 0, 20, 1000),
            new ActiveBuff(3, 2033011, 1, EntityId.None, 1, 0, 30, 1000),
            new ActiveBuff(4, 2032011, 1, EntityId.None, 1, 0, 40, 1000),
            new ActiveBuff(5, 2100001, 1, EntityId.None, 1, 0, 50, 1000),
        };

        var shown = DebuffStrip.BuildAll(live, get, deb, buf, nowMs: 0).Select(e => e.BaseId).ToArray();

        Assert.Equal(new[] { 2110057, 2033011, 2032011 }, shown);
    }
}
