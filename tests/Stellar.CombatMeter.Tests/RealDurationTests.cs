using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>
/// Owner report 2026-07-28: history rows showed `0s` for archives that plainly covered real time
/// (5s and 8s segments on run 890357114281656320). `CombatDurationMs` is the DAMAGE-HIT SPAN —
/// `FirstHitMs`/`LastHitMs` are written only in the damage handler (Plugin.Capture.cs), so healing and
/// damage-taken never move them. A heal-only tail therefore has a legitimate span of 0 while several
/// seconds of wall-clock elapsed.
///
/// Owner ruling: keep the combat-span logic exactly as it is (DPS divides by it) and ALSO surface the
/// real elapsed duration — displayed as `8.3s (0s combat)`.
///
/// Real duration comes from fields already persisted on every entry: `ArchivedAtMs - EnteredAtMs`
/// (EnteredAtMs is `_combatStartMs`). Verified non-zero on all 50 entries of the owner's live history,
/// so this works retroactively with no schema change and no migration.
/// </summary>
public class RealDurationTests
{
    // ---- RealDurationMs ----------------------------------------------------

    [Fact]
    public void RealDurationMs_IsTheElapsedSpanBetweenCombatStartAndArchive()
    {
        // Measured from the owner's history, run 890357114281656320: a `scene` archive whose damage
        // span was 0 but which covered 8.299s of real time.
        Assert.Equal(8299, Plugin.RealDurationMs(enteredAtMs: 1785245771753, archivedAtMs: 1785245780052));
    }

    [Fact]
    public void RealDurationMs_ZeroWhenNoCombatStartWasEverRecorded()
    {
        // Guard: `arch - 0` would report ~56 years. Absent baseline must read as unknown, not enormous.
        Assert.Equal(0, Plugin.RealDurationMs(enteredAtMs: 0, archivedAtMs: 1785245780052));
    }

    [Fact]
    public void RealDurationMs_ClampsAServerClockThatWentBackwards()
    {
        Assert.Equal(0, Plugin.RealDurationMs(enteredAtMs: 1785245780052, archivedAtMs: 1785245771753));
    }

    // ---- FormatRowDuration ------------------------------------------------

    [Fact]
    public void FormatRowDuration_ShowsTheCombatSpanInParenthesesWhenItDiffers()
    {
        // The owner's chosen format, from their own example: `8.3s (0s combat)`.
        Assert.Equal("8.3s (0s combat)", Plugin.FormatRowDuration(realMs: 8299, combatMs: 0));
        Assert.Equal("5.0s (0s combat)", Plugin.FormatRowDuration(realMs: 5048, combatMs: 0));
    }

    [Fact]
    public void FormatRowDuration_OmitsTheParentheticalWhenBothLandOnTheSameSecond()
    {
        // Ordinary fights must not repeat their own number. Compared on values, not rendered strings,
        // so the tenths and m/s formats can't disagree about equality.
        Assert.Equal("1m 0s", Plugin.FormatRowDuration(realMs: 60_000, combatMs: 60_000));
        Assert.Equal("3.0s", Plugin.FormatRowDuration(realMs: 3_000, combatMs: 3_000));
        // Sub-second drift between the two clocks is not worth a suffix either.
        Assert.Equal("1m 0s", Plugin.FormatRowDuration(realMs: 60_049, combatMs: 60_000));
    }

    [Fact]
    public void FormatRowDuration_UsesOneDecimalUnderAMinuteAndMinutesAbove()
    {
        Assert.Equal("59.9s", Plugin.FormatRowDuration(realMs: 59_900, combatMs: 59_900));
        Assert.Equal("1m 0s", Plugin.FormatRowDuration(realMs: 60_049, combatMs: 60_049));
        Assert.Equal("5m 11s (5m 8s combat)", Plugin.FormatRowDuration(realMs: 311_194, combatMs: 308_710));
    }

    [Fact]
    public void FormatRowDuration_NeverRendersANegativeOrAbsurdValue()
    {
        Assert.Equal("0.0s", Plugin.FormatRowDuration(realMs: 0, combatMs: 0));
        // A combat span longer than the real span (shouldn't happen, but must not print nonsense).
        Assert.Equal("1.0s (2s combat)", Plugin.FormatRowDuration(realMs: 1_000, combatMs: 2_000));
        Assert.Equal("0.0s", Plugin.FormatRowDuration(realMs: -5, combatMs: -5));
    }

    // ---- ChartExtentSeconds ------------------------------------------------

    [Fact]
    public void ChartExtentSeconds_SpansTheRealDurationNotTheDamageSpan()
    {
        // Owner ruling 2026-07-28: if the row reports real duration, the graph must cover the whole
        // duration. The series are bucketed from _combatStartMs, so the domain genuinely runs to the
        // archive moment — anchoring on the damage span clipped the chart short of its own data.
        Assert.Equal(8.299f, Plugin.ChartExtentSeconds(realMs: 8299, combatMs: 0), 3);
        Assert.Equal(311.194f, Plugin.ChartExtentSeconds(realMs: 311_194, combatMs: 308_710), 3);
    }

    [Fact]
    public void ChartExtentSeconds_FallsBackToTheCombatSpanWhenRealIsUnknown()
    {
        // Never 0-wide: an entry with no recorded combat start still gets a usable axis.
        Assert.Equal(308.710f, Plugin.ChartExtentSeconds(realMs: 0, combatMs: 308_710), 3);
    }

    [Fact]
    public void ChartExtentSeconds_ZeroOnlyWhenBothAreZero()
        => Assert.Equal(0f, Plugin.ChartExtentSeconds(realMs: 0, combatMs: 0));

    [Fact]
    public void FormatRowDuration_DoesNotChangeTheCombatSpanItself()
    {
        // DPS divides by CombatDurationMs, so the combat number must be reported verbatim — the fix is
        // display-only. A 0 combat span still reads "0s combat", never rounded up or hidden.
        Assert.Contains("(0s combat)", Plugin.FormatRowDuration(realMs: 8_299, combatMs: 0));
    }
}
