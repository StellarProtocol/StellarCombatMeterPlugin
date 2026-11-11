using System;

namespace Stellar.CombatMeter;

/// <summary>
/// Snaps the meter's displayed fractions (bar length, HP spine, debuff time-left foot bar) to steps no finer than
/// about one pixel. Raid perf (2026-11-10 raid log): every changed fraction re-meshes its uGUI graphic, and in a
/// 20-player fight the meter's debuff foot bars were the TOP changed graphic every frame
/// (<c>MeterRow/DebuffCol/Debuffs/D/Inset/Fill</c>) — sub-pixel moves nobody can see. The framework already skips a
/// rebind when the value is unchanged, so making sub-pixel changes collapse to the SAME value is what removes the
/// redraw. Display only — never applied to captured/archived numbers. Pure, pinned by DisplayQuantizeTests.
/// </summary>
internal static class DisplayQuantize
{
    /// <summary>Horizontal bars (main bar, shield overlay). ~1 px at the widest list layout.</summary>
    internal const int BarSteps = 320;

    /// <summary>Debuff/buff foot bar: the cell is at most ~32 px wide, so 1/32 is one pixel.</summary>
    internal const int RemainSteps = 32;

    /// <summary>Rounds <paramref name="v"/> (clamped to 0..1) to 1/<paramref name="steps"/>. A non-zero value
    /// never rounds down to 0 — an almost-dead player's HP sliver or a just-started bar stays visible.</summary>
    internal static float Fraction(float v, int steps)
    {
        if (!(v > 0f)) return 0f;          // also folds NaN to 0
        if (v >= 1f) return 1f;
        var q = MathF.Round(v * steps) / steps;
        return q > 0f ? q : 1f / steps;
    }

    /// <summary>Time-left fraction for a debuff/buff tile. Floors (a bar must never look FULLER than the real
    /// time left). Values at or above the framework's "permanent / just applied" threshold (0.999 — it hides the
    /// bar) pass through unchanged so that rule still sees them.</summary>
    internal static float Remain(float v)
    {
        if (!(v > 0f)) return 0f;
        if (v >= 0.999f) return v;
        return MathF.Floor(v * RemainSteps) / RemainSteps;
    }
}
