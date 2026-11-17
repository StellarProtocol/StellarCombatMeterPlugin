using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>
/// The header status timer ("● m:ss in combat") is a wall clock for the open encounter — it keeps ticking
/// between hits instead of freezing at the last hit (user report 2026-11-17). Pins the pure seam
/// <c>Plugin.HeaderElapsedMs</c>; the DPS-row divisor (last hit − start) is deliberately a different helper.
/// </summary>
public class HeaderElapsedTests
{
    private const long Start = 1_700_000_000_000;

    [Fact]
    public void KeepsCountingAfterTheLastHit()
        => Assert.Equal(30_000, Plugin.HeaderElapsedMs(true, Start, Start + 10_000, Start + 30_000));

    [Fact]
    public void NeverBelowTheLastHitSpan_WhenServerNowLagsOrIsUnsynced()
    {
        Assert.Equal(10_000, Plugin.HeaderElapsedMs(true, Start, Start + 10_000, Start + 9_000));
        Assert.Equal(10_000, Plugin.HeaderElapsedMs(true, Start, Start + 10_000, 0));
    }

    [Fact]
    public void ZeroWhenNoEncounterIsOpen()
    {
        Assert.Equal(0, Plugin.HeaderElapsedMs(false, Start, Start + 10_000, Start + 30_000));
        Assert.Equal(0, Plugin.HeaderElapsedMs(true, 0, 0, Start));
    }
}
