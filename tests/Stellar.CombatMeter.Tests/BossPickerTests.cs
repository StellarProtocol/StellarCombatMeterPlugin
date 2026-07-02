using Stellar.CombatMeter.Replay;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public sealed class BossPickerTests
{
    // candidates: (entityId, isBoss, maxHp). Picks the isBoss entity; ties broken by maxHp.
    [Fact]
    public void PicksTaggedBoss()
        => Assert.Equal(7L, BossPicker.Pick(new[] { (1L, false, 100L), (7L, true, 5_000_000L), (3L, false, 200L) }));

    [Fact]
    public void MultipleBosses_PicksHighestMaxHp()
        => Assert.Equal(9L, BossPicker.Pick(new[] { (7L, true, 1_000L), (9L, true, 9_000L) }));

    [Fact]
    public void NoBoss_ReturnsNull()
        => Assert.Null(BossPicker.Pick(new[] { (1L, false, 100L), (3L, false, 200L) }));
}
