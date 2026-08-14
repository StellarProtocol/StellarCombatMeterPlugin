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
}
