using System;
using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Services;
using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>
/// The Illusion-Breaking Strength row-element toggle (owner 2026-08-15). Parallels the existing
/// AbilityScore toggle: defaults OFF, round-trips through the per-mode config prefix, and resolves
/// independent of the width-collapse guard (it is a compact numeric cell, not a wide one).
/// </summary>
public class MeterElementTogglesTests
{
    private sealed class FakeConfigSection : IConfigSection
    {
        private readonly Dictionary<string, object?> _store = new();
        public T? Get<T>(string key, T? defaultValue)
            => _store.TryGetValue(key, out var v) && v is T t ? t : defaultValue;
        public void Set<T>(string key, T value) => _store[key] = value;
        public void Save() { }
        public void SaveQuiet() { }
        public void RemoveByPrefix(string prefix)
        {
            foreach (var key in _store.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                _store.Remove(key);
        }
    }

    [Fact]
    public void IllusionBreak_defaults_off()
    {
        Assert.False(MeterElementToggles.Defaults().IllusionBreak);
        Assert.False(MeterElementToggles.Raid20Defaults().IllusionBreak);
    }

    [Fact]
    public void IllusionBreak_round_trips_through_the_config_prefix()
    {
        var cfg = new FakeConfigSection();
        var t = MeterElementToggles.Defaults();
        t.IllusionBreak = true;
        t.Save(cfg, "list");

        var loaded = MeterElementToggles.Load(cfg, "list", MeterElementToggles.Defaults());
        Assert.True(loaded.IllusionBreak);

        // A different mode's prefix is untouched (per-mode keys).
        var other = MeterElementToggles.Load(cfg, "party20", MeterElementToggles.Raid20Defaults());
        Assert.False(other.IllusionBreak);
    }

    [Fact]
    public void IllusionBreak_resolves_regardless_of_the_width_collapse_guard()
    {
        var t = MeterElementToggles.Defaults();
        t.IllusionBreak = true;
        // Even collapsed at a tiny width, the compact numeric cell stays on (mirrors AbilityScore).
        Assert.True(t.Resolve(collapse: true, widthNow: 1f).IllusionBreak);
        Assert.True(t.Resolve(collapse: false, widthNow: 9999f).IllusionBreak);

        t.IllusionBreak = false;
        Assert.False(t.Resolve(collapse: false, widthNow: 9999f).IllusionBreak);
    }

    // ----- Bar colour (Appearance → Bars, 2.10.0) -----

    // Owner-visible guarantee: nobody's meter changes until they opt in (design 2026-09-09 § 4).
    [Fact]
    public void BarColor_defaults_to_Role_in_every_layout()
    {
        Assert.Equal(BarColorMode.Role, MeterElementToggles.Defaults().BarColor);
        Assert.Equal(BarColorMode.Role, MeterElementToggles.Raid20Defaults().BarColor);
    }

    [Fact]
    public void BarColor_round_trips_through_the_config_prefix()
    {
        var cfg = new FakeConfigSection();
        var t = MeterElementToggles.Defaults();
        t.BarColor = BarColorMode.Class;
        t.Save(cfg, "list");

        var loaded = MeterElementToggles.Load(cfg, "list", MeterElementToggles.Defaults());
        Assert.Equal(BarColorMode.Class, loaded.BarColor);

        // Per layout: writing "list" must not tint party20's own setting.
        var other = MeterElementToggles.Load(cfg, "party20", MeterElementToggles.Raid20Defaults());
        Assert.Equal(BarColorMode.Role, other.BarColor);
    }

    // Rollback safety: a config written by ≤ 2.9.1 (or by a build that rolled back and re-saved without
    // the key) has no "bar.color" at all — that must read as the default, never as Class.
    [Fact]
    public void A_config_without_the_key_yields_the_default()
    {
        var cfg = new FakeConfigSection();
        cfg.Set("list.show.rank", true);   // some other key exists, "list.bar.color" does not

        var loaded = MeterElementToggles.Load(cfg, "list", MeterElementToggles.Defaults());
        Assert.Equal(BarColorMode.Role, loaded.BarColor);
    }

    [Fact]
    public void Save_writes_BarColor_as_the_enum_ordinal_under_bar_color()
    {
        var cfg = new FakeConfigSection();
        var t = MeterElementToggles.Defaults();
        t.BarColor = BarColorMode.Class;
        t.Save(cfg, "party5");

        Assert.Equal(1, cfg.Get<int>("party5.bar.color", -1));
    }
}
