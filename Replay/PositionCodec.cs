using System;

namespace Stellar.CombatMeter.Replay;

/// <summary>Pure quantize + delta helpers for the replay position track. No allocation on Quantize.</summary>
internal static class PositionCodec
{
    /// <summary>Quantizes a world coordinate to an integer grid cell. No heap allocation.</summary>
    public static int Quantize(float world, float scale)
        => (int)MathF.Round(world / scale, MidpointRounding.AwayFromZero);

    /// <summary>Quantizes a yaw angle in degrees to the nearest integer, wrapped to [0, 359].</summary>
    public static int QuantizeYaw(float yawDegrees)
    {
        var d = (int)MathF.Round(yawDegrees, MidpointRounding.AwayFromZero) % 360;
        return d < 0 ? d + 360 : d;
    }

    /// <summary>Delta-encodes an absolute coordinate array: first element is absolute, rest are deltas.</summary>
    public static int[] DeltaEncode(int[] absolute)
    {
        if (absolute.Length == 0) return Array.Empty<int>();
        var outp = new int[absolute.Length];
        outp[0] = absolute[0];
        for (var i = 1; i < absolute.Length; i++) outp[i] = absolute[i] - absolute[i - 1];
        return outp;
    }
}
