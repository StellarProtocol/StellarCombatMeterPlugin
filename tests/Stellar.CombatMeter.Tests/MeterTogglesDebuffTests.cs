using Xunit;

namespace Stellar.CombatMeter.Tests;

public class MeterTogglesDebuffTests
{
    [Fact]
    public void Debuffs_defaults_on()
        => Assert.True(MeterElementToggles.Defaults().Debuffs);

    [Fact]
    public void Debuffs_survives_resolve()
    {
        var t = MeterElementToggles.Defaults();
        t.Debuffs = true;
        Assert.True(t.Resolve(collapse: false, widthNow: 1000f).Debuffs);
        t.Debuffs = false;
        Assert.False(t.Resolve(collapse: false, widthNow: 1000f).Debuffs);
    }

    [Fact]
    public void Debuffs_respect_the_toggle_in_both_list_and_party()
    {
        // Owner 2026-09-24: buffs & debuffs are supported in List too, gated by the per-mode toggle (no longer
        // force-hidden on collapse). List DEFAULTS the toggle OFF via ListDefaults; party modes default ON.
        var t = MeterElementToggles.Defaults();
        t.Debuffs = true;
        Assert.True(t.Resolve(collapse: true,  widthNow: 2000f).Debuffs);   // List, toggle on
        Assert.True(t.Resolve(collapse: false, widthNow: 2000f).Debuffs);   // party-focus, toggle on
        t.Debuffs = false;
        Assert.False(t.Resolve(collapse: true,  widthNow: 2000f).Debuffs);  // List, toggle off
    }

    [Fact]
    public void List_defaults_the_block_off_party_on()
    {
        Assert.False(MeterElementToggles.ListDefaults().Debuffs);
        Assert.True(MeterElementToggles.Defaults().Debuffs);
    }
}
