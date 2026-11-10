using Xunit;

namespace Stellar.CombatMeter.Tests;

// Raid perf 2026-11-10: displayed fractions snap to ~1 px steps so sub-pixel changes don't re-mesh uGUI graphics.
public class DisplayQuantizeTests
{
    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(-0.2f, 0f)]
    [InlineData(float.NaN, 0f)]
    [InlineData(1f, 1f)]
    [InlineData(1.4f, 1f)]
    [InlineData(0.5f, 0.5f)]
    public void Fraction_clamps_and_keeps_exact_steps(float v, float expected)
        => Assert.Equal(expected, DisplayQuantize.Fraction(v, DisplayQuantize.BarSteps));

    [Fact]
    public void Fraction_collapses_subpixel_changes_to_one_value()
        => Assert.Equal(DisplayQuantize.Fraction(0.50001f, 320), DisplayQuantize.Fraction(0.50080f, 320));

    [Fact]
    public void Fraction_never_rounds_a_live_sliver_to_zero()
        => Assert.Equal(1f / 320, DisplayQuantize.Fraction(0.0001f, 320));

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(1f, 1f)]
    [InlineData(0.9995f, 0.9995f)]     // framework's "permanent / just applied" (>= 0.999) passes through
    [InlineData(0.99f, 0.96875f)]      // floors — never fuller than the real time left
    [InlineData(0.5f, 0.5f)]
    [InlineData(0.01f, 0f)]
    public void Remain_floors_to_pixel_steps(float v, float expected)
        => Assert.Equal(expected, DisplayQuantize.Remain(v));
}
