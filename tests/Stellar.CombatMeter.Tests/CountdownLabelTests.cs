using Xunit;

namespace Stellar.CombatMeter.Tests;

// The Buffs & Debuffs click-tooltip countdown reads like CooldownBar's tile label (owner 2026-10-05: "1519s" was
// hard to read for a 30-min potion — show minutes like CooldownBar's "25m").
public class CountdownLabelTests
{
    [Theory]
    [InlineData(1_519_000, "25m")]   // the owner's screenshot case
    [InlineData(1_000_000, "16m")]
    [InlineData(999_500, "16m")]     // just over CooldownBar's 999 s switch
    [InlineData(999_000, "999s")]
    [InlineData(48_000, "48s")]
    [InlineData(10_000, "10s")]
    [InlineData(9_400, "9.4s")]
    [InlineData(500, "0.5s")]
    public void Matches_CooldownBar_tile_label(long remainingMs, string expected)
        => Assert.Equal(expected, CountdownLabel.Format(remainingMs));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Expired_is_empty(long remainingMs)
        => Assert.Equal("", CountdownLabel.Format(remainingMs));
}
