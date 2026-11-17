using Xunit;

namespace Stellar.CombatMeter.Tests;

// Row context-menu icon size pick (Plugin.RowMenuIcons.cs): the 16-unit slot × the UI scale (screen px per canvas unit)
// → the exact pre-drawn size, else the next larger drawing, else the largest. Never a smaller drawing scaled up.
public class RowMenuIconSizeTests
{
    private static readonly int[] Sizes = { 16, 24, 32 };

    [Theory]
    [InlineData(12f, 16)]   // 1080p (scale 0.75): no 12 drawing → next larger
    [InlineData(16f, 16)]   // 1440p reference (scale 1): exact
    [InlineData(15.97f, 16)] // float noise still counts as exact
    [InlineData(21.33f, 24)] // 1920-wide canvas at 2560 px (scale 1.33): next larger
    [InlineData(24f, 24)]   // 4K (scale 1.5): exact
    [InlineData(32f, 32)]   // scale 2: exact
    [InlineData(48f, 32)]   // past the largest → the largest
    public void Picks_ExactElseNextLarger(float targetPx, int expected)
        => Assert.Equal(expected, Sizes[Plugin.PickIconSizeIndex(targetPx, Sizes)]);
}
