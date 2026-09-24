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
}
