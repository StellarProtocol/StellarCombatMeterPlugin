using System.Globalization;

namespace Stellar.CombatMeter;

/// <summary>
/// Countdown text for a status-effect tile, in the same format as CooldownBar's tile label (owner 2026-10-05):
/// over 999 s → whole minutes ("25m"), 10 s and up → whole seconds ("48s"), under 10 s → one decimal ("9.4s").
/// Empty once expired. Unity-free + testable.
/// </summary>
public static class CountdownLabel
{
    /// <summary>Formats <paramref name="remainingMs"/>; "" when it is 0 or less.</summary>
    public static string Format(long remainingMs)
    {
        if (remainingMs <= 0) return "";
        float secs = remainingMs / 1000f;
        return secs > 999f ? $"{(int)(secs / 60f)}m"
             : secs >= 10f ? $"{(int)secs}s"
             : secs.ToString("F1", CultureInfo.InvariantCulture) + "s";
    }
}
