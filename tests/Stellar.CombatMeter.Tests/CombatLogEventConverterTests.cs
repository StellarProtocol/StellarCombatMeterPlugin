using Stellar.Abstractions.Domain;
using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public sealed class CombatLogEventConverterTests
{
    [Fact]
    public void BuffChanged_maps_firer_and_source()
    {
        var ev = new CombatEvent.BuffChanged(1_000, new EntityId(640), 80, 2203572, BuffChangeKind.Applied, 1, 1, 5000,
            FirerId: new EntityId(0x0000_0002_0000_0280), SourceKind: 0, SourceId: 2327);
        var b = Assert.IsType<BuffEvent>(CombatLogEventConverter.Convert(ev));
        Assert.Equal("8589935232", b.Src);      // 0x0000_0002_0000_0280 as decimal
        Assert.Equal(0, b.SrcKind);
        Assert.Equal(2327, b.SrcId);
        Assert.Equal("applied", b.Kind);
    }

    [Fact]
    public void BuffChanged_without_firer_writes_zero()
    {
        var ev = new CombatEvent.BuffChanged(1_000, new EntityId(640), 80, 1, BuffChangeKind.Removed, 0, 1, 0);
        var b = Assert.IsType<BuffEvent>(CombatLogEventConverter.Convert(ev));
        Assert.Equal("0", b.Src);
    }
}
