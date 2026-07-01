using Stellar.CombatMeter.Replay;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public class PositionCodecTests
{
    [Fact]
    public void Quantize_RoundsToGrid()
    {
        Assert.Equal(123, PositionCodec.Quantize(12.34f, 0.1f));  // 12.34 / 0.1 = 123.4 -> 123
        Assert.Equal(0, PositionCodec.Quantize(-0.049f, 0.1f));   // -0.049 / 0.1 = -0.49 -> 0 (AwayFromZero rounds half; -0.49 < 0.5 so truncates to 0)
    }

    [Fact]
    public void DeltaEncode_FirstAbsolute_ThenDeltas()
    {
        var abs = new[] { 100, 103, 101, 110 };
        Assert.Equal(new[] { 100, 3, -2, 9 }, PositionCodec.DeltaEncode(abs));
    }

    [Fact]
    public void QuantizeYaw_WrapsTo_0_359()
    {
        Assert.Equal(0, PositionCodec.QuantizeYaw(360f));
        Assert.Equal(359, PositionCodec.QuantizeYaw(-1f));
        Assert.Equal(90, PositionCodec.QuantizeYaw(90.4f));
    }
}
